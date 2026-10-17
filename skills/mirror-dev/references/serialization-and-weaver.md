# 序列化与 Weaver

## 目录

- [自定义序列化](#自定义序列化)
- [Weaver 概要](#weaver-概要)
- [带宽优化](#带宽优化)
- [NetworkTime](#networktime)

---

## 自定义序列化

当 SyncVar 不够灵活时，完全自定义序列化：

```csharp
public class CustomSync : NetworkBehaviour
{
    int health;
    bool healthDirty;

    public void SetHealth(int value)
    {
        health = value;
        healthDirty = true;
        SetDirty();
    }

    public override bool OnSerialize(NetworkWriter writer, bool initialState)
    {
        if (initialState)
        {
            writer.WriteInt(health);
            return true;
        }

        writer.WriteBool(healthDirty);
        if (healthDirty)
        {
            writer.WriteInt(health);
            healthDirty = false;
        }
        return healthDirty;
    }

    public override void OnDeserialize(NetworkReader reader, bool initialState)
    {
        if (initialState)
        {
            health = reader.ReadInt();
            return;
        }

        if (reader.ReadBool())
        {
            health = reader.ReadInt();
        }
    }
}
```

**标记脏数据：**
- `SetDirty()` — 通用脏标记
- `SetSyncVarDirtyBit(ulong)` — 指定位掩码的脏标记

**OnSerialize 返回值：** `true` 表示有脏数据写入了 writer，`false` 表示没有变化。

## Weaver 概要

Weaver 基于 Cecil/ILPostProcessor，编译后自动处理：

| Attribute | Weaver 处理 |
|-----------|-------------|
| `[SyncVar]` | 生成属性 + 脏位 + 序列化，替换字段访问为属性访问 |
| `[Command]` | 原方法体移到 `UserCode_`，替换为 Host 直接调用 / 否则序列化+Send |
| `[ClientRpc]` | 同 Command，方向相反（服务器 Send → 客户端执行） |
| `[TargetRpc]` | 同 ClientRpc，但发送给指定连接 |
| `[Server]` | 注入 `NetworkServer.active` 检查，不活跃时返回 |
| `[ServerCallback]` | 注入 `NetworkServer.active` 检查，不活跃时静默返回（无警告） |
| `[Client]` | 注入 `NetworkClient.active` 检查，不活跃时返回 |
| `[ClientCallback]` | 注入 `NetworkClient.active` 检查，不活跃时静默返回（无警告） |

## 带宽优化

| 技术 | 说明 |
|------|------|
| `onlySyncOnChange` | 静止时不发送 |
| `syncInterval` | 低频数据用大间隔 |
| Delta 压缩 | Reliable/Hybrid 自动量化+Delta+VarInt |
| 合适的 AOI | 只同步附近对象 |
| `SyncMode.Owner` | 只同步给拥有者 |
| Unreliable 通道 | 高频可丢失数据 |
| 减少 SyncVar 数量 | 合并为结构体 |
| 手动序列化 | OnSerialize/OnDeserialize 精确控制 |

## NetworkTime

| 属性 | 服务器 | 客户端 | 说明 |
|------|--------|--------|------|
| `localTime` | 本地时钟 | 本地时钟 | 不受 timeScale 影响 |
| `time` | = localTime | 插值后服务器时间 | 用于渲染 |
| `predictedTime` | = localTime | localTime + 预测误差 | 用于客户端预测 |

**Ping/Pong 机制：** 服务器定期发送 Ping，客户端回复 Pong，用于计算 RTT 和时间同步。
