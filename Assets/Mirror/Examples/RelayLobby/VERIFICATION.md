# Relay Lobby 实际验收记录

验收日期：2026-10-17（Asia/Shanghai）。环境：Windows 11、Unity 6000.3.24f1、Windows Standalone x64 Development、服务端目标 .NET 8。

## 通过结果

| 验证层 | 实际结果 | 本地证据（相对仓库根目录） |
| --- | --- | --- |
| 独立服务端测试 | PASS：传统 UDP、HTTP 生命周期、MTU／权限／转发／隔离／超时 | `Artifacts/RelayLobby/server-tests.log` |
| Unity EditMode | 6/6 PASS | `Artifacts/RelayLobby/EditMode.xml`、`EditMode.log` |
| Unity PlayMode，加载保存的真实场景 | 4/4 PASS | `Artifacts/RelayLobby/PlayMode.xml`、`PlayMode.log` |
| Unity Windows 构建及 Mirror Weaver | PASS，生成可执行播放器 | `Artifacts/RelayLobby/build.log`、`Builds/RelayLobby/RelayLobby.exe` |
| 房主与两个独立客户端／故障注入 | 12/12 PASS | `Artifacts/RelayLobby/processes/results.json`，同目录各播放器与服务端日志 |
| 1280×720、1600×900、1920×1080 图形检查 | 等待、房主操作、游戏画面均已生成并检查 | `Artifacts/RelayLobby/visuals/*-waiting.png`、`*-waiting-controls.png`、`*-playing.png` |

运行使用当前本地源码和生成资产。因用户已打开原工程编辑器，最终批处理编译／构建／测试在 `Artifacts/RelayLobby/UnityValidation` 的独立工程副本中执行；保存的场景、预制体和材质已同步回原工程，36 个代码／配置／资产文件及 NetworkTransformBase 以 SHA-256 比较一致，清单为 `Artifacts/RelayLobby/source-manifest.json`。未关闭用户的编辑器，也未修改其未保存场景。

## 实际覆盖

服务端验证 1200 字节完整包／48 字节头／1152 字节 KCP 包边界，拒绝 1201 字节包和 1153 字节负载；校验房间、发送者、令牌和目标，拒绝客户端到客户端的游戏转发。验证带认证 Ping、独立控制／数据套接字、撤销后旧令牌失效、公开转私密新邀请、普通成员不能获取邀请、公开列表无凭据、单成员超时后房主与另一成员仍能转发，以及房主超时关闭房间。

Unity EditMode 检查场景／预制体全部关键引用、Canvas、EventSystem、滚动区域、滚动条、列表条目、出生点、Bootstrap、服务端方向 NetworkTransform 和布局宽高约束；直接新增 NetworkTransform 的 Reset 回归测试通过。

Unity PlayMode 实际加载 RelayLobby 场景，并连接独立服务端，验证：

1. 等待时不能移动／计分；开始后移动／计分；停止发送输入后按超时停止；退出清理。
2. HTTP 连接失败回滚，实际触发 Search 按钮确认失败不会误报成功，后台轮询不覆盖操作错误，清理后可重新创建。
3. 创建途中退出，旧请求不会恢复旧房间；随后可创建新房间。
4. 传统 UDP 控制套接字接收可及时取消，不阻塞操作队列；之后可使用真实服务重试。

独立进程验收的十二项：

1. 房主、Alice、Bob 三个进程分别生成玩家，名称／颜色同步，等待时零分。
2. Alice 离开，Bob 保持连接。
3. Alice 清理后重新加入。
4. 房主踢 Alice，Alice 清理，Bob 保持连接。
5. 开始状态持续同步，三个玩家移动及计分，观察坐标发生变化。
6. 开始后新进程加入得到 HTTP 409，且无残留凭据。
7. 关闭房间后全部进程清理。
8. 只停 HTTP：本地退出、加入失败、UDP 会话继续、HTTP 恢复后重试。
9. 只停 UDP：会话结束、启动超时回滚、UDP 恢复后重试。
10. 强制结束房主进程：两个客户端结束会话。
11. 强制结束整个 Relay 服务：房主／客户端结束会话。
12. 服务重启后重新创建、加入和关闭成功。

JSON 报告保留实际连接阶段及时间、玩家状态、公开 ID、命令完成序号；不写入 ownerToken、memberToken、relayToken、legacy host token 或邀请。验收入口失败会返回错误并记录失败原因。

## 界面检查与修复证据

实际图形播放器使用独立房主和两名客户端，在三种分辨率下检查顶部连接状态与创建／搜索输入、底部房主 Save／Start／Close／Copy 操作、成员 Kick 列表、彩色计分板、场地、玩家和标签。左侧总面板和房间／成员列表均可独立滚动。

隐藏 Windows 窗口的直接 ScreenCapture 曾产生全黑图片，因此未将其视为通过证据。最终截图显式将真实场景摄像机和序列化 Canvas 渲染至相同分辨率的 RenderTexture，保留真实布局和游戏状态；测试后还原 Canvas 和摄像机设置。此图形检查不等同于人工鼠标逐项点击或跨公网延迟测试。

截图检查发现并修复了 LayoutGroup 不接管子控件尺寸造成的重叠；补上滚动条、调整房主编辑区域高度、放大玩家标签，并用轮转出生点避免随机重复出生导致的标签重叠。

## 过程中发现的问题

- Unity JsonUtility 将未使用的 Guid 字段序列化为空字符串，导致 Start 接口 HTTP 400：改用仅含 roomId 的启动 DTO。
- 本机系统代理拦截独立播放器的 `127.0.0.1` HTTP 请求，返回 502：本机 HTTP 默认与脚本改用 localhost；UDP 保留服务端返回的地址。
- 编辑器添加 NetworkTransform 时 Reset 先于 Awake，Reliable.ResetState 读取空 target：Reset 先初始化 target，新增回归测试。
- 用户打开工程造成批处理工程锁：使用独立副本，保留其编辑器和未保存内容。
- 开发中的一次源文件更新撞上正在进行的编译，造成 Editor／Player 序列化布局不一致：在源码稳定后重新编译和构建，通过。

当前 Unity 工程仍有已有的 BouncyCastle 程序集解析与重复 Unsafe 程序集警告，以及退出时 Android SDK 检测信息；本次 Windows 构建与 Weaver 成功，无 Relay Lobby C# 编译错误。未将这些已有项目警告视为 Relay Lobby 验收失败，也未改动其他示例以消除它们。

## 改动审查范围

审查包含原本未跟踪的四个完整功能目录：`Assets/Mirror/Examples/RelayLobby/`、`Assets/Mirror/Transports/Relay/`、`RelayServer/`、`RelayServer.Tests/`，包括场景、预制体、材质、测试、脚本和 Unity meta 文件。此外包含 NetworkTransformBase.Reset 的空引用修复和 Relay Lobby 产物忽略项。

保留了现有 Tanks、纹理导入、Unity 升级配置、包设置及其他场景修改。没有提交或暂存用户改动。构建产物、测试报告和独立验证副本保留在被忽略的本地目录，源码／运行说明／本记录留在功能目录中。
