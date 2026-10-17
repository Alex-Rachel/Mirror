# 高级代码模板

> 低频模板，仅在需要自定义消息或认证时加载。

## 目录

- [自定义 NetworkMessage](#自定义-networkmessage)
- [自定义 NetworkAuthenticator](#自定义-networkauthenticator)

---

## 自定义 NetworkMessage

```csharp
public struct MyMessage : NetworkMessage
{
    public string text;
    public int value;
}

// 服务器端注册（接收连接+消息）
NetworkServer.RegisterHandler<MyMessage>(OnMyMessageServer);
// 客户端注册（只接收消息）
NetworkClient.RegisterHandler<MyMessage>(OnMyMessageClient);

void OnMyMessageServer(NetworkConnectionToClient conn, MyMessage msg) { /* ... */ }
void OnMyMessageClient(MyMessage msg) { /* ... */ }

// 发送
NetworkClient.Send(new MyMessage { text = "hello", value = 42 });  // C→S
conn.Send(new MyMessage { text = "response" });                     // S→指定连接
NetworkServer.SendToAll(new MyMessage { text = "broadcast" });      // S→所有
```

## 自定义 NetworkAuthenticator

```csharp
using Mirror;

public class MyAuthenticator : NetworkAuthenticator
{
    public struct AuthRequest : NetworkMessage { public string token; }
    public struct AuthResponse : NetworkMessage { public bool success; public string message; }

    public override void OnStartServer()
    {
        NetworkServer.RegisterHandler<AuthRequest>(OnAuthRequest);
    }

    public override void OnStartClient()
    {
        NetworkClient.RegisterHandler<AuthResponse>(OnAuthResponse);
    }

    public override void OnServerAuthenticate(NetworkConnectionToClient conn) { /* 等待请求 */ }
    public override void OnClientAuthenticate()
    {
        NetworkClient.Send(new AuthRequest { token = "my-token" });
    }

    void OnAuthRequest(NetworkConnectionToClient conn, AuthRequest msg)
    {
        // ServerAccept/ServerReject 是 protected 辅助方法，供子类在 OnServerAuthenticate 中调用
        if (ValidateToken(msg.token)) { conn.authenticationData = msg.token; ServerAccept(conn); }
        else ServerReject(conn);
    }

    void OnAuthResponse(AuthResponse msg)
    {
        // ClientAccept/ClientReject 是 protected 辅助方法，供子类在 OnClientAuthenticate 中调用
        if (msg.success) ClientAccept(); else ClientReject();
    }

    bool ValidateToken(string token) => token == "my-token";
}
```
