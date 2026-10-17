# 对象生成

## 目录

- [生成方法](#生成方法)
- [生成流程](#生成流程)
- [客户端注册预制体](#客户端注册预制体)
- [自定义 SpawnHandler](#自定义-spawnhandler)
- [销毁与反生成](#销毁与反生成)
- [对象池模式](#对象池模式)

---

## 生成方法

```csharp
// 基本生成（无拥有者）
NetworkServer.Spawn(obj);

// 带拥有者生成
NetworkServer.Spawn(obj, ownerConnection);

// 通过玩家对象指定拥有者（查找 player 的 connectionToClient）
NetworkServer.Spawn(obj, ownerPlayerGameObject);

// 自定义 assetId（AssetBundle / 动态资源场景）
NetworkServer.Spawn(obj, customAssetId, ownerConnection);
```

| 重载 | 签名 | 说明 |
|------|------|------|
| 1 | `Spawn(GameObject obj, GameObject ownerPlayer)` | 通过玩家对象查找 connectionToClient |
| 2 | `Spawn(GameObject obj, NetworkConnectionToClient ownerConnection = null)` | 主重载 |
| 3 | `Spawn(GameObject obj, uint assetId, NetworkConnectionToClient ownerConnection = null)` | 覆盖 assetId |

**前置条件：** 对象必须有 `NetworkIdentity` 组件，且不能是未实例化的预制体。

## 生成流程

### 服务器端 (SpawnObject)

1. 验证：非预制体、服务器活跃、有 NetworkIdentity、未重复生成
2. 设置 `connectionToClient = ownerConnection`
3. Host 模式：若 owner 是 `LocalConnectionToClient`，设 `isOwned = true`
4. 激活对象 `SetActive(true)`
5. 首次初始化（`netId == 0` 时）：
   - 分配 `netId`（递增计数器）
   - `isServer = true`，添加到 `NetworkServer.spawned`
   - 调用所有 NetworkBehaviour 的 `OnStartServer()`
6. AOI：`aoi.OnSpawned(identity)`
7. 重建观察者 → 对每个观察者连接发送 `SpawnMessage`

### SpawnMessage 结构

```csharp
public struct SpawnMessage : NetworkMessage
{
    public uint netId;
    public SpawnFlags spawnFlags;    // isOwner + isLocalPlayer 的位合并
    public ulong sceneId;            // 非零时用于场景对象
    public uint assetId;             // 预制体资源ID
    public Vector3 position;         // localPosition
    public Quaternion rotation;      // localRotation
    public Vector3 scale;            // localScale
    public ArraySegment<byte> payload; // 序列化的组件数据
}

[Flags] public enum SpawnFlags : byte
{
    None = 0, isOwner = 1 << 0, isLocalPlayer = 1 << 1
}
```

### 客户端接收 (OnSpawn → ApplySpawnPayload)

1. 查找或创建对象：先查 `spawned` 字典，再查自定义 spawnHandler，最后查预制体 Instantiate
2. 设置 `netId`、`isOwned`、transform
3. 反序列化 payload（SyncVar/SyncObject 初始状态）
4. 调用回调：`OnStartClient()` → `OnStartAuthority()`（若 isOwned）→ `OnStartLocalPlayer()`（若 isLocalPlayer）

**初始批次 vs 后续生成：** 首次连接时服务器批量发送所有对象，客户端延迟到 `ObjectSpawnFinished` 后统一应用；后续生成则立即应用。

## 客户端注册预制体

### 委托类型

```csharp
public delegate GameObject SpawnDelegate(Vector3 position, uint assetId);
public delegate GameObject SpawnHandlerDelegate(SpawnMessage msg);
public delegate void UnSpawnDelegate(GameObject spawned);
```

### RegisterPrefab 重载

| # | 签名 | 说明 |
|---|------|------|
| 1 | `RegisterPrefab(GameObject prefab, uint newAssetId)` | 覆盖 assetId |
| 2 | `RegisterPrefab(GameObject prefab)` | 使用预制体自带 assetId |
| 3 | `RegisterPrefab(GameObject prefab, uint newAssetId, SpawnDelegate, UnSpawnDelegate)` | 简单委托+自定义 assetId |
| 4 | `RegisterPrefab(GameObject prefab, SpawnDelegate, UnSpawnDelegate)` | 简单委托 |
| 5 | `RegisterPrefab(GameObject prefab, uint newAssetId, SpawnHandlerDelegate, UnSpawnDelegate)` | 完整消息委托+自定义 assetId |
| 6 | `RegisterPrefab(GameObject prefab, SpawnHandlerDelegate, UnSpawnDelegate)` | 完整消息委托 |

- **SpawnDelegate**：接收 `(position, assetId)`，适合简单场景
- **SpawnHandlerDelegate**：接收完整 `SpawnMessage`（含 position, rotation, scale, netId 等），适合需要旋转/缩放的场景

## 自定义 SpawnHandler

当没有预制体时（如运行时生成的对象），按 assetId 注册：

```csharp
NetworkClient.RegisterSpawnHandler(myAssetId,
    spawnHandler: (SpawnMessage msg) =>
    {
        return Instantiate(myPrefab, msg.position, msg.rotation);
    },
    unspawnHandler: (GameObject obj) =>
    {
        Pool.Return(obj);
    }
);
```

| 重载 | 签名 |
|------|------|
| 1 | `RegisterSpawnHandler(uint assetId, SpawnDelegate, UnSpawnDelegate)` |
| 2 | `RegisterSpawnHandler(uint assetId, SpawnHandlerDelegate, UnSpawnDelegate)` |

## 销毁与反生成

```csharp
NetworkServer.Destroy(obj);    // 客户端也销毁/隐藏 GameObject
NetworkServer.UnSpawn(obj);    // 反生成但不销毁（用于对象池）
```

两者共享内部方法 `UnSpawnInternal(obj, resetState)`，区别：

| 行为 | UnSpawn | Destroy(场景对象) | Destroy(预制体对象) |
|------|---------|-----------------|-------------------|
| 发送 ObjectDestroyMessage | Yes | Yes | Yes |
| 调用 OnStopServer | Yes | Yes | Yes |
| 调用 OnStopClient (Host) | Yes | Yes | Yes |
| 调用 OnStopLocalPlayer (Host) | Yes | Yes | Yes |
| 调用 OnStopAuthority (Host) | Yes | Yes | Yes |
| 调用自定义 unspawnHandler (Host) | Yes | Yes | Yes |
| `identity.ResetState()` | **Yes** | **Yes** | No |
| `SetActive(false)` | **Yes** | **Yes** | No |
| `destroyCalled = true` | No | No | **Yes** |
| `UnityEngine.Object.Destroy()` | **No** | **No** | **Yes** |
| 对象保留在服务器 | **Yes**（可复用） | **Yes**（可复用） | **No**（已销毁） |

**客户端收到 ObjectDestroyMessage 后：**
1. `OnStopLocalPlayer()`（若 isLocalPlayer）
2. `OnStopClient()`
3. 有自定义 unspawnHandler → 调用它 + `ResetState()`
4. 无 handler：预制体对象 → `Destroy()`；场景对象 → `SetActive(false)` + 放回 spawnableObjects

## 对象池模式

```csharp
public static class Spawner
{
    static readonly Pool<GameObject> pool = new Pool<GameObject>();

    public static GameObject Get(GameObject prefab, Vector3 pos, Quaternion rot)
    {
        GameObject obj = pool.Get() ?? Instantiate(prefab);
        obj.transform.position = pos;
        obj.transform.rotation = rot;
        return obj;
    }

    public static void Return(GameObject obj)
    {
        NetworkServer.UnSpawn(obj);  // 反生成但不销毁
        obj.SetActive(false);
        pool.Return(obj);
    }
}

// 注册自定义生成处理器
NetworkClient.RegisterPrefab(prefab,
    spawnHandler: (msg) => Spawner.Get(prefab, msg.position, msg.rotation),
    unspawnHandler: (obj) => Spawner.Return(obj)
);
```
