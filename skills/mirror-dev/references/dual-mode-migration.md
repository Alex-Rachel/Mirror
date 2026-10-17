# 旧项目迁移到 RelayLobby 双模式

目标是让同一份玩法同时支持：

1. **原有直连模式**：保留旧地址/端口、认证、Host/Client 入口和已有部署方式。
2. **RelayLobby 模式**：通过 HTTP 创建/加入房间，使用 Relay 返回的凭据和 `RelayKcpTransport` 连接同一个 Mirror 权威 Host。

两种模式共享游戏规则和网络对象，区别集中在会话编排和 Transport 配置。不要把“支持两种连接方式”实现成两套玩家脚本、两套场景或两个互相竞争的 `NetworkManager` 单例。

## 先做资产和代码盘点

在编辑前记录：

| 项目 | 需要确认 |
| --- | --- |
| Mirror | 实际导入程序集、版本/commit、Weaver 是否成功 |
| 入口 | offline/online 场景、Bootstrap、现有 `NetworkManager` 是否 `DontDestroyOnLoad` |
| Transport | 直连 Transport 类型、地址/端口来源、是否被 Encryption/Multiplex/LatencySimulation 包装 |
| 会话 | Host/Join/Disconnect/Retry 是否散落在 UI、静态单例或场景脚本 |
| 玩法 | 玩家 prefab、spawnable、NetworkIdentity、场景对象、服务器权威规则 |
| 序列化 | 现有 prefab/scene GUID、asmdef、测试和构建场景 |
| 退出语义 | 重复点击、场景销毁、应用退出、断线、房主退出、连接超时 |

用 `rg` 搜索 `StartHost`、`StartClient`、`StartServer`、`NetworkManager.singleton`、`Transport.active`、`Instantiate`、`Destroy`、`SceneManager.LoadScene` 和旧大厅/认证 API。排除 `Library`、`Temp`、`obj`、`Logs`、`Build` 等生成目录。先读项目指令和工作区改动，不要覆盖用户未保存的 Unity 资产。

## 推荐分层

```text
GameBootstrap / UI
        │ 只选择模式、收集输入、显示状态
        ▼
SessionFacade（唯一入口）
   ├─ DirectSessionAdapter
   └─ RelayLobbySessionAdapter
        │
        ▼
NetworkManager + 当前 Transport
        │
        ▼
共享玩法 NetworkBehaviour / 场景 / Prefab
```

### 共享层

继续共用：

- 玩法 `NetworkBehaviour`、服务器权威验证、SyncVar/SyncList/Rpc；
- 玩家/道具/场景 NetworkIdentity 和 Spawnable 注册；
- online/offline 场景、出生点、镜头和本地输入门控；
- 断线后的玩法清理和回合状态；
- 与网络无关的模型、UI 组件和存档规则。

### 模式层

只在模式层处理：

- 地址、端口、Transport 类型和连接 URI；
- Direct 的认证/邀请码或旧会话 token；
- RelayLobby 的 HTTP 房间、owner/member/relay token、房间轮询、成员操作；
- 连接状态、取消、超时、释放 allocation 和错误映射。

如果现有 `NetworkManager` 已经包含玩法逻辑，先提取一个继承类或组合式 `SessionFacade`；不要为了 RelayLobby 复制整个 manager。

## 模式选择和 Transport 组合

定义明确的模式和状态，不用 `bool useRelay` 到处传播：

```csharp
public enum SessionMode
{
    Direct,
    RelayLobby
}

public enum SessionState
{
    Offline,
    Preparing,
    Lobby,
    Connecting,
    InGame,
    Closing,
    Error
}
```

推荐由一个 facade 持有 `SessionMode`、`SessionState`、当前取消源和当前 session generation：

```csharp
public sealed class SessionFacade : MonoBehaviour
{
    public SessionMode mode;
    public RelayNetworkManager relayManager;
    public NetworkManager directManager;

    public Task HostAsync(CancellationToken ct)
    {
        return mode == SessionMode.RelayLobby
            ? HostRelayAsync(ct)
            : HostDirectAsync(ct);
    }
}
```

