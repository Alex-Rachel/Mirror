# 常见开发模式

> 从 Mirror 25+ 官方示例中提取的经过验证的模式。

## 目录

- [SyncVar Hook + C# 事件桥接](#syncvar-hook--c-事件桥接)
- [Struct SyncVar 变异](#struct-syncvar-变异)
- [Command requiresAuthority=false](#command-requiresauthorityfalse)
- [ReplacePlayerForConnection（载具/角色切换）](#replaceplayerforconnection载具角色切换)
- [权威回调（OnStartAuthority/OnStopAuthority）](#权威回调onstartauthorityonstopauthority)
- [客户端预测模式](#客户端预测模式)
- [Additive Scene Portal](#additive-scene-portal)
- [NetworkRoomManager 子类化](#networkroommanager-子类化)
- [对象池 Spawn/UnSpawn](#对象池-spawnunspawn)
- [自定义 NetworkManager 事件](#自定义-networkmanager-事件)
- [Host 模式守卫](#host-模式守卫)
- [SyncVar 排序依赖](#syncvar-排序依赖)
- [接口组件模式](#接口组件模式)

---

## SyncVar Hook + C# 事件桥接

将 SyncVar hook 转换为 C# event，供 UI/其他组件订阅：

```csharp
[SyncVar(hook = nameof(OnPlayerNumberChanged))]
public byte playerNumber;

public event System.Action<byte> OnPlayerNumberUpdate;

void OnPlayerNumberChanged(byte _, byte newPlayerNumber)
{
    OnPlayerNumberUpdate?.Invoke(newPlayerNumber);
}
```

**来源：** Basic 示例

## Struct SyncVar 变异

struct 是值类型，不能直接修改字段。必须复制-修改-赋值：

```csharp
[SyncVar]
public EquippedItemConfig equippedItemConfig;

// 错误：equippedItemConfig.Use(); // 编译错误
// 正确：
EquippedItemConfig config = equippedItemConfig;
config.Use();
equippedItemConfig = config;
```

**来源：** PickupsDropsChilds 示例

## Command requiresAuthority=false

非拥有者也能调用 Command（聊天、台球、载具夺取）：

```csharp
// 无 sender connection
[Command(requiresAuthority = false)]
void CmdSendChat(string message) { /* ... */ }

// 带 sender connection（识别谁发的）
[Command(requiresAuthority = false)]
void CmdTakeControl(NetworkConnectionToClient conn = null)
{
    Debug.Log($"Control taken by {conn}");
}
```

**来源：** Billiards, TankTheftAuto, Chat 示例

## ReplacePlayerForConnection（载具/角色切换）

替换玩家对象，保留或销毁旧对象：

```csharp
// 上车：旧玩家保持活跃但失去所有权
NetworkServer.ReplacePlayerForConnection(
    connectionToClient, vehicle, ReplacePlayerOptions.KeepActive);

// 下车：旧载具反生成
NetworkServer.ReplacePlayerForConnection(
    conn, player, ReplacePlayerOptions.Unspawn);
```

**缓存原始玩家：** 用 `connectionToClient.authenticationData` 存储原始玩家引用，下车时恢复。

**来源：** TankTheftAuto, CharacterSelection 示例

## 权威回调（OnStartAuthority/OnStopAuthority）

获得/失去权威时启用/禁用控制器：

```csharp
public override void OnStartAuthority()
{
    inputComponent.enabled = true;
    cameraController.enabled = true;
}

public override void OnStopAuthority()
{
    inputComponent.enabled = false;
    cameraController.enabled = false;
}
```

**来源：** CouchCoop, TankTheftAuto, AssignAuthority 示例

## 客户端预测模式

客户端立即应用输入 + Command 发送服务器验证。完整代码见 [prediction-and-input.md](prediction-and-input.md) 模式 B。

**关键点：** Host 模式下 Command 直接本地执行，无需 `if (!isServer)` 判断。

**来源：** BilliardsPredicted 示例

## Additive Scene Portal

场景间传送门，使用 customHandling + RemovePlayer + AddPlayer：

```csharp
[Scene]
public string destinationScene;

void OnTriggerEnter(Collider other)
{
    NetworkIdentity identity = other.GetComponent<NetworkIdentity>();
    if (identity == null || !identity.isServer) return;

    // 从当前场景移除玩家
    NetworkServer.RemovePlayerForConnection(
        identity.connectionToClient, RemovePlayerOptions.Unspawn);

    // 移到目标场景
    SceneManager.MoveGameObjectToScene(
        other.gameObject,
        SceneManager.GetSceneByPath(destinationScene));

    // 重新添加玩家
    NetworkServer.AddPlayerForConnection(
        identity.connectionToClient, other.gameObject);
}
```

**来源：** AdditiveLevels 示例

## NetworkRoomManager 子类化

房间/大厅系统，重写关键虚方法：

```csharp
public class MyRoomManager : NetworkRoomManager
{
    public override void OnRoomServerPlayersReady()
    {
        // 所有人就绪，开始游戏
        ServerChangeScene(GameplayScene);
    }

    public override bool OnRoomServerSceneLoadedForPlayer(
        NetworkConnectionToClient conn, GameObject roomPlayer, GameObject gamePlayer)
    {
        // 将房间数据传递给游戏玩家
        var rp = roomPlayer.GetComponent<MyRoomPlayer>();
        var gp = gamePlayer.GetComponent<MyGamePlayer>();
        gp.color = rp.color;
        return true; // 已处理
    }
}
```

**来源：** Room 示例

## 对象池 Spawn/UnSpawn

使用 `NetworkServer.UnSpawn` 反生成但不销毁，配合 `Pool<T>` 复用。完整代码见 [spawning.md](spawning.md) 对象池模式。

**来源：** Room, MultipleAdditiveScenes 示例

## 自定义 NetworkManager 事件

重写 NetworkManager 虚方法处理连接/断开：

```csharp
public class MyNetworkManager : NetworkManager
{
    public override void OnServerDisconnect(NetworkConnectionToClient conn)
    {
        // 清理玩家数据
        Player player = conn.identity?.GetComponent<Player>();
        if (player != null) RemovePlayerFromGame(player);

        base.OnServerDisconnect(conn);
    }

    public override void OnServerAddPlayer(NetworkConnectionToClient conn)
    {
        // 自定义玩家创建逻辑
        GameObject player = Instantiate(playerPrefab);
        NetworkServer.AddPlayerForConnection(conn, player);
    }
}
```

**来源：** Chat, MultipleMatches, TopDownShooter 示例

## Host 模式守卫

区分 Host/纯服务器/纯客户端逻辑：

```csharp
// 仅在纯客户端执行（Host 跳过）
if (isClientOnly) { /* 客户端预测 */ }

// 仅在纯服务器执行（Host 跳过）
if (isServerOnly) { /* 专用服务器逻辑 */ }

// Host 模式下 Command 直接本地执行，无需特殊处理
// 但 ClientRpc 在 Host 模式也会在本地调用
// 用 includeOwner=false 避免重复
[ClientRpc(includeOwner = false)]
void RpcEffect() { /* Host 已本地预测 */ }
```

## SyncVar 排序依赖

多个 SyncVar 之间有依赖时，Weaver 按声明顺序序列化：

```csharp
// equippedItemConfig 先于 equippedItem 序列化
// 客户端接收时也按此顺序，hook 可安全引用
[SyncVar(hook = nameof(OnConfigChanged))]
public EquippedItemConfig equippedItemConfig;

[SyncVar(hook = nameof(OnItemChanged))]
public Item equippedItem;
```

**来源：** PickupsDropsChilds 示例

## 接口组件模式

用接口解耦子组件，SyncVar hook 通知所有实现接口的子组件：

```csharp
public interface IEquipped
{
    void OnEquipped(Item item);
    void OnUnequipped();
}

[SyncVar(hook = nameof(OnEquippedItemChanged))]
Item equippedItem;

void OnEquippedItemChanged(Item oldItem, Item newItem)
{
    // 通知所有子组件
    foreach (IEquipped e in GetComponents<IEquipped>())
    {
        if (oldItem != null) e.OnUnequipped();
        if (newItem != null) e.OnEquipped(newItem);
    }
}
```

**来源：** PickupsDropsChilds 示例
