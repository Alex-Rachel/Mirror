# Attribute 参数与约束

## 目录

- [SyncVar](#syncvar)
- [Command](#command)
- [ClientRpc](#clientrpc)
- [TargetRpc](#targetrpc)
- [Server / ServerCallback](#server--servercallback)
- [Client / ClientCallback](#client--clientcallback)
- [其他 Attribute](#其他-attribute)
- [常见错误](#常见错误)

---

> 按使用频率排序。常见错误见底部。

## [SyncVar]

| 参数 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `hook` | string | null | 签名必须 `void Method(T old, T new)`，只在**接收端**调用 |

**约束：** 必须在 NetworkBehaviour 字段上；不能 static/泛型；每类最多 64 个（含父类）；Host 模式 Hook 立即触发（有递归保护）；`GameObject`/`NetworkIdentity`/`NetworkBehaviour` 类型内部存为 netId。

## [Command]

| 参数 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `channel` | int | Reliable(0) | 高频用 Unreliable(1) |
| `requiresAuthority` | bool | true | 非拥有者能否调用 |

**约束：** 必须在 NetworkBehaviour 方法上；不能 static/abstract（virtual 可以）；返回 void；不能协程；无泛型/out 参数；可选参数仅限 `NetworkConnectionToClient conn = null`（sender connection，其他参数不可 optional）；Host 模式直接本地执行。

**注意：** Command 的 sender connection 参数只接受 `NetworkConnectionToClient` 及其派生类，不接受基类 `NetworkConnection`（Weaver 会报错）。

## [ClientRpc]

| 参数 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `channel` | int | Reliable(0) | 传输通道 |
| `includeOwner` | bool | true | 拥有者已本地预测时设 false |

**约束：** 同 Command，但**不支持** sender connection 可选参数（不能用 `NetworkConnectionToClient conn = null`），所有参数均不可 optional。

## [TargetRpc]

| 参数 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `channel` | int | Reliable(0) | 传输通道 |

第一个参数可为 `NetworkConnection target` 或 `NetworkConnectionToClient target` 显式指定目标（基类或派生类均可），省略则默认 `connectionToClient`。

**与 Command 的区别：** TargetRpc 的 connection 参数接受基类 `NetworkConnection` 及其派生类；而 Command 的 sender connection 参数只接受 `NetworkConnectionToClient` 及其派生类（不接受基类 `NetworkConnection`，Weaver 会报错）。

## [Server] / [ServerCallback]

| Attribute | 客户端误调用 | 适用 |
|-----------|-------------|------|
| `[Server]` | 警告+返回 | 业务方法 |
| `[ServerCallback]` | 静默返回 | Unity回调(Update/OnTrigger) |

## [Client] / [ClientCallback]

与 Server 对称，检查 `NetworkClient.active`。

## 其他 Attribute

| Attribute | 用途 |
|-----------|------|
| `[Scene]` | Inspector 场景选择 |
| `[ShowInInspector]` | 显示私有 SyncObject |
| `[ReadOnly]` | Inspector 只读 |
| `[WeaverPriority]` | Reader/Writer 优先选择 |

## 常见错误

| 错误 | 正确做法 |
|------|----------|
| SyncVar 在 MonoBehaviour | 必须在 NetworkBehaviour |
| SyncVar hook 签名不匹配 | 必须 `void M(T old, T new)` |
| [Server] 在 Update 上 | 用 [ServerCallback] |

> 完整 Weaver 错误速查见 [weaver-errors.md](weaver-errors.md)
