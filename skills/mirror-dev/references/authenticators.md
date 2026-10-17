# 内置认证器

## 目录

- [选择指南](#选择指南)
- [BasicAuthenticator](#basicauthenticator)
- [DeviceAuthenticator](#deviceauthenticator)
- [UniqueNameAuthenticator](#uniquenameauthenticator)
- [TimeoutAuthenticator](#timeoutauthenticator)

---

## 选择指南

```
用户名+密码 → BasicAuthenticator
设备唯一ID → DeviceAuthenticator（可伪造，非安全）
唯一用户名 → UniqueNameAuthenticator
需要超时 → TimeoutAuthenticator 包装上述任意一个
完全自定义 → 继承 NetworkAuthenticator（见 templates-advanced.md）
```

## BasicAuthenticator

用户名/密码认证。Inspector 配置服务器和客户端凭据。

| 配置 | 类型 | 说明 |
|------|------|------|
| `serverUsername` | string | 服务器端期望的用户名 |
| `serverPassword` | string | 服务器端期望的密码 |
| `username` | string | 客户端发送的用户名 |
| `password` | string | 客户端发送的密码 |

**流程：** 客户端发送 `AuthRequestMessage{authUsername, authPassword}` → 服务器比对 → 成功返回 code=100 + `ServerAccept` / 失败返回 code=200 + 延迟1秒 `ServerReject`。

## DeviceAuthenticator

设备唯一标识认证。无 Inspector 配置。始终接受连接。

**流程：** 客户端自动读取 `SystemInfo.deviceUniqueIdentifier`（不支持时回退到 PlayerPrefs 中的 GUID）→ 发送 `AuthRequestMessage{clientDeviceID}` → 服务器存入 `conn.authenticationData` → `ServerAccept`。

**安全警告：** deviceUniqueIdentifier 可被伪造，不保证安全。

## UniqueNameAuthenticator

唯一用户名认证。服务器端静态 HashSet 跟踪已连接用户名。

| 配置 | 类型 | 说明 |
|------|------|------|
| `playerName` | string | 客户端发送的用户名 |

| 事件 | 类型 | 说明 |
|------|------|------|
| `OnAuthSuccess` | UnityEvent\<string\> | 客户端认证成功 |
| `OnAuthFailure` | UnityEvent\<string\> | 客户端认证失败 |

**流程：** 客户端发送 `AuthRequestMessage{authUsername}` → 服务器检查 `playerNames` 唯一性 → 成功：加入 HashSet + 存入 `conn.authenticationData` + `ServerAccept` / 失败：延迟1秒 `ServerReject`。

**客户端失败行为：** 调用 `NetworkManager.singleton.StopHost()` + 触发 `OnAuthFailure`。

**UI 集成：** `SetPlayername(string)` 方法供输入框调用。

## TimeoutAuthenticator

装饰器模式，为任意认证器添加超时断开。

| 配置 | 类型 | 默认 | 说明 |
|------|------|------|------|
| `authenticator` | NetworkAuthenticator | null | 被包装的认证器 |
| `timeout` | float | 60 | 超时秒数（0=无超时） |

**行为：** 委托所有生命周期方法给内部认证器，额外启动协程等待超时。超时后若 `conn.isAuthenticated` 仍为 false，调用 `conn.Disconnect()`。

**典型配置：** TimeoutAuthenticator → 内嵌 BasicAuthenticator，timeout=30 秒。
