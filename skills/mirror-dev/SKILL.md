---
name: mirror-dev
description: Mirror Networking for Unity 开发、调试与迁移指导；涵盖常规 Mirror 网络代码、Transport、权威/同步、Weaver，以及把旧项目改造成同时支持直连与 RelayLobby（HTTP 大厅 + UDP Relay + Mirror Host）的双模式。适用于 RelayNetworkManager/RelayKcpTransport、房间生命周期、旧 UDP 兼容和真实连接验收；不用于非 Mirror 网络栈或纯通用 Unity 工作。
---

# Mirror Dev

## 任务路由

先判断请求属于哪一种模式，再只读取相关参考：

| 请求信号 | 工作模式 | 必读参考 |
| --- | --- | --- |
| `NetworkBehaviour`、`SyncVar`、`Command`、`Rpc`、Weaver、AOI、Transform | 常规 Mirror 开发 | 对应现有 `references/` |
| `RelayKcpTransport`、`RelayNetworkManager`、房间/大厅、邀请码、Relay 心跳 | RelayLobby 开发 | [relay-lobby.md](references/relay-lobby.md) |
| “旧项目改造”“保留原有联机”“双模式”“兼容直连和 RelayLobby” | 双模式迁移 | [dual-mode-migration.md](references/dual-mode-migration.md)；必要时再读 [relay-lobby.md](references/relay-lobby.md) |
| 仅需要旧 UDP `RelaySession.Create/Join` | Relay 兼容路径 | [relay-lobby.md](references/relay-lobby.md) 的兼容路径章节 |

不要因为项目引用了 Relay 程序集就默认重写为大厅模式；先确认用户要的是新项目、旧项目迁移，还是只修复已有 Relay 连接。

## 双模式总原则

- **玩法共享，接入分流。** 网络对象、服务器权威规则、同步字段、场景和玩家预制体尽量共用；旧直连和 RelayLobby 只在会话编排、凭据来源、Transport 配置处分叉。
- **一个会话只有一个活动 Transport。** 不要在 `NetworkClient` 或 `NetworkServer` 已经 active 时替换 `NetworkManager.transport`；切换模式必须先完整 Leave/Stop，再清理凭据，最后启动新模式。
- **大厅不是玩法状态。** 房间名、容量、可加入、成员、邀请码、开始标志属于 Relay 控制面；玩家位置、生命值、得分和战斗结果仍由 Mirror 主机权威同步。
- **Relay 凭据只经过会话管理器。** `ownerToken`、`memberToken`、`relayToken`、邀请信息不得写日志、截图报告、共享脚本或同步到客户端玩法对象。
- **启动必须可回滚。** HTTP 分配成功但 Relay/Mirror 启动失败时，必须停止本地网络并释放房间；晚到的异步响应不能恢复已退出的会话。
- **兼容是可验证的行为，不是保留旧类名。** 旧模式要继续通过原有地址/端口、认证、场景和重连/退出路径；RelayLobby 要额外验证公开/私密房间、成员隔离、开始后禁止加入和 Relay 心跳。

## 最小工作流

1. 读取项目实际导入的 Mirror 版本、当前入口场景、现有 `NetworkManager`、Transport、认证器、玩家预制体和测试；不要只根据 README 或另一个 Mirror checkout 推断 API。
2. 先画出两条会话路径：旧直连的 `StartHost/StartClient`，以及 RelayLobby 的“创建/加入房间 → 应用凭据 → `StartHost/StartClient` → RelayReady → Mirror 连接”。
3. 将模式选择与会话状态集中到一个管理器/门面；不要在 UI、玩家脚本和多个静态单例中各自保存连接状态。
4. 只把模式差异注入传输和会话编排层；玩法 `NetworkBehaviour` 通过同一套服务器权威契约运行。
5. 对每条路径执行编译、Weaver、真实 Transport 的 Host + 独立客户端、重复退出/重进和故障回滚验证。

## Attribute 决策树

