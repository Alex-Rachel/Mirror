# 核心代码模板

> 高频模板，按使用频率排序。完整可复制。

## 目录

- [NetworkBehaviour 完整骨架](#networkbehaviour-完整骨架)
- [SyncVar + Hook](#syncvar--hook)
- [Command 变体](#command-变体)
- [ClientRpc 变体](#clientrpc-变体)
- [TargetRpc](#targetrpc)

---

## NetworkBehaviour 完整骨架

```csharp
using Mirror;

public class MyNetworkObject : NetworkBehaviour
{
    [SyncVar(hook = nameof(OnHealthChanged))]
    int health = 100;

    public override void OnStartServer() { /* 初始化服务器状态 */ }
    public override void OnStopServer() { /* 清理服务器资源 */ }
    public override void OnStartClient() { /* 初始化客户端视觉 */ }
    public override void OnStopClient() { /* 清理客户端资源 */ }
    public override void OnStartLocalPlayer() { /* 启用输入、设置相机 */ }
    public override void OnStopLocalPlayer() { /* 清理本地玩家 */ }
    public override void OnStartAuthority() { /* 启用控制器 */ }
    public override void OnStopAuthority() { /* 禁用控制器 */ }

    void OnHealthChanged(int oldHealth, int newHealth) { /* 更新UI */ }

    [Command]
    void CmdDoSomething() { /* 服务器执行 */ }

    [ClientRpc]
    void RpcNotifyAll() { /* 所有观察者执行 */ }

    [TargetRpc]
    void TargetNotifyOne(NetworkConnectionToClient target) { /* 指定客户端执行 */ }
}
```

## SyncVar + Hook

```csharp
[SyncVar]
public int score;

[SyncVar(hook = nameof(OnColorChanged))]
public Color playerColor;

void OnColorChanged(Color oldColor, Color newColor)
{
    GetComponent<Renderer>().material.color = newColor;
}

// 客户端权威（Inspector 设置 syncDirection = ClientToServer）
[SyncVar]
public Vector3 clientPosition;
```

## Command 变体

```csharp
// 需要权威（默认）
[Command]
void CmdFire() { /* ... */ }

// 不需要权威（聊天等）
[Command(requiresAuthority = false)]
void CmdSendChat(string message) { /* ... */ }

// 获取发送者连接
[Command]
void CmdShoot(NetworkConnectionToClient conn = null)
{
    Debug.Log($"Shot from {conn}");
}

// 高频用不可靠通道
[Command(channel = Channels.Unreliable)]
void CmdMove(Vector3 position) { /* ... */ }
```

## ClientRpc 变体

```csharp
[ClientRpc]
void RpcOnFire() { animator.SetTrigger("Shoot"); }

// 排除拥有者（已本地预测）
[ClientRpc(includeOwner = false)]
void RpcScoreChanged(int score) { scoreText.text = score.ToString(); }

// 不可靠通道
[ClientRpc(channel = Channels.Unreliable)]
void RpcTransformUpdate(Vector3 pos, Quaternion rot) { /* ... */ }
```

## TargetRpc

```csharp
// 显式指定目标
[TargetRpc]
void TargetSyncInventory(NetworkConnectionToClient target, ItemData[] items)
{
    inventoryUI.UpdateItems(items);
}

// 默认使用 connectionToClient
[TargetRpc]
void TargetNotifyScore(int score) { scoreText.text = score.ToString(); }
```
