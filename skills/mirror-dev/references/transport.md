# Transport 选型与配置

## 目录

- [选择指南](#选择指南)
- [Transport 抽象基类](#transport-抽象基类)
- [KCP Transport（推荐）](#kcp-transport推荐)
- [ThreadedKCP Transport](#threadedkcp-transport)
- [SimpleWeb Transport（WebSocket）](#simpleweb-transportwebsocket)
- [Multiplex Transport（多路复用）](#multiplex-transport多路复用)
- [Encryption Transport（加密）](#encryption-transport加密)
- [LatencySimulation Transport（延迟模拟）](#latencysimulation-transport延迟模拟)
- [Edgegap Relay Transport](#edgegap-relay-transport)

---

## 选择指南

```
大多数游戏 → KCP（默认，Reliable+Unreliable over UDP）
├─ WebGL客户端 → SimpleWeb + Multiplex(KCP+SimpleWeb)
├─ 需要加密 → EncryptionTransport 包装 KCP
├─ 服务器瓶颈 → ThreadedKCP
├─ NAT穿透 → Edgegap Relay
├─ 开发测试 → LatencySimulation 包装真实Transport
└─ 防火墙限制 → Telepathy（TCP，不推荐）
```

| Transport | 协议 | 通道 | WebGL | 场景 |
|-----------|------|------|-------|------|
| **KCP** | UDP | Reliable+Unreliable | ❌ | 大多数游戏 |
| ThreadedKCP | UDP | Reliable+Unreliable | ❌ | 服务器CPU瓶颈 |
| SimpleWeb | WebSocket | Reliable only | ✅ | WebGL浏览器客户端 |
| Multiplex | 多协议 | 取决子传输 | ✅ | 原生+浏览器 |
| Encryption | 包装器 | 取决内部 | 取决内部 | 端到端加密 |
| LatencySimulation | 包装器 | 取决内部 | ❌ | 开发测试 |
| Edgegap | UDP中继 | Reliable+Unreliable | ❌ | NAT穿透 |

## Transport 抽象基类

所有传输层继承 `Transport : MonoBehaviour`，实现以下接口：

```csharp
// 客户端
ClientConnect(address) / ClientSend(data, channelId) / ClientDisconnect()
// 服务器
ServerStart() / ServerSend(connId, data, channelId) / ServerDisconnect(connId) / ServerStop()
// 更新（处理入站/出站）
ClientEarlyUpdate() / ServerEarlyUpdate() / ClientLateUpdate() / ServerLateUpdate()
// 生命周期
Available() / GetMaxPacketSize(channelId) / Shutdown()
```

**通道：** `Channels.Reliable`(0, 有序保证到达) / `Channels.Unreliable`(1, 无序可能丢失)

**单例：** `Transport.active` — 当前活跃的传输层实例。

## KCP Transport（推荐）

基于 KCP 协议（可靠 UDP），Mirror 默认传输层。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `port` | ushort | 7777 | UDP 端口 |
| `DualMode` | bool | true | IPv6+IPv4 双栈监听 |
| `NoDelay` | bool | true | 低延迟模式（推荐开启） |
| `Interval` | uint | 10 | KCP 更新间隔(ms)，越低延迟越小 |
| `Timeout` | int | 10000 | 连接超时(ms)，KCP 自动发 ping |
| `FastResend` | int | 2 | 快速重传（0=普通，2=快速，带宽换延迟） |
| `CongestionWindow` | bool | false | 拥塞窗口（关闭提升吞吐） |
| `ReceiveWindowSize` | uint | 4096 | 接收窗口大小（影响最大消息尺寸） |
| `SendWindowSize` | uint | 4096 | 发送窗口大小 |
| `MaximizeSocketBuffers` | bool | true | 自动最大化 Socket 缓冲到 OS 限制 |

**不支持 WebGL。** 支持 Reliable + Unreliable 双通道。

## ThreadedKCP Transport

与 KCP 相同的配置字段和默认值，但将 KCP 计算移到专用工作线程：

- 继承 `ThreadedTransport`（而非 `Transport`）
- 所有操作通过 `ConcurrentQueue` 在主线程和工作线程间传递
- `ServerGetClientAddress` 不支持（线程不安全）
- 自动检测设备休眠，超时后断开连接（`sleepDetection`/`sleepTimeoutInSeconds`）
- 统计 GUI/日志因线程安全原因被禁用

另有 `ThreadedEncryptionKcpTransport`：ThreadedKCP + AES256-GCM 加密。

## SimpleWeb Transport（WebSocket）

用于 WebGL 浏览器客户端。仅支持 Reliable 通道。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `port` | ushort | 27777 | WebSocket 端口 |
| `maxMessageSize` | int | 16384 | 防分配攻击的消息大小上限 |
| `serverMaxMsgsPerTick` | int | 10000 | 服务器每 tick 最大处理消息数 |
| `clientMaxMsgsPerTick` | int | 1000 | 客户端每 tick 最大处理消息数 |
| `maxSendQueueSize` | int | 1000 | 发送队列上限（超限断开） |
| `sendTimeout` | int | 5000 | 发送超时(ms) |
| `receiveTimeout` | int | 20000 | 接收超时(ms) |
| `noDelay` | bool | true | 禁用 Nagle 算法 |
| `batchSend` | bool | true | 批量发送 |
| `waitBeforeSend` | bool | true | 等待1ms后批量发送 |
| `clientUseWss` | bool | false | 客户端使用 wss:// 方案 |

**SSL/WSS：** `sslEnabled` 控制服务器端 SSL 终止（需 cert.json）；`clientUseWss` 强制客户端用 wss://（适用于反向代理 TLS）。`sslEnabled=true` 时自动强制 `clientUseWss=true`。

## Multiplex Transport（多路复用）

同时运行多个传输层，客户端使用**第一个可用**的连接。

```csharp
// 典型配置：KCP(7777) + SimpleWeb(27777)，同时服务原生和浏览器客户端
```

- 服务器：所有传输同时监听，连接ID通过双向查找字典映射为唯一多路复用ID
- 客户端：遍历 transports 数组，用第一个 `Available()` 的连接
- `GetMaxPacketSize` 返回所有传输中的**最小值**（确保消息不会过大）

## Encryption Transport（加密）

为任何传输层添加 **AES-256-GCM** 加密。`IsEncrypted` 始终返回 true。

**公钥验证模式（ValidationMode）：**

| 模式 | 说明 |
|------|------|
| `Off` | 不验证，服务器公钥始终接受 |
| `List` | 与 `ClientTrustedPubKeySignatures` 指纹数组匹配 |
| `Callback` | 调用 `OnClientValidateServerPubKey` 自定义验证 |

**工作原理：** 包装 `Inner` 传输，所有数据经 `EncryptedConnection` 加密后传给 Inner。`GetMaxPacketSize` 自动减去加密开销。握手完成后才触发连接事件。

## LatencySimulation Transport（延迟模拟）

开发测试用，包装真实传输层添加模拟网络条件。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `latency` | float | 100 | 基础延迟(ms)，可靠和不可靠都应用 |
| `jitter` | float | 0.02 | 抖动系数（Perlin噪声），范围[0,1] |
| `jitterSpeed` | float | 1 | 抖动速度 |
| `unreliableLoss` | float | 2 | 不可靠通道丢包率(%)，推荐2%长期测试 |
| `unreliableScramble` | float | 2 | 不可靠通道乱序率(%) |

- 可靠通道：仅延迟+抖动（丢包表现为重传延迟，可靠不乱序）
- 不可靠通道：延迟+丢包+乱序
- 使用 `Mathf.PerlinNoise` 产生确定性抖动

## Edgegap Relay Transport

继承 `KcpTransport`，通过 Edgegap 中继服务器路由流量，解决 NAT 穿透。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `relayAddress` | string | 127.0.0.1 | 中继服务器地址 |
| `relayGameServerPort` | ushort | 8888 | 中继上的游戏服务器端口 |
| `relayGameClientPort` | ushort | 9999 | 中继上的游戏客户端端口 |
| `userId` | uint | 11111111 | 用户ID（中继认证） |
| `sessionId` | uint | 22222222 | 会话ID（中继认证） |

另有 `EdgegapLobbyKcpTransport`：扩展 EdgegapKcpTransport，添加大厅服务集成（`lobbyUrl`、`lobbyWaitTimeout`）。
