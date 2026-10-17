# 兴趣管理 (AOI) 选型与配置

## 目录

- [选择指南](#选择指南)
- [Visibility 枚举](#visibility-枚举)
- [架构原理](#架构原理)
- [DistanceInterestManagement](#distanceinterestmanagement)
- [SceneInterestManagement](#sceneinterestmanagement)
- [SceneDistanceInterestManagement](#scenedistanceinterestmanagement)
- [MatchInterestManagement](#matchinterestmanagement)
- [TeamInterestManagement](#teaminterestmanagement)
- [SpatialHashingInterestManagement](#spatialhashinginterestmanagement)
- [SpatialHashing3DInterestManagement](#spatialhashing3dinterestmanagement)
- [HexSpatialHash2DInterestManagement](#hexspatialhash2dinterestmanagement)
- [HexSpatialHash3DInterestManagement](#hexspatialhash3dinterestmanagement)
- [重建时机对比](#重建时机对比)
- [缺少组件行为对比](#缺少组件行为对比)

---

## 选择指南

```
简单/原型 → DistanceInterestManagement
隔离场景 → SceneInterestManagement
匹配/房间 → MatchInterestManagement（需NetworkMatch）
团队 → TeamInterestManagement（需NetworkTeam）
俯视/平面3D → SpatialHashingInterestManagement
3D有垂直 → SpatialHashing3DInterestManagement
大型开放世界(2D) → HexSpatialHash2DInterestManagement
大型开放世界(3D) → HexSpatialHash3DInterestManagement
大区域+距离 → SceneDistanceInterestManagement
```

| AOI | 复杂度 | 场景 | 性能 | 重建方式 |
|-----|--------|------|------|----------|
| Distance | 低 | 简单/原型 | O(N*M) 暴力 | 定时 |
| Scene | 低 | 隔离场景 | O(脏场景) | 事件驱动 |
| SceneDistance | 中 | 大区域+距离 | O(场景连接) | 定时+事件 |
| Match | 低 | 房间/匹配 | O(脏匹配) | 事件驱动 |
| Team | 低 | 团队 | O(脏团队) | 事件驱动 |
| SpatialHashing | 中 | 俯视3D | ~30x Distance | 定时(网格每帧刷新) |
| SpatialHashing3D | 中 | 3D | 快于Distance | 定时(网格每帧刷新) |
| HexSpatialHash2D | 高 | 大开放世界 | 最佳2D | 定时(含静态优化) |
| HexSpatialHash3D | 高 | 大3D开放世界 | 最佳3D | 定时(含静态优化) |

## Visibility 枚举

```csharp
public enum Visibility { Default, ForceHidden, ForceShown }
```

| 值 | 说明 |
|----|------|
| `Default` | 由 AOI 系统决定 |
| `ForceHidden` | 对所有观察者隐藏（如怪物重生中），拥有者自身仍可见 |
| `ForceShown` | 对所有客户端可见（如全局分数对象），无视 AOI |

**与 AOI 交互：**
- `ForceShown`：`OnRebuildObservers` 被跳过，直接添加所有就绪连接
- `ForceHidden`：`OnRebuildObservers` 被跳过，newObservers 为空（仅保留拥有者自身）
- `Default`：正常调用 AOI 的 `OnRebuildObservers`

## 架构原理

### 类层次

```
InterestManagementBase (抽象, MonoBehaviour)
├── OnCheckObserver(identity, newObserver) → bool  // 新连接可见性检查
├── Rebuild(identity, initialize)                  // 重建观察者集合
├── SetHostVisibility(identity, visible)            // Host模式隐藏/显示
└── InterestManagement (抽象, 添加 Rebuild 框架)
    └── OnRebuildObservers(identity, newObservers)  // 核心抽象方法
```

### Rebuild 算法

1. 清空 `newObservers`
2. 若非 `ForceHidden`，调用 `OnRebuildObservers(identity, newObservers)`
3. 始终添加 `identity.connectionToClient`（玩家总能看到自己）
4. 对比新旧观察者集合：添加新增、移除过时
5. 首次重建时，若 Host 本地连接不在 newObservers 中，调用 `SetHostVisibility(false)`

### Host 模式隐藏行为

`SetHostVisibility` 默认实现控制以下组件类型的 enabled 状态：

| 组件 | 隐藏时 | 说明 |
|------|--------|------|
| Renderer | enabled=false | 隐藏渲染 |
| Light | enabled=false | 隐藏灯光（手电筒/车灯） |
| AudioSource | enabled=false | 静音（喇叭声） |
| Canvas | enabled=false | 隐藏UI |
| Terrain | drawHeightmap=false, drawTreesAndFoliage=false | 隐藏地形 |
| ParticleSystem | emission.enabled=false | 停止粒子 |

## DistanceInterestManagement

暴力 O(N*M) 距离检查。遍历所有服务器连接，检查每个连接的玩家位置与对象位置的距离。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `visRange` | int | 500 | 可见距离上限 |
| `rebuildInterval` | float | 1 | 重建间隔(秒) |

**配套组件：** `DistanceInterestManagementCustomRange` — 添加到需要不同可见距离的对象上（`visRange` 默认 100）。

## SceneInterestManagement

对象只对同一 Unity Scene 中的玩家可见。使用 `Scene` 结构体（非字符串）以正确处理同名附加场景。

**重建方式：** 事件驱动 — 仅当对象切换场景时重建，无定时轮询。

## SceneDistanceInterestManagement

组合 Scene + Distance 过滤：观察者必须**在同一场景**且**在距离范围内**。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `visRange` | int | 500 | 可见距离上限 |
| `rebuildInterval` | float | 1 | 重建间隔(秒) |

支持 `DistanceInterestManagementCustomRange` 组件。重建方式：场景变化事件 + 定时。

## MatchInterestManagement

相同 `NetworkMatch.matchId`（Guid）的对象互相可见。`Guid.Empty` 无效 — 无 match 或空 matchId 的对象**永远不会被观察**。

**配套组件：** `NetworkMatch`（添加到需要匹配的对象上）

```csharp
// NetworkMatch 组件
var match = GetComponent<NetworkMatch>();
match.matchId = Guid.NewGuid();  // 服务器端设置
```

`matchId` setter 在运行时抛出 `InvalidOperationException`（如果不在活跃服务器上），变化时自动通知 AOI 重建。

**重建方式：** 事件驱动 — 仅当 match 变化时重建。

## TeamInterestManagement

相同 `NetworkTeam.teamId`（string）的对象互相可见。空/null/空白 teamId 无效。

**关键区别：** 没有 `NetworkTeam` 组件的对象**对所有客户端可见**（与 MatchInterestManagement 不同，Match 中无组件 = 永不可见）。

**配套组件：** `NetworkTeam`

| 字段 | 类型 | 说明 |
|------|------|------|
| `teamId` | string | 团队ID，setter 在运行时非服务器抛异常 |
| `forceShown` | bool | 为 true 时无视团队，对所有客户端可见（通常用于玩家对象） |

**重建方式：** 事件驱动 — 仅当 team 变化时重建。

## SpatialHashingInterestManagement

2D 空间哈希网格，将连接放入网格单元，9邻居检查（中心+8邻居）。比暴力距离检查快约 30 倍。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `visRange` | int | 30 | 可见距离上限 |
| `rebuildInterval` | float | 1 | 重建间隔(秒) |
| `checkMethod` | CheckMethod | XZ_FOR_3D | XZ=3D俯视, XY=2D横版 |

**网格分辨率：** `resolution = visRange / 2`（2个单元跨越可见距离）。

**重建方式：** 网格每帧刷新（连接重新定位），观察者按 `rebuildInterval` 定时重建。

## SpatialHashing3DInterestManagement

3D 空间哈希网格，27邻居检查（3x3x3立方体）。无 `CheckMethod` — 始终使用完整 3D 坐标。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `visRange` | int | 30 | 可见距离上限 |
| `rebuildInterval` | float | 1 | 重建间隔(秒) |

**网格分辨率：** `resolution = visRange / 2`。

## HexSpatialHash2DInterestManagement

六边形空间哈希，7邻居检查（中心+6个六边邻居）。支持静态对象优化（静态对象更新频率更低）。使用稀疏数组索引和对象池。

| 配置 | 类型 | 默认 | 范围 | 说明 |
|------|------|------|------|------|
| `visRange` | ushort | 1100 | 10-5000 | 超级六边形半径，设为相机远裁面+10% |
| `rebuildInterval` | byte | 1 | 1-60 | 重建间隔(秒) |
| `staticRebuildInterval` | byte | 10 | 1-60 | 静态对象重建间隔(秒) |
| `minMoveDistance` | ushort | 1 | 1-100 | 触发单元更新的最小移动距离 |
| `checkMethod` | CheckMethod | XZ_FOR_3D | — | XZ=3D, XY=2D |

**自动检测静态对象：** 通过 `Renderer.isPartOfStaticBatch` 判断。

## HexSpatialHash3DInterestManagement

3D 六边形空间哈希，将六边层垂直堆叠。21邻居检查（3层 x 7单元/层）。始终使用完整 3D 坐标。

| 配置 | 类型 | 默认 | 范围 | 说明 |
|------|------|------|------|------|
| `visRange` | ushort | 1100 | 10-5000 | 水平方向六边形半径 |
| `cellHeight` | ushort | 500 | 10-5000 | 垂直层高度 |
| `rebuildInterval` | byte | 1 | 1-60 | 重建间隔(秒) |
| `staticRebuildInterval` | byte | 10 | 1-60 | 静态对象重建间隔(秒) |
| `minMoveDistance` | ushort | 1 | 1-100 | 触发单元更新的最小移动距离 |

## 重建时机对比

| 实现 | 触发方式 | 间隔字段 | 默认值 |
|------|----------|----------|--------|
| Distance | LateUpdate 定时 | `rebuildInterval` (float) | 1s |
| Scene | 事件驱动（场景变化） | 无 | — |
| SceneDistance | 事件驱动 + 定时 | `rebuildInterval` (float) | 1s |
| Match | 事件驱动（match变化） | 无 | — |
| Team | 事件驱动（team变化） | 无 | — |
| SpatialHashing | Update 定时（网格每帧刷新） | `rebuildInterval` (float) | 1s |
| SpatialHashing3D | Update 定时（网格每帧刷新） | `rebuildInterval` (float) | 1s |
| HexSpatialHash2D | LateUpdate 定时 | `rebuildInterval` (byte) | 1s |
| HexSpatialHash3D | LateUpdate 定时 | `rebuildInterval` (byte) | 1s |

## 缺少组件行为对比

| 实现 | 对象无组件 | 观察者无组件 |
|------|-----------|-------------|
| Match | 永不被观察 | 不能观察任何对象 |
| Team | **对所有可见** | **能观察所有对象** |
