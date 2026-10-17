# 延迟补偿与网络发现

## 目录

- [LagCompensator](#lagcompensator)
- [NetworkDiscovery](#networkdiscovery)
- [NetworkRoomManager](#networkroommanager)

---

## LagCompensator

基于 Valve 网络模型，服务器将命中检测回溯到客户端发送命令时的状态。

添加到有碰撞体的玩家对象上，服务器端自动记录碰撞体位置历史。

```csharp
// 采样当前回溯状态
lagCompensator.Sample(viewerConn, out Capture3D sample);

// 边界检测（tolerance 是距离，单位米）
lagCompensator.BoundsCheck(viewerConn, hitPoint, toleranceDistance, out distance, out nearest);

// 射线检测（tolerance 是百分比，0.05=5%额外容差）
lagCompensator.RaycastCheck(viewerConn, originPoint, hitPoint, tolerancePercent, layerMask, out hit);
```

**注意：** `BoundsCheck` 的 tolerance 是距离（米），`RaycastCheck` 的 tolerance 是百分比（0.05=5%额外容差）。

## NetworkDiscovery

基于 UDP 广播的 LAN 服务器发现。**无 singleton**，需通过 Inspector 引用或 `FindObjectOfType` 获取实例。

```csharp
NetworkDiscovery discovery; // 通过 Inspector 赋值

// 服务器广播
discovery.AdvertiseServer();

// 客户端搜索
discovery.StartDiscovery();
discovery.OnServerFound.AddListener(OnServerFound);
```

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `serverBroadcastListenPort` | int | 47777 | 广播监听端口 |
| `ActiveDiscoveryInterval` | float | 3 | 主动发现间隔(秒) |
| `secretHandshake` | long | 随机 | 握手密钥（防止跨游戏干扰） |

## NetworkRoomManager

继承 NetworkManager，提供房间/大厅功能。

**两个场景：** RoomScene + GameplayScene
**两个玩家预制体：** roomPlayerPrefab + playerPrefab

**流程：**
1. 连接 → 进入 RoomScene → 生成 roomPlayer
2. 所有人就绪 → `OnRoomServerPlayersReady()`
3. 切换 GameplayScene → 替换为游戏玩家

**关键虚方法：**

| 方法 | 说明 |
|------|------|
| `OnRoomStartServer()` | 房间服务器启动 |
| `OnRoomServerConnect(conn)` | 玩家进入房间 |
| `OnRoomServerCreateGamePlayer(conn, roomPlayer)` | 创建游戏玩家对象 |
| `OnRoomServerPlayersReady()` | 所有玩家就绪 |

**NetworkRoomPlayer：** 房间中的玩家对象，包含 `ready` 状态和 `index` 位置。
