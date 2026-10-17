# 场景管理

## 目录

- [基本场景切换](#基本场景切换)
- [Additive Scenes](#additive-scenes)
- [场景切换回调](#场景切换回调)

---

## 基本场景切换

```csharp
// 服务器发起场景切换
NetworkManager.singleton.ServerChangeScene("GameScene");
```

**自动流程：**
1. `SetAllClientsNotReady()` — 所有客户端设为 NotReady
2. `isLoadingScene = true` — 暂停消息处理
3. `OnServerChangeScene(newSceneName)` 回调
4. 服务器 `LoadSceneAsync`
5. 广播 `SceneMessage` 给所有客户端
6. 客户端 `LoadSceneAsync`
7. 加载完成后 `SpawnObjects` + 回调

## Additive Scenes

```csharp
// 服务器发送加载附加场景消息
NetworkServer.SendToAll(new SceneMessage
{
    sceneName = "SubScene",
    sceneOperation = SceneOperation.LoadAdditive
});

// 给特定连接加载附加场景
conn.Send(new SceneMessage
{
    sceneName = "ZoneB",
    sceneOperation = SceneOperation.LoadAdditive
});

// 卸载附加场景
NetworkServer.SendToAll(new SceneMessage
{
    sceneName = "SubScene",
    sceneOperation = SceneOperation.UnloadAdditive
});
```

**SceneOperation 枚举：**

| 值 | 说明 |
|----|------|
| `Normal` (0) | 完整场景替换 |
| `LoadAdditive` (1) | 加载附加场景 |
| `UnloadAdditive` (2) | 卸载附加场景 |

**自定义场景加载：** 设置 `customHandling = true`，客户端跳过内置 `SceneManager.LoadSceneAsync`，由 `OnClientChangeScene` 重写处理。

## 场景切换回调

| 回调 | 时机 | 典型用途 |
|------|------|----------|
| `OnServerChangeScene(string)` | 服务器开始加载前 | 保存状态、清理 |
| `OnServerSceneChanged(string)` | 服务器加载完成后 | 生成对象、初始化 |
| `OnClientChangeScene(string, SceneOperation, bool)` | 客户端开始加载前 | 自定义加载逻辑 |
| `OnClientSceneChanged()` | 客户端加载完成后 | Ready+AddPlayer |

**关键行为：**
- `OnClientSceneChanged` 默认对 Normal 操作调用 `NetworkClient.Ready()` + `NetworkClient.AddPlayer()`，Additive 操作不自动 AddPlayer
- Host 模式下客户端侧跳过场景加载（服务器已加载）
- `customHandling = true` 时，NetworkManager 不会自动调用 `LoadSceneAsync`
