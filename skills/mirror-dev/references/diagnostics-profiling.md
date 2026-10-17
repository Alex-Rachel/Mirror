# 诊断与性能分析

## 目录

- [NetworkDiagnostics](#networkdiagnostics)
- [NetworkRuntimeProfiler](#networkruntimeprofiler)
- [NetworkStatistics](#networkstatistics)
- [NetworkPingDisplay](#networkpingdisplay)
- [ConnectionQuality](#connectionquality)
- [NetworkName](#networkname)
- [NetworkDiagnosticsDebugger](#networkdiagnosticsdebugger)

---

## NetworkDiagnostics

静态事件系统，发布每条消息的发送/接收事件。供性能分析工具订阅。

```csharp
// 订阅
NetworkDiagnostics.OutMessageEvent += OnSend;
NetworkDiagnostics.InMessageEvent += OnReceive;

void OnSend(NetworkDiagnostics.MessageInfo info)
{
    // info.message  — 消息对象
    // info.channel  — 通道
    // info.bytes    — 消息大小（不含传输头）
    // info.count    — 发送目标连接数
}
```

**MessageInfo 字段：** `message` (NetworkMessage), `channel` (int), `bytes` (int), `count` (int)

## NetworkRuntimeProfiler

定期打印网络消息/RPC 统计表。添加到任意 GameObject 即可工作。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `RecentDuration` | float | 5 | "最近"统计窗口(秒)，也是输出间隔 |
| `Sort` | Sorter | — | 排序方式 |
| `Output` | OutputType | — | 输出目标 |
| `OutputFilePath` | string | "network-stats.log" | 文件输出路径 |

**SortBy 枚举：** `RecentBytes`, `RecentCount`, `TotalBytes`, `TotalCount`

**OutputType 枚举：** `UnityLog`, `StdOut`, `File`

**输出格式：** IN/OUT Messages + IN/OUT RPCs 四个表，列：Name, Recent Bytes, Recent Count, Total Bytes, Total Count

## NetworkStatistics

每秒统计客户端/服务端的收发包数和字节数。添加到 NetworkManager 同一 GameObject。

**公开字段（每秒结果）：**

| 字段 | 说明 |
|------|------|
| `clientReceivedPacketsPerSecond` | 客户端收包/s |
| `clientReceivedBytesPerSecond` | 客户端收字节/s |
| `clientSentPacketsPerSecond` | 客户端发包/s |
| `clientSentBytesPerSecond` | 客户端发字节/s |
| `serverReceivedPacketsPerSecond` | 服务器收包/s |
| `serverReceivedBytesPerSecond` | 服务器收字节/s |
| `serverSentPacketsPerSecond` | 服务器发包/s |
| `serverSentBytesPerSecond` | 服务器发字节/s |

**数据源：** 直接挂钩 Transport 事件（非 NetworkDiagnostics）。

## NetworkPingDisplay

IMGUI 显示客户端 RTT 和连接质量。右下角。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `color` | Color | white | 文字颜色 |
| `padding` | int | 2 | 屏幕边距 |
| `width` | int | 150 | 显示宽度 |
| `height` | int | 25 | 显示高度 |

**显示：** `RTT: Xms` + `Q: ----`（破折号数=ConnectionQuality 等级，颜色编码）

## ConnectionQuality

```csharp
NetworkClient.connectionQuality  // 当前连接质量
```

| 等级 | 值 | 含义 |
|------|----|------|
| `ESTIMATING` | 0 | 估算中 |
| `POOR` | 1 | 不可玩 |
| `FAIR` | 2 | 延迟非常明显 |
| `GOOD` | 3 | 可玩（非竞技） |
| `EXCELLENT` | 4 | 竞技级 |

**估算方法：**

| 方法 | 输入 | EXCELLENT | GOOD | FAIR |
|------|------|-----------|------|------|
| `Simple` | rtt, jitter | rtt≤100ms && jitter≤100ms | rtt≤200ms && jitter≤200ms | rtt≤400ms && jitter≤500ms |
| `Pragmatic` | targetBufferTime, currentBufferTime | 倍率≤1.15 | 倍率≤1.25 | 倍率≤1.50 |

**Pragmatic 需** `SnapshotInterpolation.dynamicAdjustment` 启用。

**颜色编码：** POOR=红, FAIR=橙, GOOD=黄, EXCELLENT=绿, ESTIMATING=灰

## NetworkName

同步 GameObject 名称。手动序列化实现（非 SyncVar）。

```csharp
// 添加到任意网络化 GameObject
// 自动同步 name 从服务器到客户端
[DisallowMultipleComponent]
public class NetworkName : NetworkBehaviour
{
    // OnSerialize 写入 name
    // OnDeserialize 读取并赋值 name
}
```

## NetworkDiagnosticsDebugger

Editor 窗口，实时显示 NetworkDiagnostics 事件流。Window → Mirror → Network Diagnostics Debugger 打开。
