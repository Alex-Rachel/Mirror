# NetworkReader / NetworkWriter 序列化

## 目录

- [核心 API](#核心-api)
- [池化](#池化)
- [支持类型一览](#支持类型一览)
- [自定义读写函数](#自定义读写函数)
- [VarInt 压缩](#varint-压缩)
- [NetworkIdentity/Behaviour 序列化](#networkidentitybehaviour-序列化)
- [空值编码约定](#空值编码约定)
- [分配保护](#分配保护)

---

## 核心 API

```csharp
// 写入
NetworkWriter writer = new NetworkWriter();
writer.WriteInt(health);
writer.WriteString(name);
writer.WriteVector3(position);

// 读取
NetworkReader reader = new NetworkReader(writer.ToArraySegment());
int health = reader.ReadInt();
string name = reader.ReadString();
Vector3 position = reader.ReadVector3();

// 泛型（Weaver 自动填充 Reader<T>.read / Writer<T>.write）
writer.Write(myStruct);
MyStruct s = reader.Read<MyStruct>();
```

**NetworkWriter 关键属性：** `Position`（可设置）、`Capacity`（自动扩容）

**NetworkReader 关键属性：** `Position`（可设置）、`Remaining`、`Capacity`

## 池化

避免 GC 分配，使用 `using` 模式：

```csharp
// 写入
using (NetworkWriterPooled writer = NetworkWriterPool.Get())
{
    writer.WriteInt(health);
    NetworkServer.SendToAll(writer.ToArraySegment(), channelId);
} // 自动归还池

// 读取
using (NetworkReaderPooled reader = NetworkReaderPool.Get(data))
{
    int health = reader.ReadInt();
} // 自动归还池
```

池预分配 1000 个实例。`Get()` 自动 Reset。

## 支持类型一览

| 分类 | 类型 | Nullable? |
|------|------|-----------|
| 整数 | byte, sbyte, short, ushort, int, uint, long, ulong | Yes |
| 浮点 | float, double, decimal, Half | Yes (除 Half) |
| 布尔/字符 | bool, char | Yes |
| 字符串 | string | null=0 前缀 |
| Unity 数学 | Vector2/3/4, Vector2Int/3Int, Color/Color32, Quaternion, Rect, Plane, Ray, LayerMask, Matrix4x4 | Yes |
| Guid | Guid | Yes |
| 日期 | DateTime | Yes |
| 网络 | NetworkIdentity, NetworkBehaviour, Transform, GameObject | null=netId 0 |
| 集合 | List\<T\>, T[], HashSet\<T\>, ArraySegment\<T\> | null=0 前缀 |
| 其他 | Uri, Texture2D, Sprite, byte[] | null 特殊编码 |

## 自定义读写函数

Weaver 自动为 struct/class 生成 Reader/Writer。也可手动注册：

```csharp
// 注册自定义写入
NetworkWriterExtensions.WriteExtension += (writer, value) =>
{
    writer.WriteInt(value.x);
    writer.WriteInt(value.y);
};

// 注册自定义读取（需匹配 Write 顺序）
NetworkReaderExtensions.ReadExtension += (reader) =>
{
    return new MyType { x = reader.ReadInt(), y = reader.ReadInt() };
};
```

**Weaver 自动生成条件：** struct 有无参构造函数；class 有无参构造函数且非 abstract/interface/MonoBehaviour/ScriptableObject/Object。

## VarInt 压缩

`[WeaverPriority]` 标记的 VarInt 方法，Weaver 优先选择以节省带宽：

```csharp
writer.WriteVarInt(score);   // 小值用更少字节
int score = reader.ReadVarInt();
```

SyncVar 中 int/uint/long/ulong 默认使用 VarInt 编码。

## NetworkIdentity/Behaviour 序列化

```csharp
// 写入：发送 netId（null/未生成 = 0）
writer.WriteNetworkIdentity(targetEnemy);
writer.WriteGameObject(projectile);
writer.WriteNetworkBehaviour(weapon);

// 读取：通过 spawned 字典查找
NetworkIdentity enemy = reader.ReadNetworkIdentity();    // netId=0 返回 null
GameObject proj = reader.ReadGameObject();
NetworkBehaviour weapon = reader.ReadNetworkBehaviour();
```

**注意：** 写入未生成的对象（netId=0）会输出警告。

## 空值编码约定

变长类型（string, byte[], List, Array, HashSet）使用 size+1 编码：
- `null` → 写入 VarUInt 0
- 空集合 → 写入 VarUInt 1（实际 count=0）
- N 个元素 → 写入 VarUInt N+1

Nullable 值类型使用 bool 前缀：`false` = null, `true` + 值。

## 分配保护

| 保护 | 限制 | 影响方法 |
|------|------|----------|
| 字符串最大长度 | 65534 字节 | WriteString |
| 读取分配上限 | 16 MB | ReadList, ReadArray, ReadBytes, ReadTexture2D |
