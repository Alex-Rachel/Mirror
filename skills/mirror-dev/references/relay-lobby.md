# RelayLobby 模式

本参考描述仓库当前 `Mirror.Relay` 实现的真实契约。它适用于新增 RelayLobby、修复 Relay 连接、为旧项目保留兼容路径，以及审查大厅和 Transport 的边界。若目标项目安装的是不同版本，先对照目标项目实际源码和程序集；本文件不能替代版本核对。

## 架构边界

RelayLobby 不是把 Mirror 服务器搬到中继上，而是三层协作：

```text
Unity UI / 游戏入口
        │
        ▼
RelayNetworkManager（唯一会话所有者）
   ├─ HTTP 控制面：创建、查询、加入、更新、开始、离开、踢出、关闭
   └─ RelayKcpTransport：UDP Relay 数据面 + KCP
        │
        ▼
Mirror Host/Client/Server
   └─ 游戏玩法、权威状态、Spawn、SyncVar、Command/Rpc
```

- **控制面**返回房间快照和凭据，负责“谁能进房间”。
- **数据面**认证并转发 KCP 数据报，负责“谁能交换 Mirror 数据”。
- **Mirror 主机**仍然是玩法权威。Relay 只转发数据，不执行 Unity 场景或游戏规则。
- Host 进程中的本地玩家仍走 Mirror `LocalConnection`；远端玩家通过同一个权威 Host 的 Relay KCP 连接。

不要把 HTTP 房间成员列表当成 Mirror 连接列表，也不要把 Relay token 当成应用层加密。需要保密时，仍要增加应用层或加密 Transport。

## 组件职责

| 组件 | 责任 | 不应承担 |
| --- | --- | --- |
| `RelayNetworkManager` | 串行化会话操作、分配/应用凭据、启动/停止 Mirror、轮询房间、错误与回滚 | 玩法规则、UI 布局、保存 token 到玩家对象 |
| `RelayKcpTransport` | 将 Relay 凭据转换为 `relaykcp://`/KCP 连接；报告 `RelayReady` 和失联 | 创建房间、决定谁可以加入 |
| `RelayLobbyApi` | HTTP JSON 请求、Bearer、超时、错误转换 | 直接调用 `StartHost` 或维护游戏状态 |
| `RelayLobbyClient` | 可选的轻量门面，委托给同一个 manager 会话 | 建立第二套 session state |
| `RelaySession` | 旧 UDP 控制路径 `Create/Join/Leave` | 新 HTTP 大厅的房间编辑和成员模型 |
| `RelayRoomEvents` | 房间变化、成员变化、关闭、错误事件 | 替代 Mirror 的连接回调 |
| `RelayRoomRegistry` | 服务端内存房间、成员、owner/member/relay 凭据、权限和超时 | 持久化账号、匹配算法、游戏玩法 |
| `RelayHttpServer` / `RelayUdpServer` | API 路由与 UDP 转发 | 加载 Unity、执行场景或 AI |

## 会话状态与启动顺序

`RelayRoomState` 的常见状态为：

```text
Offline → Connecting → Lobby → Connected → Started
                    ↘ Error
任何活动状态 → Closing → Offline
```

推荐把一次操作当成“获取版本号 + 串行锁 + 可取消会话”：

1. **检查空闲。** 旧 Mirror 网络和旧凭据必须已清理。
2. **创建新的 session generation。** 取消旧的 HTTP/启动任务，清除旧错误。
3. **HTTP 分配或加入。** `POST /api/v1/rooms` 或 `POST /api/v1/rooms:join`。
4. **校验返回。** 至少校验 `roomId`、`memberId`、`peerId`、`relayToken`、`memberToken`、Relay 地址和端口。
5. **应用凭据。** 只由 manager 调用 `RelayKcpTransport.ApplyCredentials`，并把该 Transport 设置为当前 `NetworkManager.transport`。
6. **启动 Mirror。** 房主调用 `StartHost()`；成员使用凭据 URI 调用 `StartClient(...)`。
7. **等待两个条件。** `RelayReady` 表示认证 Ping 已确认；`NetworkClient.isConnected` 和本地玩家生成表示 Mirror 已建立。只满足其一不能宣称连接成功。
8. **应用房间快照。** 成功进入后才把 UI 切换到 Lobby/Connected/Started。
9. **任一阶段失败都回滚。** 停止本地 Mirror，发送 UDP best-effort Leave，并用 owner/member token 调用 HTTP 清理；将状态置为 `Error` 或 `Offline`。

Relay 心跳默认每 0.5 秒发送一次，约 15 秒没有认证回应判定 Relay 失联。`RelayReady` 与 `NetworkClient.isConnected` 是两个独立信号，UI、测试和断线逻辑要分别显示/处理。

## HTTP 契约

当前服务器端点：

