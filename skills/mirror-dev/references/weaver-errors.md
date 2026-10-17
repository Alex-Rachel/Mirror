# Weaver 编译错误参考

> 避免 Weaver 错误的完整清单。无需编译即可检查。

## 目录

- [SyncVar 错误](#syncvar-错误)
- [Command 错误](#command-错误)
- [ClientRpc 错误](#clientrpc-错误)
- [TargetRpc 错误](#targetrpc-错误)
- [SyncObject 错误](#syncobject-错误)
- [NetworkBehaviour 错误](#networkbehaviour-错误)
- [消息类型错误](#消息类型错误)
- [Reader/Writer 错误](#readerwriter-错误)
- [Server/Client Attribute 错误](#serverclient-attribute-错误)
- [MonoBehaviour 限制](#monobehaviour-限制)

---

## SyncVar 错误

| 错误代码 | 触发条件 | 修复 |
|----------|----------|------|
| SyncVar on MonoBehaviour | `[SyncVar]` 在非 NetworkBehaviour 类 | 移到 NetworkBehaviour |
| SyncVar is static | `[SyncVar]` 标记 static 字段 | 移除 static |
| SyncVar generic type | `[SyncVar]` 使用泛型类型 | 使用具体类型 |
| SyncVar interface type | `[SyncVar]` 使用接口类型 | 使用具体类型或 NetworkBehaviourSyncVar |
| SyncVar more than 64 | 单类（含父类）SyncVar 超过 64 个 | 减少 SyncVar 或拆分类 |
| SyncVar on SyncList | `[SyncVar]` 标记 SyncObject 字段 | SyncObject 不需要 [SyncVar] |
| SyncVar Unity component | `[SyncVar]` 使用 Component/Transform 等类型 | 使用 NetworkIdentity/NetworkBehaviour/NetworkBehaviourSyncVar |
| Hook not found | hook 方法名不存在或签名不匹配 | 确保签名 `void Method(T old, T new)` |
| Hook wrong old type | hook 的 old 参数类型与 SyncVar 不匹配 | 匹配 SyncVar 类型 |
| Hook wrong new type | hook 的 new 参数类型与 SyncVar 不匹配 | 匹配 SyncVar 类型 |

## Command 错误

| 错误代码 | 触发条件 | 修复 |
|----------|----------|------|
| Cmd is static | `[Command]` 标记 static 方法 | 移除 static |
| Cmd is abstract | `[Command]` 标记 abstract 方法 | 改为 virtual |
| Cmd returns non-void | `[Command]` 返回非 void | 改为 void，用 ClientRpc 回传 |
| Cmd is coroutine | `[Command]` 返回 IEnumerator | 不能用协程，拆为普通方法 |
| Cmd has ref/out | `[Command]` 参数含 ref/out | 移除 ref/out |
| Cmd has generic param | `[Command]` 参数含泛型类型 | 使用具体类型 |
| Cmd has optional param | `[Command]` 有可选参数（除 conn=null） | 仅允许 `NetworkConnectionToClient conn = null` |
| Cmd wrong connection type | sender connection 用 `NetworkConnection` 基类 | 必须用 `NetworkConnectionToClient` |
| Cmd on MonoBehaviour | `[Command]` 在非 NetworkBehaviour 类 | 移到 NetworkBehaviour |

## ClientRpc 错误

| 错误代码 | 触发条件 | 修复 |
|----------|----------|------|
| Rpc is static | `[ClientRpc]` 标记 static 方法 | 移除 static |
| Rpc is abstract | `[ClientRpc]` 标记 abstract 方法 | 改为 virtual |
| Rpc returns non-void | `[ClientRpc]` 返回非 void | 改为 void |
| Rpc is coroutine | `[ClientRpc]` 返回 IEnumerator | 不能用协程 |
| Rpc has ref/out | `[ClientRpc]` 参数含 ref/out | 移除 ref/out |
| Rpc has generic param | `[ClientRpc]` 参数含泛型类型 | 使用具体类型 |
| Rpc has optional param | `[ClientRpc]` 有任何可选参数 | ClientRpc 不支持可选参数 |
| Rpc has NetworkConnection | `[ClientRpc]` 参数含 NetworkConnection | ClientRpc 不支持 connection 参数 |
| Rpc on MonoBehaviour | `[ClientRpc]` 在非 NetworkBehaviour 类 | 移到 NetworkBehaviour |

## TargetRpc 错误

| 错误代码 | 触发条件 | 修复 |
|----------|----------|------|
| TargetRpc is static | `[TargetRpc]` 标记 static 方法 | 移除 static |
| TargetRpc is abstract | `[TargetRpc]` 标记 abstract 方法 | 改为 virtual |
| TargetRpc returns non-void | `[TargetRpc]` 返回非 void | 改为 void |
| TargetRpc is coroutine | `[TargetRpc]` 返回 IEnumerator | 不能用协程 |
| TargetRpc conn not first | NetworkConnection 不是第一个参数 | 移到第一个参数或省略（默认 connectionToClient） |
| TargetRpc wrong conn type | 第一个参数不是 NetworkConnection 类型 | 用 NetworkConnection 或 NetworkConnectionToClient |

## SyncObject 错误

| 错误代码 | 触发条件 | 修复 |
|----------|----------|------|
| SyncObject more than max | SyncObject 数量超过上限（64） | 减少 SyncObject 数量 |
| SyncList generic struct | SyncList\<T\> 中 T 是无自定义 Read/Write 的泛型 struct | 为泛型 struct 注册自定义 Read/Write |
| SyncList interface | SyncList\<T\> 中 T 是接口 | 使用具体类型 |
| SyncDictionary generic struct key | 键类型是泛型 struct 且无自定义 Read/Write | 注册自定义 Read/Write |
| SyncDictionary generic struct item | 值类型是泛型 struct 且无自定义 Read/Write | 注册自定义 Read/Write |
| SyncObject not readonly | SyncObject 字段未标记 readonly | 标记 readonly（Weaver 会警告） |

## NetworkBehaviour 错误

| 错误代码 | 触发条件 | 修复 |
|----------|----------|------|
| Cmd duplicate name | 同一类中多个 Command 同名 | 重命名 |
| ClientRpc duplicate name | 同一类中多个 ClientRpc 同名 | 重命名 |
| TargetRpc duplicate name | 同一类中多个 TargetRpc 同名 | 重命名 |
| Generic SyncVar | SyncVar 使用泛型类型参数 | 使用具体类型 |
| Generic Cmd/Rpc | Cmd/Rpc 参数使用泛型类型 | 使用具体类型 |

## 消息类型错误

| 错误代码 | 触发条件 | 修复 |
|----------|----------|------|
| Message member generic | NetworkMessage struct 含泛型成员 | 使用具体类型 |
| Message member interface | NetworkMessage struct 含接口成员 | 使用具体类型 |

## Reader/Writer 错误

| 错误代码 | 触发条件 | 修复 |
|----------|----------|------|
| No valid constructor | class 无无参构造函数 | 添加无参构造或注册自定义 Read/Write |
| Jagged array | 二维交错数组 `T[][]` | 改用 `List<List<T>>` 或一维数组 |
| Multidimensional array | 多维数组 `T[,]` | 改用一维数组 |
| Invalid array type | 数组元素类型不可序列化 | 确保元素类型有 Writer |
| Invalid List type | List 元素类型不可序列化 | 确保元素类型有 Writer |
| Abstract class | 使用 abstract 类作消息字段 | 注册自定义 Read/Write |
| Interface | 使用接口作消息字段 | 注册自定义 Read/Write |
| MonoBehaviour | 使用 MonoBehaviour 作消息字段 | 传 netId/NetworkIdentity |
| ScriptableObject | 使用 ScriptableObject 作消息字段 | 传必要数据而非对象引用 |
| Object | 使用 System.Object 作消息字段 | 使用具体类型 |

## Server/Client Attribute 错误

| 错误代码 | 触发条件 | 修复 |
|----------|----------|------|
| [Server] on abstract method | abstract 方法标记 [Server] | 改为 virtual 或移到子类 |
| [Server] on override method | override 方法标记 [Server] | 标记在 virtual 基类方法上 |
| [Server] on regular class | 非 NetworkBehaviour 类标记 [Server] | 移到 NetworkBehaviour |
| [Server] on static class | static 类标记 [Server] | 移到 NetworkBehaviour 实例方法 |

## MonoBehaviour 限制

MonoBehaviour **不能**使用以下 Attribute（仅 NetworkBehaviour 可以）：

| Attribute | 错误 |
|-----------|------|
| `[Command]` | Commands can only be added to NetworkBehaviours |
| `[ClientRpc]` | ClientRpcs can only be added to NetworkBehaviours |
| `[TargetRpc]` | TargetRpcs can only be added to NetworkBehaviours |
| `[SyncVar]` | SyncVars can only be used in NetworkBehaviours |
| SyncObject 字段 | SyncObjects can only be used in NetworkBehaviours |

**MonoBehaviour 可以使用：** `[Server]`, `[ServerCallback]`, `[Client]`, `[ClientCallback]`
