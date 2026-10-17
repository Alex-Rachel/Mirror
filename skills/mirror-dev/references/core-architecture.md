# 核心架构

## 目录

- [架构概览](#架构概览)
- [网络更新循环](#网络更新循环)
- [NetworkManager](#networkmanager)
- [NetworkIdentity](#networkidentity)
- [NetworkClient](#networkclient)
- [NetworkServer](#networkserver)
- [NetworkConnection](#networkconnection)

---

## 架构概览

```
┌─────────────────────────────────────────────────┐
│                  NetworkManager                  │
│  (单例，管理整个网络会话生命周期)                    │
├──────────────────┬──────────────────────────────┤
│   NetworkServer  │       NetworkClient           │
│  (静态类，服务端) │  (静态类，客户端)               │
├──────────────────┴──────────────────────────────┤
│              Transport (传输层抽象)                │
│  KCP / Telepathy / WebSocket / Multiplex / ...   │
├─────────────────────────────────────────────────┤
│           NetworkIdentity (每个网络对象一个)        │
│  ┌─────────────────────────────────────────┐    │
│  │     NetworkBehaviour (网络行为基类)       │    │
│  │  [SyncVar] [Command] [ClientRpc] ...    │    │
│  └─────────────────────────────────────────┘    │
├─────────────────────────────────────────────────┤
│        InterestManagement (兴趣管理/AOI)          │
│  Distance / Scene / Match / Team / SpatialHash  │
└─────────────────────────────────────────────────┘
```

**核心设计理念：**
- **服务器权威**：所有游戏状态以服务器为准
- **Host 模式**：服务器和客户端可运行在同一进程（LocalConnection 交叉队列，零延迟）
- **Weaver IL 织入**：编译后自动注入网络代码，开发者只需添加 Attribute

## 网络更新循环

NetworkLoop 注入 Unity 的 PlayerLoop：

```
EarlyUpdate.NetworkEarlyUpdate  → 处理入站消息（Transport → Mirror）
    ↓
FixedUpdate / Update / LateUpdate  → 用户游戏逻辑
    ↓
PreLateUpdate.NetworkLateUpdate  → 处理出站消息（Mirror → Transport）
```

**设计原理：** `接收消息 → 更新世界 → 发送消息`，最小化延迟 — Update 中的状态变化在同一帧发出。

## NetworkManager

单例（`NetworkManager.singleton`），通常放在 DontDestroyOnLoad 的 GameObject 上。

### 关键配置

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `transport` | Transport | null | 传输层（自动查找同GameObject） |
| `networkAddress` | string | localhost | 服务器地址 |
| `maxConnections` | int | 100 | 最大连接数 |
| `sendRate` | int | 60 | 同步频率(Hz) |
| `playerPrefab` | GameObject | null | 默认玩家预制体 |
| `autoCreatePlayer` | bool | true | 连接后自动创建玩家 |
| `offlineScene` | string | "" | 断开后加载的场景 |
| `onlineScene` | string | "" | 连接后加载的场景 |
| `authenticator` | NetworkAuthenticator | null | 认证器组件 |
| `disconnectInactiveConnections` | bool | false | 自动踢出无响应连接 |
| `disconnectInactiveTimeout` | float | 60 | 无响应超时(秒) |
| `exceptionsDisconnect` | bool | true | 反序列化异常时断开（安全默认） |

### 启动方法

```csharp
NetworkManager.singleton.StartHost();    // Host模式（服务器+客户端同进程）
NetworkManager.singleton.StartServer();  // 纯服务器
NetworkManager.singleton.StartClient();  // 纯客户端
```

### 关键虚方法

```csharp
// 服务器端
OnStartServer() / OnStopServer()
OnServerConnect(conn) / OnServerDisconnect(conn) / OnServerReady(conn)
OnServerAddPlayer(conn)  // 默认：Instantiate playerPrefab + AddPlayerForConnection

// 客户端
OnStartClient() / OnStopClient()
OnClientConnect() / OnClientDisconnect()

// 场景
OnServerChangeScene(string) / OnServerSceneChanged(string)
OnClientChangeScene(string, SceneOperation, bool) / OnClientSceneChanged()
```

## NetworkIdentity

**sealed** 类，每个网络化 GameObject 必须有且仅有一个（在根对象上）。

### 关键属性

| 属性 | 类型 | 说明 |
|------|------|------|
| `netId` | uint | 运行时唯一网络ID（从1开始） |
| `assetId` | uint | 预制体标识（自动从 prefab GUID 生成） |
| `sceneId` | ulong | 场景对象持久ID |
| `isClient` / `isServer` / `isHost` | bool | 运行环境判断 |
| `isLocalPlayer` | bool | 是否是本地玩家对象 |
| `isOwned` | bool | 当前客户端是否拥有此对象 |
| `connectionToClient` | NetworkConnectionToClient | 拥有此对象的客户端连接（服务器端） |
| `connectionToServer` | NetworkConnection | 到服务器的连接（客户端端） |
| `observers` | Dictionary | 能看到此对象的连接集合 |

### Inspector 字段

- **serverOnly**：若为 true，对象不会在客户端生成
- **visibility**：`Default` / `ForceHidden` / `ForceShown`

### 权威方法

```csharp
identity.AssignClientAuthority(conn);    // 分配客户端权威，返回 bool
identity.RemoveClientAuthority();        // 移除客户端权威（不能用于玩家对象）
```

## NetworkClient

静态类，提供客户端全局访问点。

```csharp
NetworkClient.Connect(address);           // 连接服务器
NetworkClient.Disconnect();               // 断开连接
NetworkClient.Ready();                    // 标记就绪
NetworkClient.AddPlayer();                // 请求添加玩家
NetworkClient.Send<T>(message);           // 发送消息到服务器
NetworkClient.RegisterHandler<T>(handler); // 注册消息处理器
NetworkClient.RegisterPrefab(prefab);     // 注册可生成的预制体

NetworkClient.active;        // 客户端是否活跃
NetworkClient.isConnected;   // 是否已连接
NetworkClient.ready;         // 是否就绪
NetworkClient.localPlayer;   // 本地玩家 NetworkIdentity
NetworkClient.spawned;       // 所有已生成的网络对象字典 (uint → NetworkIdentity)
```

## NetworkServer

静态类，提供服务端全局访问点。

```csharp
NetworkServer.Listen(maxConnections);              // 开始监听
NetworkServer.Shutdown();                           // 关闭服务器
NetworkServer.Spawn(obj, ownerConnection);          // 生成网络对象
NetworkServer.Destroy(obj);                         // 销毁网络对象
NetworkServer.UnSpawn(obj);                         // 反生成但不销毁（对象池）
NetworkServer.SendToAll<T>(message);                // 广播消息
NetworkServer.SendToReady<T>(message);              // 发送给就绪客户端
NetworkServer.AddPlayerForConnection(conn, player); // 为连接添加玩家
NetworkServer.RegisterHandler<T>(handler);          // 注册消息处理器

NetworkServer.active;        // 服务器是否活跃
NetworkServer.spawned;       // 所有已生成的网络对象 (uint → NetworkIdentity)
NetworkServer.connections;   // 所有连接 (int → NetworkConnectionToClient)
NetworkServer.aoi;           // 当前兴趣管理组件
```

## NetworkConnection

### 层次结构

```
NetworkConnection (抽象基类)
├── NetworkConnectionToServer    // 客户端端：到服务器的连接
│   └── LocalConnectionToServer  // Host模式（交叉队列到LocalConnectionToClient）
└── NetworkConnectionToClient    // 服务器端：到客户端的连接
    └── LocalConnectionToClient  // Host模式（交叉队列到LocalConnectionToServer）
```

**Host 模式原理：** LocalConnection 通过交叉队列工作 — `LocalConnectionToServer.Send()` 入队到 `connectionToClient.queue`，反之亦然。消息在 `Update()` 中处理，模拟与远程连接相同的流程。

### 关键属性

```csharp
conn.isAuthenticated;    // 是否已通过认证
conn.authenticationData; // 认证数据（可存储用户名等，object类型）
conn.isReady;            // 是否已就绪
conn.identity;           // 连接的玩家对象 NetworkIdentity
conn.owned;              // 此连接拥有的所有对象集合 (HashSet<NetworkIdentity>)
conn.lastMessageTime;    // 最后消息时间（用于超时检测）
```

### NetworkConnectionToClient 额外属性

```csharp
conn.address;            // 客户端IP地址
conn.connectionId;       // 连接ID
conn.observing;          // 此连接正在观察的对象集合
conn.rtt;                // 往返时间(秒)
```
