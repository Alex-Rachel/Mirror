# Relay Lobby 完整示例

Unity **6000.3.24f1**、Windows Standalone、.NET **8**。同一场景中创建或加入房间，房主按 Start 后解锁移动与计分，同时禁止新成员加入。

## 直接运行

在仓库根目录运行：

```powershell
./RelayServer/Scripts/Start-RelayServer.ps1
```

打开 `Assets/Mirror/Examples/RelayLobby/Scenes/RelayLobby.unity` 并进入 Play。默认 HTTP 地址为 `http://localhost:9001`，UDP 中继为 `127.0.0.1:9000`。

1. 填写房间名、玩家名、容量和标签，按 **Create Host**。
2. 启动另外两份示例播放器，填写不同玩家名，Search 后 Select 房间，再按 **Join Room**。
3. 房主等待成员列表完整后按 **Start**。使用 WASD 或方向键移动；移动时服务端增加分数。输入框获得焦点时移动被屏蔽。
4. 房主可 Save 设置、切换 Private、复制邀请信息、Kick 成员或 Close 房间；普通成员可 Leave。公开转私密后会生成新邀请。私密房间通过 Room ID 和 Private invitation 加入。
5. 左侧面板可用鼠标滚轮上下滚动；房间列表和成员列表也支持独立滚动。房主操作位于 CURRENT ROOM 区域。

HTTP、Relay 心跳和 Mirror 连接分别显示状态。操作错误会保留到下一次成功操作。后台刷新保留尚未 Save 的房间编辑内容。

连接另一台机器时，客户端只需修改 **HTTP endpoint** 并 Apply；服务端启动时设置 `-RelayAddress` 为所有客户端可达的 IP 或域名。HTTP 响应中的 UDP 地址是最终连接依据，客户端 `relayAddress` 字段仅供传统 UDP 接口使用。已开始的房间不支持重新加入；退出后只能清理并加入仍开放的房间。

部分系统代理会拦截独立播放器的 `127.0.0.1` HTTP 请求并返回 502；本机示例和进程脚本使用 `localhost`。UDP 地址不受该代理设置影响。

## 编辑器中的资产

- `Scenes/RelayLobby.unity`：管理器、Bootstrap、Canvas 实例、EventSystem、摄像机、地面和八个出生点。
- `Prefabs/RelayLobbyUI.prefab`：完整 uGUI 控件及序列化引用，可直接在编辑器调整。
- `Prefabs/RelayLobbyRoomEntry.prefab`、`RelayLobbyMemberEntry.prefab`：列表条目。
- `Prefabs/RelayLobbyPlayer.prefab`：NetworkIdentity、服务端方向 NetworkTransformReliable、玩家模型和名称／分数标签。

主要 UI 不在运行时拼装。正常使用只实例化列表条目和玩家。Unity 6 字体使用 `LegacyRuntime.ttf`。

菜单 **Mirror / Relay Lobby / Rebuild example assets** 可以重新生成整套示例资产；此操作会覆盖示例布局，定制布局后请直接编辑现有场景／预制体。**Build Windows example** 只构建 RelayLobby 场景。

玩家名称、颜色、分数和开始标志都是持久 SyncVar。服务端保存最后输入，在 FixedUpdate 中积分移动，超过 0.35 秒未更新的输入停止生效；命令数量不会决定移动距离。计分规则为有有效移动输入时每个服务端物理更新加一分，上限 999999。

## 构建和验收

```powershell
# 专用 Windows 开发构建，只包含 RelayLobby 场景
./RelayServer/Scripts/Build-RelayLobby.ps1

# 服务端测试 → Unity EditMode/PlayMode → Windows 构建 → 独立进程验收
./RelayServer/Scripts/Test-RelayLobby.ps1

# 使用已有播放器单独检查进程场景
./RelayServer/Scripts/Test-RelayLobbyProcesses.ps1

# 真实图形播放器：三种分辨率的等待／开始画面
./RelayServer/Scripts/Test-RelayLobbyVisuals.ps1
```

播放器输出：`Builds/RelayLobby/RelayLobby.exe`。日志、XML、JSON 和截图输出：`Artifacts/RelayLobby/`，这些本地产物已被 Git 忽略。测试只停止自己启动的进程。

批处理构建和 Unity 测试要求目标工程未被另一个编辑器占用；编辑器中可直接使用 Test Runner 和上述构建菜单。脚本也接受 `-Project <独立验证工程目录>`、`-Unity <Unity.exe>`。构建仅在调用期间设置 HTTP 为 DevelopmentOnly，并在 finally 恢复原设置，不修改全局场景构建列表。

进程验收默认使用 HTTP 19001、UDP 19000。覆盖房主和两名客户端、名称颜色、等待门控、开始同步、移动计分、离开／重新加入／踢人隔离、晚加入 409、关闭房间、HTTP 与 UDP 分别停止、杀死房主、停止服务和清理后的重试。界面脚本使用 20000／20001 端口。

## Bootstrap 自动化接口

没有 `-relayRole` 时 Bootstrap 不执行自动连接。所有参数支持 `-参数=值` 和 `-参数 值`：

| 参数 | 作用 |
| --- | --- |
| `-relayRole host\|client\|observer` | 创建、加入或仅观察场景 |
| `-relayHttp http://localhost:9001` | HTTP 控制面地址 |
| `-relayRoom <roomId>` | 加入的公开房间 ID |
| `-relayInvite <inviteCode>` | 私密邀请；勿写入共享脚本或报告 |
| `-relayName <name>` | 玩家名 |
| `-relayOutput <absolute.json>` | 安全状态报告，每 0.25 秒刷新 |
| `-relayCommands <absolute.json>` | 本机测试命令文件 |
| `-relayKeepAlive true` | 启动失败后保留进程以测试重试 |
| `-relayAutoMove true` | 显式测试输入；开始前仍不能移动 |
| `-relayScreenshot <absolute.png>` | 启动约四秒后截图 |
| `-relayCaptureOffscreen true` | 隐藏图形播放器使用真实 Camera／Canvas 离屏渲染截图 |

阶段包括 HTTP 分配、Relay 就绪、Mirror 连接、玩家生成和状态同步。报告仅包含公开 ID、连接状态、玩家名称／颜色／分数／坐标及错误，不包含 ownerToken、memberToken、relayToken 或邀请。启动失败退出码为 2，显式 quit 为 0；进程验收脚本失败时抛出异常并保存失败报告。

本机命令格式为 `{"id":1,"action":"start"}`，id 必须递增。支持 start、kick（memberId）、leave、close、create、join（roomId／inviteCode）、endpoint（http）、screenshot（path，可选 roomControls）和 quit。命令文件不是远程接口。`-relayUdp` 已移除，避免覆盖服务端返回的地址。

服务端内存状态在重启后丢失。请求和启动默认超时 10 秒，认证 Ping 间隔 0.5 秒，Relay 失联阈值 15 秒。退出、被踢、关闭、断线和销毁都清理本地会话并尽力释放分配；HTTP 不可用时本地立即停止网络，远端分配最终由 UDP 离开或超时回收。这里不提供自动恢复会话、房主迁移或聊天／准备系统。

实际验收记录见 `VERIFICATION.md`。