实际工程中可以只有一个继承自 `RelayNetworkManager` 的 manager；关键是**任何时刻只让一个 Transport 成为 `NetworkManager.transport`**。模式切换必须遵循：

```text
停止输入/UI 操作
→ Cancel 当前请求
→ StopClient/StopHost/StopServer
→ Relay Leave/Delete（如有）
→ 清除 room/member/relay 凭据
→ 配置新 Transport
→ 启动新模式
```

不要在连接中直接把 `NetworkManager.transport` 从 KCP 改成 RelayKCP，也不要让 Direct 和 Relay manager 同时成为 singleton。

## Direct 模式适配

Direct adapter 应该保留旧行为，只增加一个统一的结果和状态回调：

```csharp
async Task HostDirectAsync(CancellationToken ct)
{
    EnsureOffline();
    state = SessionState.Connecting;
    directManager.networkAddress = directAddress;
    directTransport.port = directPort;
    directManager.StartHost();
    await WaitForMirrorReadyAsync(ct);
    state = SessionState.InGame;
}
```

需要把旧逻辑中的：

- UI 直接调用 `StartHost/StartClient`；
- 多个脚本直接写 `networkAddress`/`port`；
- 断线时直接 `Destroy` manager；
- 静态 token 或房间变量；

收拢到 adapter/facade，但不要改变旧协议、端口和服务器配置，除非用户明确要求迁移。

## RelayLobby adapter

RelayLobby adapter 只负责编排，使用目标仓库的 API 形状：

```csharp
async Task HostRelayAsync(CancellationToken ct)
{
    EnsureOffline();
    state = SessionState.Preparing;
    RelayRoomCredentials credentials =
        await relayManager.StartLobbyHostAsync(BuildCreateRequest(), ct);
    // RelayNetworkManager 已完成：
    // HTTP 创建 → ApplyCredentials → StartHost → 等待 RelayReady/Mirror/player。
    state = relayManager.CurrentRoom?.room.started == true
        ? SessionState.InGame
        : SessionState.Lobby;
}

async Task JoinRelayAsync(string roomId, string inviteCode, CancellationToken ct)
{
    EnsureOffline();
    state = SessionState.Preparing;
    await relayManager.StartLobbyClientAsync(roomId, inviteCode, displayName, ct);
    state = SessionState.Lobby;
}
```

若目标项目的 Relay API 不同，先写一层 `IRelayLobbyGateway`，把 API DTO 和玩法/UI 隔开；不要让 `RelayRoomCredentials` 渗透到玩家 `NetworkBehaviour`。

## 共享玩法的改造要点

### 输入和权威

- 本地输入只在 `isLocalPlayer`/`isOwned` 对象启用，Dedicated Server 不依赖 Camera、EventSystem 或 UI。
- Command 只传意图和必要参数；服务器重新检查距离、状态、冷却、房间开始标志和目标有效性。
- 房间未开始时，服务器也要拒绝移动/计分等玩法请求；不能只在 UI 禁用按钮。
- RelayLobby 的成员列表是控制面信息，玩家的生命、位置、分数仍走 Mirror 同步。

### 连接与生成

- 进入房间不等于玩家已 Spawn；等待 `RelayReady`、`NetworkClient.isConnected` 和 `NetworkClient.localPlayer` 的完整条件。
- 服务器/客户端重连、场景切换、晚加入和退出时要清理本地房间状态；不能让旧快照复活。
- 保留原有 player prefab 和 spawnable GUID；新增 Relay 组件时优先修改真实 prefab/scene 序列化资产，不要手工拼 fileID。

### UI

UI 只向 facade 发起命令并订阅统一状态：

```text
Direct：Address/Port → Host 或 Join
RelayLobby：HTTP endpoint → Create/Search/Select/Join → Lobby controls
```

两种模式共用连接错误、取消、Leave、回到菜单和加载状态；Relay 特有的房间容量、邀请码、成员、Kick/Start 只在 RelayLobby 面板显示。UI 不直接读取/打印 token。

