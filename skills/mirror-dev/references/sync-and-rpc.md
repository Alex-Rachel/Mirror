# 同步与远程调用

## 目录

- [SyncDirection](#syncdirection)
- [消息通道](#消息通道)
- [Sync 集合](#sync-集合)
- [SyncObject 回调](#syncobject-回调)

---

## SyncDirection

### ServerToClient（默认）

- 服务器序列化→发送给客户端
- 只有服务器可修改 SyncVar/SyncObject
- 适用：血量、分数、NPC 状态

### ClientToServer

- 拥有者客户端序列化→发送给服务器→服务器验证后广播
- 客户端可直接修改 SyncVar/SyncObject
- 服务器可在 `OnDeserialize` 中验证/拒绝
- 适用：玩家位置、客户端预测状态
- 设置方式：Inspector 中将 NetworkBehaviour 的 `syncDirection` 设为 `ClientToServer`

**authority 逻辑：**
- Host：ServerToClient 或拥有对象都有权威
- 纯客户端：只有 ClientToServer + 拥有对象才有权威
- 纯服务器：只有 ServerToClient 有权威

## 消息通道

```csharp
Channels.Reliable   // 有序，保证到达（默认）
Channels.Unreliable // 无序，可能丢失
```

| 场景 | 通道 |
|------|------|
| 游戏逻辑（开火、伤害、状态变更） | Reliable |
| 初始生成状态 | Reliable |
| 高频 Transform 同步 | Unreliable |
| 快节奏位置/旋转更新 | Unreliable |

## Sync 集合

| 需求 | 选择 | 操作 |
|------|------|------|
| 有序列表 | SyncList\<T\> | ADD, SET, INSERT, REMOVEAT, CLEAR |
| 键值映射 | SyncDictionary\<K,V\> | ADD, SET, REMOVE, CLEAR |
| 唯一成员集合 | SyncHashSet\<T\> | ADD, REMOVE, CLEAR |
| 唯一成员+有序迭代 | SyncSortedSet\<T\> | ADD, REMOVE, CLEAR |

**注意：** 非拥有者修改 SyncObject 会抛 `InvalidOperationException`。

SyncDictionary 索引器：`dict["hp"] = 100` — 键存在→SET，不存在→ADD。

## SyncObject 回调

```csharp
// SyncList — 参数为 index 或 (index, 旧值)
list.OnAdd = (index) => { };              // 新增项的 index
list.OnInsert = (index) => { };           // 插入项的 index
list.OnSet = (index, oldItem) => { };     // index + 被替换的旧值
list.OnRemove = (index, oldItem) => { };  // index + 被移除的旧值
list.OnClear = () => { };
// OnChange: T 的语义因 Operation 而异
//   ADD/INSERT → T 是新值 | SET/REMOVEAT → T 是旧值 | CLEAR → T 是 default
list.OnChange = (op, index, item) => { };       // Operation + index + 值(语义见上)
// Callback 提供 oldItem 和 newItem（SyncList 独有，SyncDictionary 无此回调）
list.Callback = (op, index, oldItem, newItem) => { }; // 4参数完整回调

// SyncDictionary（注意：没有 Callback，只有 OnChange）
dict.OnAdd = (key) => { };
dict.OnSet = (key, oldValue) => { };
dict.OnRemove = (key, oldValue) => { };
dict.OnClear = () => { };
// OnChange: TValue 的语义因 Operation 而异
//   ADD → TValue 是新值 | SET/REMOVE → TValue 是旧值 | CLEAR → TKey/TValue 是 default
dict.OnChange = (op, key, value) => { };

// SyncHashSet / SyncSortedSet
set.OnAdd = (item) => { };               // 新增的项
set.OnRemove = (item) => { };            // 被移除的项
set.OnClear = () => { };
set.OnChange = (op, item) => { };
```
