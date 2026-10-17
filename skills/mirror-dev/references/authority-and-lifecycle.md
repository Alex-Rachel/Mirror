# 权威分配与生命周期

## 目录

- [权威分配](#权威分配)
- [替换玩家对象](#替换玩家对象)
- [移除玩家对象](#移除玩家对象)
- [生命周期回调详解](#生命周期回调详解)
- [回调调用顺序](#回调调用顺序)

---

## 权威分配

### AssignClientAuthority

```csharp
public bool AssignClientAuthority(NetworkConnectionToClient conn)
```

返回 `bool`：成功返回 `true`，失败返回 `false`。

**失败条件：**

| 条件 | 错误信息 |
|------|----------|
| `!isServer` | 只能在服务器端对已生成对象调用 |
| `conn == null` | owner 不能为 null，如需移除权威请用 RemoveClientAuthority |
| `connectionToClient != null && conn != connectionToClient` | 已有拥有者，需先 RemoveClientAuthority |

**成功后：**
1. `SetClientOwner(conn)` — 设置 connectionToClient
2. 发送 `ChangeOwnerMessage` 给新拥有者
3. 触发 `clientAuthorityCallback?.Invoke(conn, this, true)`
4. 若连接已在观察此对象，标记所有 `SyncMode.Owner` 组件为脏；否则重建观察者

**clientAuthorityCallback 事件：**
```csharp
public delegate void ClientAuthorityCallback(NetworkConnectionToClient conn, NetworkIdentity identity, bool authorityState);
public static event ClientAuthorityCallback clientAuthorityCallback;
// authorityState=true: AssignClientAuthority 调用
// authorityState=false: RemoveClientOwner 调用（包括断开连接时）
```

### RemoveClientAuthority

```csharp
public void RemoveClientAuthority()
```

返回 `void`（不返回成功/失败）。

**失败条件（日志错误并提前返回）：**

| 条件 | 错误信息 |
|------|----------|
| `!isServer` | 只能在服务器端对已生成对象调用 |
| `connectionToClient?.identity == this` | **不能移除玩家对象的权威** |

**成功后：**
1. 保存 `previousOwner`
2. 调用 `RemoveClientOwner()` — 触发 `clientAuthorityCallback?.Invoke(conn, this, false)`，然后 `connectionToClient = null`
3. 发送 `ChangeOwnerMessage` 给前拥有者

**注意：** 玩家断开连接时，`NetworkConnectionToClient.DestroyOwnedObjects` 会内部调用 `RemoveClientOwner()` 静默移除场景对象的权威。

## 替换玩家对象

### ReplacePlayerForConnection

```csharp
// 当前推荐的重载
public static bool ReplacePlayerForConnection(NetworkConnectionToClient conn, GameObject player, ReplacePlayerOptions replacePlayerOptions)

// 带自定义 assetId
public static bool ReplacePlayerForConnection(NetworkConnectionToClient conn, GameObject player, uint assetId, ReplacePlayerOptions replacePlayerOptions)
```

**已弃用重载**（`bool keepAuthority` 参数）仍可用但建议迁移。

**失败条件（返回 false）：**

| 条件 | 错误信息 |
|------|----------|
| 新对象无 NetworkIdentity | playerGameObject has no NetworkIdentity |
| 新对象已被其他连接拥有 | Cannot replace player, new player is already owned by a different connection |

### ReplacePlayerOptions 枚举

```csharp
public enum ReplacePlayerOptions
{
    KeepAuthority,  // 旧对象保持活跃且保留所有权
    KeepActive,     // 旧对象保持活跃，仅移除所有权
    Unspawn,        // 旧对象在客户端反生成
    Destroy         // 旧对象完全销毁
}
```

| 选项 | 对旧玩家对象的操作 |
|------|-------------------|
| `KeepAuthority` | 发送 ChangeOwnerMessage 清除 isLocalPlayer，保留 hasAuthority |
| `KeepActive` | 调用 `RemoveClientAuthority()` — 清除 isLocalPlayer 和 hasAuthority |
| `Unspawn` | 调用 `NetworkServer.UnSpawn()` |
| `Destroy` | 调用 `NetworkServer.Destroy()` |

**典型场景：** 角色选择、载具上下车（KeepActive = 下车后车辆仍在）。

## 移除玩家对象

```csharp
public static void RemovePlayerForConnection(NetworkConnectionToClient conn, RemovePlayerOptions removeOptions = RemovePlayerOptions.KeepActive)
```

### RemovePlayerOptions 枚举

```csharp
public enum RemovePlayerOptions
{
    KeepActive,  // 仅移除所有权
    Unspawn,     // 反生成
    Destroy      // 销毁
}
```

| 选项 | 操作 |
|------|------|
| `KeepActive` | 设 `connectionToClient = null`，从 `conn.owned` 移除，发送 ChangeOwnerMessage |
| `Unspawn` | 调用 `NetworkServer.UnSpawn()` |
| `Destroy` | 调用 `NetworkServer.Destroy()` |

之后始终设置 `conn.identity = null`。

## 生命周期回调详解

所有 8 个回调定义在 `NetworkBehaviour` 上，均为 `public virtual void`，无参数无返回值。

| 回调 | 时机 | 典型用途 |
|------|------|----------|
| `OnStartServer()` | 服务器生成时（首次 SpawnObject） | 初始化状态、设置初始值 |
| `OnStopServer()` | 服务器停止时（UnSpawnInternal） | 清理服务器资源 |
| `OnStartClient()` | 客户端生成时（仅调用一次） | 初始化视觉、设置UI |
| `OnStopClient()` | 客户端停止时 | 清理客户端资源 |
| `OnStartLocalPlayer()` | 成为本地玩家时（仅调用一次） | 启用输入、设置相机 |
| `OnStopLocalPlayer()` | 不再是本地玩家时 | 清理本地玩家资源 |
| `OnStartAuthority()` | 获得权威时 | 启用控制器 |
| `OnStopAuthority()` | 失去权威时 | 禁用控制器 |

**关键区别：**
- `OnStartClient`：对所有网络对象调用
- `OnStartLocalPlayer`：只对本地玩家调用
- `OnStartAuthority`：获得权威时调用（包括非玩家对象通过 AssignClientAuthority）

**守卫标志：**
- `OnStartClient` 有 `clientStarted` 标志，防止重复调用
- `OnStartLocalPlayer` 有 `previousLocalPlayer` 静态检查，防止重复调用

## 回调调用顺序

### 生成时（客户端侧）

```
OnStartClient → OnStartAuthority（若 isOwned）→ OnStartLocalPlayer（若 isLocalPlayer）
```

**初始批次 vs 后续：** 首次连接时，所有对象延迟到 `ObjectSpawnFinished` 后统一按 netId 顺序调用；后续生成立即调用。

### 销毁/反生成时

**纯客户端模式（NetworkClient.DestroyObject）：**
```
OnStopLocalPlayer（若 isLocalPlayer）→ OnStopClient
```

**Host 模式（NetworkServer.UnSpawnInternal）：**
```
OnStopLocalPlayer（若 isLocalPlayer）→ OnStopClient → isOwned=false → OnStopAuthority → OnStopServer
```

### 所有权变更时（NetworkClient.ChangeOwner）

```
OnStopLocalPlayer（若不再是本地玩家）→ 更新 isOwned → OnStartAuthority 或 OnStopAuthority → 更新 isLocalPlayer → OnStartLocalPlayer（若成为本地玩家）
```
