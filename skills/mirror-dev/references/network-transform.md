# NetworkTransform 与快照插值

## 目录

- [变体选择](#变体选择)
- [NetworkTransformBase 通用配置](#networktransformbase-通用配置)
- [NetworkTransformReliable](#networktransformreliable)
- [NetworkTransformUnreliable](#networktransformunreliable)
- [NetworkTransformHybrid](#networktransformhybrid)
- [传送方法](#传送方法)
- [NetworkRigidbody 系列](#networkrigidbody-系列)
- [快照插值设置](#快照插值设置)

---

## 变体选择

| 变体 | 带宽 | 延迟 | 可靠性 | 推荐场景 |
|------|------|------|--------|----------|
| **Reliable** | 低（Delta压缩） | 中（队头阻塞） | 高 | 大多数游戏 |
| **Unreliable** | 中（只发变化轴） | 低 | 中（可能丢包） | 极快节奏竞技 |
| **Hybrid** | 低（Delta压缩） | 低 | 高 | 竞技+带宽敏感 |

## NetworkTransformBase 通用配置

所有变体共享以下配置：

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `target` | Transform | null | 同步的目标 Transform（可以是子对象） |
| `updateMethod` | UpdateMethod | Update | 更新时机：Update/FixedUpdate/LateUpdate |
| `syncPosition` | bool | true | 同步位置（运行时不可改） |
| `syncRotation` | bool | true | 同步旋转（运行时不可改） |
| `syncScale` | bool | false | 同步缩放（运行时不可改） |
| `onlySyncOnChange` | bool | true | 仅变化时发送 |
| `compressRotation` | bool | true | smallest-three 四元数压缩（有损） |
| `interpolatePosition` | bool | true | 位置插值（false=瞬移效果） |
| `interpolateRotation` | bool | true | 旋转插值 |
| `interpolateScale` | bool | true | 缩放插值 |
| `coordinateSpace` | CoordinateSpace | Local | 坐标空间：Local/World |
| `sendIntervalMultiplier` | uint | (计算值) | 基于 syncInterval 的倍数 |
| `timelineOffset` | bool | true | 时间线偏移 |

**枚举：**
```csharp
public enum CoordinateSpace { Local, World }
public enum UpdateMethod { Update, FixedUpdate, LateUpdate }
```

## NetworkTransformReliable

AddComponentMenu: "Network/Network Transform (Reliable)"

同步方式：`SyncMethod.Reliable`，位置/缩放使用量化 + Delta + VarInt 压缩。

| 配置 | 类型 | 默认 | 范围 | 说明 |
|------|------|------|------|------|
| `onlySyncOnChangeCorrectionMultiplier` | float | 2 | — | 仅变化同步时的修正倍数 |
| `rotationSensitivity` | float | 0.01 | — | 旋转变化检测阈值（度） |
| `positionPrecision` | float | 0.01 | 0.0001-1 | 位置量化精度（0.01=1cm） |
| `scalePrecision` | float | 0.01 | 0.0001-1 | 缩放量化精度 |

**内部机制：**
- `lastSerializedPosition` / `lastDeserializedPosition`：Delta 压缩基线
- 旋转：`Quaternion.Angle > rotationSensitivity` 时检测为变化
- 传送时调用 `base.ResetState()` 而非 `ResetState()`，以保留 Delta 压缩基线

## NetworkTransformUnreliable

AddComponentMenu: "Network/Network Transform (Unreliable)"

同步方式：不可靠 Cmd/Rpc 通道，逐轴变化检测。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `bufferResetMultiplier` | float | 3 | 缓冲区重置倍数 |
| `positionSensitivity` | float | 0.01 | 位置变化阈值（每轴） |
| `rotationSensitivity` | float | 0.01 | 旋转变化阈值（每欧拉角） |
| `scaleSensitivity` | float | 0.01 | 缩放变化阈值（幅度） |

**内部机制：**
- 使用 `Changed` 位掩枚举（PosX/PosY/PosZ/RotX/RotY/RotZ/Rot/CompressRot/Scale/None）
- Cmd/Rpc 走 `Channels.Unreliable`
- `compressRotation=true` 时用 `Quaternion.Angle` 整体比较

## NetworkTransformHybrid

AddComponentMenu: "Network/Network Transform (Hybrid)"

同步方式：可靠基线 + 不可靠 Delta。`initialState=true` 走可靠，`initialState=false` 走不可靠。

| 配置 | 类型 | 默认 | 范围 | 说明 |
|------|------|------|------|------|
| `onlySyncOnChangeCorrectionMultiplier` | float | 2 | — | 仅变化同步时的修正倍数 |
| `rotationSensitivity` | float | 0.01 | — | 旋转变化检测阈值 |
| `positionPrecision` | float | 0.01 | 0.0001-1 | 位置量化精度 |
| `rotationPrecision` | float | 0.001 | 0.0001-1 | 四元数分量量化精度 |
| `scalePrecision` | float | 0.01 | 0.0001-1 | 缩放量化精度 |
| `debugDraw` | bool | false | — | 调试绘制 |

**内部机制：**
- `compressRotation=false`：旋转使用 `Vector4Long` Delta 压缩（量化四元数 xyzw）
- `compressRotation=true`：旋转使用 smallest-three 压缩（无 Delta）
- Delta 压缩基线仅在可靠全状态序列化时更新
- 传送时调用 `base.ResetState()` 保留基线

## 传送方法

所有传送方法定义在 `NetworkTransformBase`，无变体覆盖。

| 方法 | 签名 | 属性 | 说明 |
|------|------|------|------|
| `CmdTeleport` | `void CmdTeleport(Vector3 destination)` | `[Command]` | 客户端→服务器 |
| `CmdTeleport` | `void CmdTeleport(Vector3 destination, Quaternion rotation)` | `[Command]` | 客户端→服务器 |
| `RpcTeleport` | `void RpcTeleport(Vector3 destination)` | `[ClientRpc]` | 服务器→客户端 |
| `RpcTeleport` | `void RpcTeleport(Vector3 destination, Quaternion rotation)` | `[ClientRpc]` | 服务器→客户端 |
| `ServerTeleport` | `void ServerTeleport(Vector3 destination, Quaternion rotation)` | `[Server]` | 服务器端直接+广播 |

**注意：** `ServerTeleport` 只有 `(Vector3, Quaternion)` 重载，无仅位置版本。NetworkRigidbody 变体覆盖 `OnTeleport` 以额外设置 `rb.position` / `rb.rotation`。

## NetworkRigidbody 系列

在 NetworkTransform 基础上增加 Rigidbody 处理：非权威端自动 `isKinematic = true`，权威端恢复原始状态。

| 组件 | 基类 | 菜单名 | 用途 |
|------|------|--------|------|
| NetworkRigidbodyReliable | NetworkTransformReliable | Network Rigidbody (Reliable) | 3D刚体+可靠 |
| NetworkRigidbodyUnreliable | NetworkTransformUnreliable | Network Rigidbody (Unreliable) | 3D刚体+不可靠 |
| NetworkRigidbodyHybridCORE | NetworkTransformHybrid | Network Rigidbody Hybrid (CORE) | 3D刚体+混合 |
| NetworkRigidbodyReliable2D | NetworkTransformReliable | Network Rigidbody 2D (Reliable) | 2D刚体+可靠 |
| NetworkRigidbodyUnreliable2D | NetworkTransformUnreliable | Network Rigidbody 2D (Unreliable) | 2D刚体+不可靠 |

**共同行为：**
- `Awake()`：缓存 Rigidbody 和原始 `isKinematic` 状态
- `FixedUpdate()`：非权威端设 `isKinematic = true`
- `OnStopServer/Client`：恢复原始 `isKinematic`
- 覆盖 `OnTeleport`：额外设置 `rb.position` / `rb.rotation`

**2D 变体差异：**
- 使用 `Rigidbody2D` 而非 `Rigidbody`
- Unity 6+：使用 `rb.bodyType` / `RigidbodyType2D.Kinematic`
- 旋转传送：仅设置 Z 轴 `rb.rotation = transform.rotation.eulerAngles.z`

## 快照插值设置

基于 Glenn Fiedler 快照插值算法，配置在 `SnapshotInterpolationSettings`（通过 NetworkManager.snapshotSettings 访问）。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `bufferTimeMultiplier` | double | 2 | 缓冲倍数（乘以 sendInterval） |
| `bufferLimit` | int | 32 | 最大缓冲快照数 |
| `catchupNegativeThreshold` | float | -1 | 触发追赶的时间偏差下限(秒) |
| `catchupPositiveThreshold` | float | 1 | 触发追赶的时间偏差上限(秒) |
| `catchupSpeed` | double | 0.02 | 追赶速度（0-1） |
| `slowdownSpeed` | double | 0.04 | 减速速度（0-1） |
| `driftEmaDuration` | int | 1 | 漂移EMA持续时间(秒) |
| `dynamicAdjustment` | bool | true | 根据网络抖动自动调整缓冲 |
| `dynamicAdjustmentTolerance` | float | 1 | 动态调整容差 |
| `deliveryTimeEmaDuration` | int | 2 | 送达时间EMA持续时间(秒) |

**运行时计算：**
- `initialBufferTime = NetworkServer.sendInterval * bufferTimeMultiplier`
- `bufferTime` 在 `dynamicAdjustment=true` 时根据抖动动态调整
- 超前时减速（slowdown），落后时加速（catchup）