| 方法 | 路径 | 要点 |
| --- | --- | --- |
| `GET` | `/health` | 进程和房间计数，不代表某个客户端已连接 |
| `GET` | `/api/v1/rooms` | 只返回公开、可加入、未开始房间；支持 `pageSize/cursor/tag/state` |
| `POST` | `/api/v1/rooms` | 创建房间并返回 owner/member/relay 凭据 |
| `GET` | `/api/v1/rooms/{roomId}` | 无凭据只返回安全快照；owner/member 才可获得成员详情 |
| `PATCH` | `/api/v1/rooms/{roomId}` | 仅 owner；修改名称、容量、可见性、可加入和标签 |
| `DELETE` | `/api/v1/rooms/{roomId}` | 仅 owner；关闭并清理成员 |
| `POST` | `/api/v1/rooms:join` | 公开房间用 `roomId`，私密房间还要 `inviteCode` |
| `POST` | `/api/v1/rooms:leave` | `roomId/memberId/memberToken`；成员主动离开 |
| `POST` | `/api/v1/rooms:start` | 仅 owner；将 `started=true` 且禁止新成员加入 |
| `POST` | `/api/v1/rooms/{roomId}/members/{memberId}:kick` | 仅 owner；先撤销成员，再通知 Relay |

服务器将 owner、member、relay 凭据分开保存，授权比较使用哈希/固定时间比较。公开列表不得返回 token、Relay endpoint、私密房间或完整成员记录。公开切私密时应生成新邀请码；普通成员不能读取邀请码。

## Relay 数据面不变量

- Relay UDP 数据报上限为 1200 字节，当前头部 48 字节，KCP 可用负载最多 1152 字节；不要用业务消息大小绕过 `Transport.GetMaxPacketSize`。
- 客户端只能向 Host peer `1` 发送游戏数据；Host 向成员发送。Relay 不允许客户端互转。
- 控制 socket 和 KCP 数据 socket 可以是不同 UDP endpoint；不要按控制 socket 固定来源做错误的反欺骗假设。
- Ping 的认证确认只说明 Relay 会话存活，不等于 Mirror 玩家已生成。
- 离开、踢出、成员超时应只断开目标成员；剩余成员继续转发。Host 离开、关闭房间或 Relay 失联则结束整个会话。
- 凭据是认证材料，不是加密。不要将 token 拼入普通日志、截图 JSON、遥测或共享命令文件。

## 旧 UDP 兼容路径

旧客户端可以继续：

```csharp
RelayCredentials host = RelaySession.Create(relayAddress, relayPort);
RelayCredentials member = RelaySession.Join(relayAddress, relayPort, roomId, hostToken);
relayTransport.ApplyCredentials(credentials);
StartHost(); // host
StartClient(credentials.ToUri()); // member
RelaySession.Leave(credentials);
```

该路径创建的是传统私密房间，没有 HTTP 房间编辑、公开列表、成员显示或开始门控。新功能默认走 HTTP 控制面；旧模式仍保留时，必须明确 UI 和测试选择的是哪条路径。

## Unity 接入骨架

```csharp
public enum SessionMode { Direct, RelayLobby }

public async Task StartAsync(SessionMode mode, CancellationToken ct)
{
    EnsureOffline();
    if (mode == SessionMode.Direct)
    {
        ConfigureDirectTransport();
        StartHostOrClient();
        return;
    }

    RelayRoomCredentials credentials = await manager.StartLobbyHostAsync(
        BuildCreateRequest(), ct);
    // manager 内部已 ApplyCredentials + StartHost + 等待 Relay/Mirror ready。
    BindLobbyEvents(manager.RoomEvents);
}
```

应用层的 `SessionMode` 只是选择入口；不要复制一套玩家、场景和玩法 NetworkBehaviour。

## 错误与回滚

至少区分这些错误：

| 阶段 | 典型错误 | 必须动作 |
| --- | --- | --- |
| HTTP | 400/403/404/409、超时、代理返回非 JSON | 不启动 Mirror；保留可读错误；清除本地临时凭据 |
| Relay | 无法解析地址、认证拒绝、15 秒心跳超时 | 停止 Mirror；清理房间/成员；禁止旧任务恢复状态 |
| Mirror | KCP/Weaver/Spawn/场景切换失败 | 停止当前网络；释放 Relay allocation；回到 Offline/Error |
| UI 操作竞争 | Search/Join/Leave 同时触发 | manager 串行化；旧操作完成后不得覆盖新状态 |

取消、退出、被踢、房主关闭、服务端重启都要验证。Relay 服务重启后内存房间和凭据失效，不要假设能自动恢复会话；若产品需要重连，另行设计新的身份和幂等协议。

## 验收清单

### 基础连接

- HTTP 创建/搜索/加入、RelayReady、Mirror 连接、本地玩家生成全部可观测。
- Host 使用 LocalConnection；至少一个独立进程客户端通过真实 UDP Relay 加入。
- 等待状态下玩法输入被门控；开始后状态同步和服务器权威逻辑正常。

### 房间生命周期

- 公开/私密、邀请码轮换、容量、开始后 `409`、成员离开、owner 踢人、关闭房间。
- 成员离开或被踢不会误断开其他成员；Host 断开会结束剩余客户端。
- HTTP 停止、UDP 停止、Relay 心跳超时、服务端重启后都能回到稳定状态并重试。

### 安全与观测

- 报告只包含公开房间 ID、成员 ID/名称、连接阶段和错误；不包含 owner/member/relay token 或邀请码。
- 验证错误 room、peer、target、token、超大包和客户端到客户端转发均被拒绝。
- 检查场景/预制体序列化引用、Transport、玩家 prefab、Spawnable、UI EventSystem 和滚动列表。

仓库内完整示例和脚本见 `Assets/Mirror/Examples/RelayLobby/README.md`、`VERIFICATION.md` 以及 `RelayServer/Scripts/`。这些文件是当前仓库的可执行参考，不应替代目标项目的实际版本核对。
