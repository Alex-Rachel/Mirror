# 客户端预测与输入处理

## 目录

- [输入处理模式](#输入处理模式)
- [PredictedRigidbody](#predictedrigidbody)
- [NetworkAnimator](#networkanimator)

---

## 输入处理模式

### 模式 A：NetworkTransform + ClientToServer（最简单）

休闲/非竞技游戏。设置 `NetworkTransform.syncDirection = ClientToServer`，无需 Command。

```csharp
void Update()
{
    if (!isOwned) return;
    Vector3 move = new Vector3(Input.GetAxis("Horizontal"), 0, Input.GetAxis("Vertical"));
    controller.Move(move * speed * Time.deltaTime);
}
```

### 模式 B：PredictedRigidbody + Command（零延迟）

竞技游戏，即时反馈。客户端立即应用输入，同时发送到服务器验证。

```csharp
void FixedUpdate()
{
    if (!isOwned) return;
    Vector3 force = GetInputForce();
    predictedRb.rb.AddForce(force, ForceMode.Impulse);  // 本地立即应用
    CmdApplyForce(force);                                 // 服务器验证
}

[Command]
void CmdApplyForce(Vector3 force)
{
    if (IsValidMove(force))
        predictedRb.rb.AddForce(force, ForceMode.Impulse);
}
```

### 模式 C：服务器权威 Command（最安全）

防作弊游戏。客户端有延迟，服务器完全控制。

```csharp
[Command]
void CmdMove(Vector3 direction)
{
    rb.AddForce(direction * speed, ForceMode.Impulse);
}
```

## PredictedRigidbody

### PredictionMode 枚举

| 模式 | 说明 |
|------|------|
| `Smooth` | 物理+碰撞体移到 Ghost 对象，视觉对象平滑跟随。碰撞可能看起来偏软 |
| `Fast` | 直接在原对象上修正，使用 MovePosition/MoveRotation。强制 `RigidbodyInterpolation.Interpolate` |

### 完整配置

#### 运动平滑

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `mode` | PredictionMode | Smooth | Smooth vs Fast 预测模式 |
| `motionSmoothingVelocityThreshold` | float | 0.1 | 启动/停止 Ghost 的速度阈值 |
| `motionSmoothingAngularVelocityThreshold` | float | 5.0 | 启动/停止 Ghost 的角速度阈值 |
| `motionSmoothingTimeTolerance` | float | 0.5 | Ghost 最短活跃时间（防止闪烁） |

#### 状态历史

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `stateHistoryLimit` | int | 32 | 最大历史条目（32 x 50ms = 1.6秒） |
| `recordInterval` | float | 0.050 | 记录间隔(秒) |
| `onlyRecordChanges` | bool | true | 仅在状态实际变化时插入 |
| `compareLastFirst` | bool | true | 先与最后记录比较再遍历历史 |

#### 修正

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `positionCorrectionThreshold` | double | 0.10 | 位置修正阈值(米) |
| `rotationCorrectionThreshold` | double | 5 | 旋转修正阈值(度) |
| `oneFrameAhead` | bool | true | 服务器修正提前一帧应用 |

#### 对齐

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `snapThreshold` | float | 2 | 速度低于此值时硬对齐到服务器状态 |

#### 视觉插值

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `showGhost` | bool | true | 显示 Ghost 调试材质 |
| `ghostVelocityThreshold` | float | 0.1 | 物理组件移到 Ghost 的速度阈值 |
| `localGhostMaterial` | Material | null | 本地预测 Ghost 材质 |
| `remoteGhostMaterial` | Material | null | 远程服务器状态 Ghost 材质 |
| `checkGhostsEveryNthFrame` | int | 4 | 每N帧检查一次 Ghost 创建/销毁 |
| `positionInterpolationSpeed` | float | 15 | 视觉对象跟随物理副本的位置速度 |
| `rotationInterpolationSpeed` | float | 10 | 视觉对象跟随物理副本的旋转速度 |
| `teleportDistanceMultiplier` | float | 10 | 超过 colliderSize x multiplier 时瞬移 |

#### 带宽

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `reduceSendsWhileIdle` | bool | true | 静止时降低发送频率（移动时 syncInterval=0，静止时=1） |

### 预测/修正循环

**客户端录制（FixedUpdate）：**
1. 仅在 `isClientOnly` 时运行
2. 使用 `NetworkTime.predictedTime`（非 `NetworkTime.time`）作为时间戳
3. 强制 `recordInterval` 间隔，保持 `stateHistoryLimit` 上限
4. 存储绝对值和 Delta（用于修正后重放）

**客户端接收服务器状态（OnDeserialize）：**
1. 读取 `PredictedSyncData`（blittable：deltaTime, position, rotation, velocity, angularVelocity）
2. 获取 `remoteTimeStamp`，加上 `serverDeltaTime`（+ 可选 `oneFrameAhead`）
3. 调用 `OnReceivedState(timestamp, RigidbodyState)`

**修正逻辑（OnReceivedState）：**
1. 快速路径：若 `compareLastFirst=true` 且当前状态在阈值内，跳过
2. 确保历史密集（记录当前状态）
3. 需要至少 2 条历史记录
4. 状态早于最旧历史：硬修正
5. 状态晚于最新历史：仅超出位置阈值时硬修正
6. 正常情况：`Prediction.Sample()` 找到包围时间戳的两条记录，插值比较
7. 超出阈值时：`Prediction.CorrectHistory()` 插入修正 + 重放后续所有 Delta

**应用状态（ApplyState）：**
- 速度 <= `snapThreshold`：硬对齐，清空历史，触发 `OnSnappedIntoPlace()`
- 速度 > `snapThreshold`：
  - **Smooth**：直接设置 `predictedRigidbody.position/rotation`（物理副本瞬移不可见，视觉平滑跟随）
  - **Fast**：使用 `MovePosition()` / `MoveRotation()`

### Ghost 对象机制

Ghost 系统将物理与渲染分离，仅在对象移动时按需创建。

1. **创建**（`CreateGhosts`）：创建 `{name}_Physical` 对象，将 Rigidbody/Collider/Joint 从原始对象移到 Ghost
2. **视觉跟随**（`LateUpdate`）：原始对象通过插值平滑跟随物理副本
3. **销毁**（`DestroyGhosts`）：对象停止移动后（超过 `motionSmoothingTimeTolerance`），将物理组件移回原始对象

**关键：** `predictedRb` 字段始终指向当前活跃的 Rigidbody（Ghost 或原始对象）。

### 虚方法回调

| 方法 | 时机 |
|------|------|
| `OnSnappedIntoPlace()` | 硬对齐到服务器状态后 |
| `OnBeforeApplyState()` | 应用修正前（非对齐） |
| `OnCorrected()` | 修正应用后 |
| `OnBeginPrediction()` | 开始移动（Ghost 创建） |
| `OnEndPrediction()` | 停止移动（Ghost 销毁） |

## NetworkAnimator

同步 Mecanim 动画状态。默认 `syncDirection = ClientToServer`（`Reset()` 中设置）。

### syncDirection

| 方向 | 说明 | SetTrigger/ResetTrigger 调用条件 |
|------|------|-------------------------------|
| `ServerToClient` | 服务器控制动画 | 仅服务器可调用 |
| `ClientToServer` | 拥有者客户端控制 | 客户端需 `isOwned` |

### 公共方法

```csharp
networkAnimator.SetTrigger("Jump");     // 按名称触发
networkAnimator.SetTrigger(hash);       // 按哈希触发
networkAnimator.ResetTrigger("Jump");   // 按名称重置
networkAnimator.ResetTrigger(hash);     // 按哈希重置

// 设置动画速度：直接修改 animator.speed，NetworkAnimator 自动检测变化并同步
// （animatorSpeed 是 private SyncVar，不可直接设置）
animator.speed = 1.5f;
```

**Trigger 不走参数脏检查系统**，必须通过 `SetTrigger`/`ResetTrigger` 显式调用。

### 同步内容

**初始序列化（initialState=true）：**
- `byte layerCount`
- 每层：`int fullPathHash` + `float normalizedTime` + `float weight`
- 所有参数（Int/Float/Bool，强制全发）

**运行时同步：**
- 动画状态变化（层权重/过渡/状态切换）
- 参数变化（Int/Float/Bool，Float 容差 0.001f）
- Animator speed 变化（容差 0.001f）
- Trigger（显式调用时发送）

**不同步：**
- Trigger 参数不在脏检查系统中
- 由动画曲线控制的参数（初始化时过滤）
- 过渡状态本身（只播放目标状态 `animator.Play()`）
