---
name: mirror-dev
description: Mirror Networking for Unity 开发指导。提供 Attribute 决策树、代码模板、架构选型和最佳实践。触发场景：(1) 编写 Mirror 网络代码（NetworkBehaviour/SyncVar/Command/ClientRpc/TargetRpc），(2) 配置 NetworkManager/NetworkIdentity/Transport/AOI/NetworkTransform，(3) 使用 SyncList/SyncDictionary/SyncDirection/InterestManagement，(4) 实现 PredictedRigidbody/NetworkAnimator/自定义 NetworkMessage/NetworkAuthenticator，(5) 调试 NetworkServer/NetworkClient 问题，(6) 场景切换/Additive Scenes/NetworkRoomManager，(7) LagCompensator/NetworkDiscovery，(8) 自定义序列化/Weaver错误/带宽优化/NetworkTime，(9) Spawn/对象池/权威分配/生命周期，(10) NetworkReader/NetworkWriter/自定义读写，(11) 内置认证器/ConnectionQuality/性能分析，(12) 常见开发模式/代码模板。
---

# Mirror Dev

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