```
同步单个值 → [SyncVar]（需UI响应？加 hook）
客户端请求服务器 → [Command]（非拥有者？requiresAuthority=false；高频？Unreliable）
服务器通知所有客户端 → [ClientRpc]（拥有者已预测？includeOwner=false）
服务器通知一个客户端 → [TargetRpc]
仅服务器运行 → Unity回调?[ServerCallback] : [Server]
仅客户端运行 → Unity回调?[ClientCallback] : [Client]
```

## 速查

| Attribute | 方向 | 关键参数 |
|-----------|------|----------|
| `[SyncVar]` | S→C / C→S | `hook` |
| `[Command]` | C→S | `channel`, `requiresAuthority` |
| `[ClientRpc]` | S→所有 | `channel`, `includeOwner` |
| `[TargetRpc]` | S→指定 | `channel` |
| `[Server]`/`[ServerCallback]` | 守卫 | 回调用Callback避免警告 |

## 最小骨架

```csharp
using Mirror;
public class MyNetObj : NetworkBehaviour
{
    [SyncVar(hook = nameof(OnHealthChanged))]
    int health = 100;

    public override void OnStartServer() { /* 初始化 */ }
    public override void OnStartLocalPlayer() { /* 启用输入 */ }
    void OnHealthChanged(int old, int @new) { /* 更新UI */ }

    [Command] void CmdFire() { /* 服务器执行 */ }
    [ClientRpc] void RpcEffect() { /* 客户端执行 */ }
}
```

## 按需加载

### 核心架构

| 场景 | 读取 |
|------|------|
| NetworkManager/Identity/Client/Server/Connection/Loop | [core-architecture.md](references/core-architecture.md) |

### Attribute 与同步

| 场景 | 读取 |
|------|------|
| Attribute 参数/约束/常见错误 | [attributes.md](references/attributes.md) |
| 核心代码模板（SyncVar/Cmd/Rpc） | [templates-core.md](references/templates-core.md) |
| 高级代码模板（Message/Authenticator） | [templates-advanced.md](references/templates-advanced.md) |
| SyncDirection/通道/Sync集合/自定义消息 | [sync-and-rpc.md](references/sync-and-rpc.md) |

### 对象管理

| 场景 | 读取 |
|------|------|
| Spawn/注册预制体/销毁/反生成/对象池 | [spawning.md](references/spawning.md) |
| 权威分配/替换玩家/生命周期回调 | [authority-and-lifecycle.md](references/authority-and-lifecycle.md) |

### Transform 与预测

| 场景 | 读取 |
|------|------|
| NetworkTransform 三变体/NetworkRigidbody/快照插值 | [network-transform.md](references/network-transform.md) |
| PredictedRigidbody/输入模式/NetworkAnimator | [prediction-and-input.md](references/prediction-and-input.md) |

### 基础设施

| 场景 | 读取 |
|------|------|
| Transport 选型与配置 | [transport.md](references/transport.md) |
| AOI 选型与配置 | [aoi.md](references/aoi.md) |
| 内置认证器（Basic/Device/UniqueName/Timeout） | [authenticators.md](references/authenticators.md) |
| NetworkReader/Writer/Pool/自定义序列化 | [reader-writer.md](references/reader-writer.md) |
| RelayLobby 控制面、RelayKCP、凭据与心跳 | [relay-lobby.md](references/relay-lobby.md) |

### 高级主题

| 场景 | 读取 |
|------|------|
| 场景切换/Additive Scenes | [scene-management.md](references/scene-management.md) |
| LagComp/Discovery/Room | [lag-compensation-discovery.md](references/lag-compensation-discovery.md) |
| 自定义序列化/Weaver/带宽优化/NetworkTime | [serialization-and-weaver.md](references/serialization-and-weaver.md) |
| Weaver 编译错误速查 | [weaver-errors.md](references/weaver-errors.md) |
| 诊断/性能分析/ConnectionQuality | [diagnostics-profiling.md](references/diagnostics-profiling.md) |

### 开发模式

| 场景 | 读取 |
|------|------|
| 常见模式（Hook桥接/Struct变异/预测/对象池/场景传送门等） | [common-patterns.md](references/common-patterns.md) |
| 旧项目保留直连并新增 RelayLobby 双模式 | [dual-mode-migration.md](references/dual-mode-migration.md) |