## 迁移阶段

### 阶段 1：保持旧路径可回归

- 先建立 Direct Host + 独立 Client 的基线；
- 记录旧场景、玩家生成、玩法交互、退出和重复启动结果；
- 不要把 Relay 代码提前接入游戏入口。

### 阶段 2：抽取会话边界

- 把 UI/Bootstrap 中的连接调用移到 facade；
- 将旧 manager 保留为 Direct adapter 或由统一 manager 调用；
- 所有停止路径统一到 `LeaveAsync/StopAsync`，确保幂等。

### 阶段 3：接入 RelayLobby

- 添加 `RelayNetworkManager`/`RelayKcpTransport` 或目标项目等价实现；
- 实现 HTTP endpoint 配置、Create/Search/Join、房间刷新和 Leave；
- 先验证 Host + 一个独立客户端，再接入成员操作、私密邀请和开始门控。

### 阶段 4：共享玩法和场景

- 将两个入口都指向同一 online 场景和 player prefab；
- 验证 Host 的 LocalConnection 与远端 Relay 客户端行为一致；
- 检查场景切换、Late Join、玩家退出、房主退出和重复会话。

### 阶段 5：故障和安全收口

- 让 HTTP 分配成功但 Mirror 失败、Relay 心跳超时、服务端重启、取消和 UI 重复点击都可回滚；
- 报告脱敏，凭据仅留在 manager 内存；
- 关闭不需要的旧 API/入口，但保留用户明确要求的兼容路径。

## 验证矩阵

| 场景 | Direct | RelayLobby |
| --- | --- | --- |
| Host + 独立客户端 | 必须 | 必须通过真实 UDP Relay |
| 玩法权威、输入、同步 | 必须 | 必须 |
| 连接失败后重试 | 必须 | HTTP/UDP/Mirror 分阶段重试 |
| Leave/重复 Start/销毁 | 必须幂等 | 必须释放 allocation 和取消旧请求 |
| Late Join | 按旧协议 | 开始前允许，开始后应收到 `409` |
| 成员离开/踢出 | 按旧协议 | 目标断开，其他成员保持连接 |
| Host 退出/服务端停止 | 按旧协议 | 全员收到关闭/断线并清理状态 |
| 凭据与日志 | 旧凭据不泄露 | owner/member/relay token 和 invite 不泄露 |

最终验收至少包含：

```text
编译 + Weaver
→ 保存资产重载后检查 scene/prefab 引用
→ Direct Host + 独立 Client
→ Relay Host + 两个独立 Client
→ 房间操作/开始门控/玩法
→ HTTP/UDP/Host/服务端故障
→ 重复 Leave/重试/重新创建
→ 检查最终 diff 与脱敏报告
```

不要用单进程 Host、LatencySimulation 或仅看 `NetworkClient.isConnected` 代替真实 Relay 验收。若没有独立 Relay 服务或 Unity 构建条件，应明确报告未完成的验收层，而不是宣称双模式完成。

## 常见错误

- **复制整套玩法。** 结果是 Direct 和 Relay 的同步/权威规则逐渐分叉。应共享玩法层，只替换会话 adapter。
- **让 UI 持有凭据。** UI 销毁、日志和截图容易泄露 token。应由 manager 保存，UI 只收公开快照。
- **先 `StartHost` 后申请 Relay。** Relay 凭据是 Transport 启动前置条件；正确顺序是申请、校验、应用、启动。
- **把房间开始标志只做成 SyncVar。** Relay 服务端必须同时把房间设为不可加入；玩法服务器也要拒绝开始前输入。
- **在异步回调中恢复旧房间。** Leave 后的晚响应必须检查 session generation，并释放已经分配的凭据。
- **把 HTTP 服务当成持久化匹配服务。** 当前服务端状态在内存，重启会使房间/凭据失效；自动恢复需要单独设计。
- **只修 C# 不检查 Unity 资产。** Relay Transport、manager、player prefab、spawnable、场景和 EventSystem 的序列化引用同样是功能的一部分。
