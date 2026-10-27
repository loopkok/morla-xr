# MORLA XR 1.1 工作区实现与验证说明

源码目标：Windows Unity **6000.0.65f1**、Meta XR SDK **72.0.0**、Quest 3。新构建版本为 **1.1.0 / versionCode 8**。

仓库根目录现有 `MORLA-XR.apk` 是原构建，尚未被本次源码生成的新 APK 覆盖。

## 已落地的用户流程

- 启动后直接显示透视工作区与底部工具栏，首次连接引导可跳过。旧菜单默认关闭，Unity/VR/Android 重复启动图已关闭，保留 Meta 系统透视加载设置。
- 工具栏包含开始/暂停发送、视频、麦克风和设置，使用同一组原生线性图标和可读文字。
- 设置包含概览、连接与传输、追踪输入、视频与显示、语音、诊断与关于六页；支持滚动、窗口拖动、回到面前、字号和对比调整。
- 手柄扳机和手部捏合均通过 `WorkspacePointer` 产生 uGUI 事件；手柄摇杆可滚动。现有场景的 OVRHand PointerPose 无父节点，代码将其从 tracking space 正确转换到世界坐标。
- 设置草稿与生效配置分开；USB/局域网/UDP 分别记忆端点。旧 PlayerPrefs 只迁移一次，保存为 `morla-settings.json`，损坏文件会备份。
- 左手 Menu 键唤出/关闭设置。UI 意图优先关闭发送门，关闭设置后仍需显式恢复；恢复前等待按键/扳机/捏合释放和新追踪样本。
- 标准、Mixed、IOBT 三选一。Mixed 与 IOBT 互斥；双侧/仅左/仅右、头部、显示刷新率均有对应设置。
- 本地追踪与网络发送独立，视频可独立观察，麦克风有独立会话。权限失败不会阻塞进入工作区。
- 主视频可切换实际 track，支持主画面、缩略、隐藏和世界固定；保留源端 FOV/比例约定。帧冻结/信令断开给出明确提示；必要视角丢失暂停位姿发送。
- 诊断 UI 与协议调试字段分开，骨骼点默认隐藏；可导出本地诊断文件。

## 核心代码职责

- `AppManager`：保留原场景序列化引用用于迁移，创建服务和工作区；运行配置不再读取旧 Toggle。
- `AppSettings / SettingsStore`：配置模型、校验、迁移、原子保存、端点历史和草稿复制。
- `TrackingCoordinator / SessionStatus`：源状态、观察更新率、有效性与新鲜度；源状态不从 HUD 字符串反推。
- `SessionCoordinator`：显式发送、暂停、UI 隔离、前后台保护、必要数据流 watchdog；关闭窗口与恢复追踪都不会自动恢复发送。
- `TelemetryChannel`：原有每源独立通道、后台连接/写入、最新样本优先、100 ms 过期丢弃、原有校时握手；没有在 UI 主线程等待 socket。
- `WorkspaceUIController / UiKit / WorkspacePointer`：六页原生 uGUI、统一控件样式与手/柄事件、拖动/滚动/输入和工具栏。
- `MorlaRobotClient / MorlaControlSnapshot`：现有 MORLA HTTP 接口适配、严格状态解析和 operation_id 回执关联。

UI 使用共享工厂在运行时组装，同一工厂定义全部控件；没有克隆旧 Toggle 或依赖旧 UI 的业务值。Editor 自动准备可复用工作区入口 prefab 与 TMP 中文字体。这样导入项目即可运行，无须手工重接大场景中的数十个 UI 引用；界面结构和事件绑定集中在代码中维护。

位姿 payload、坐标转换常量、数值精度、`@q3m` 包头、进程 session、按流 sequence 和 challenge/response 保持原契约。线程只发送在采样/提交时封装的数据，不在队列出队时伪造新采样时间。原手部/身体限流器的既有数值和算法未借改版改变。

## MORLA 主机接口

已阅读本机 `../MORLA/src/common/morla_web_manager` 与 `morla_bringup` 的现有实现，本次复用：

```text
GET  http://HOST:8080/api/v1/state
POST http://HOST:8080/api/v1/control/pause
POST http://HOST:8080/api/v1/control/start
```

在“连接与传输”开启 MORLA 状态接口、填写主机/端口并应用，然后点击连接。USB 还需：

```bash
adb reverse tcp:8000 tcp:8000
adb reverse tcp:8001 tcp:8001
adb reverse tcp:8765 tcp:8765
adb reverse tcp:8080 tcp:8080
```

8080 用于 Web 状态/控制；8765 是视频信令，媒体链路仍须验证 WebRTC/ICE。仅有 ADB 转发不代表对应主机服务已经启动。

状态必须包含新鲜的 `data.control`、profile_id、teleop_armed，存在 node_count 时必须为 1。缺少/过期/重复协调器时显示未知并关闭必要发送，不推断为“机器人已停止”。

控制请求返回的 `ok=true` 仅视为接受。客户端提取 `operation_id`，继续读取状态；只有相同 Profile、相同操作 ID、相同命令、phase=completed、result=true 且实际 armed 状态吻合才显示确认。控制请求不自动重试，网络恢复不自动发送 start。使能、HOME、工作位保持原有 MORLA Web 流程。

这是对**现有 HTTP API**的适配；没有向 8000/8001/8765 插入新命令，也没有修改本机 MORLA 仓库或启动机器人服务。原 API 没有客户端 request_id 幂等契约，因此本版本采用单请求在途、主机 operation_id 关联和不自动重试。若要扩展无人值守重试、额外控制权租约，需要双方后续升级协议。

## 字体与资源准备

随项目包含 `Assets/UI/Fonts/NotoSansSC-Regular.ttf` 及 SIL OFL 授权文件。源字体来自 Google Fonts 的 Noto Sans SC Regular，字体文件由 Google 字体服务提供。

首次打开 Unity 后，`Assets/Editor/MorlaWorkspaceAssets.cs` 自动准备：

```text
Assets/Resources/MorlaFont.asset
Assets/Resources/MorlaWorkspace.prefab
```

中文 SDF 使用动态 atlas，并预热常用文案；运行时主机中文反馈可继续补字。也可使用菜单 **MORLA → Prepare Workspace Assets**。构建预检查缺少字体资源时会明确报错，不静默发布中文方框界面。

图标由 Unity UI 网格绘制，不依赖 macOS 字体、SF Symbols 或远程图片。

## Windows 导入与构建

1. 将完整工程同步到 Windows，包括 `Assets`、`.meta`、`Packages`、`ProjectSettings`，使用 Unity 6000.0.65f1 和 Android Build Support。
2. 等待包和字体导入，检查 Console；执行 **MORLA → Prepare Workspace Assets** 可显式重新检查资源。
3. 打开 `Assets/Scenes/Scene.unity`。应用仍从该场景内原 AppManager 入口启动；旧菜单节点已默认关闭。
4. 切换 Android，使用 **MORLA → Build Quest APK** 输出 `Builds/MORLA-XR.apk`。
5. 安装新包后按设备权限提示测试；覆盖安装会迁移已有端点，不需要卸载来获得新 UI。

批处理入口（替换 Editor 和工程绝对路径）：

```powershell
& 'C:\Program Files\Unity\Hub\Editor\6000.0.65f1\Editor\Unity.exe' `
  -batchmode -quit -buildTarget Android -projectPath 'D:\unity_data\MORLA-XR' `
  -executeMethod MORLA.XR.Editor.MorlaWorkspaceAssets.BuildQuest `
  -logFile 'D:\unity_data\MORLA-XR\Builds\unity-build.log'
```

## 自动验证与实际边界

便携检查使用 .NET 8：

```bash
dotnet run --project Tools/Validation/Morla.Validation.csproj -- /absolute/path/to/morla-xr
```

验证内容包括：

- 所有 C# 的 C# 9 语法，包括 Android 条件分支；额外检查 iterator/局部变量等语言错误。
- 直接编译并运行本次配置、状态、发送门、网络与回执判定的实际源码。Unity 序列化/时间/场景依赖使用小型 shim；它不模拟 XR。
- 旧配置迁移、互斥优先级、端点历史、非法端点、草稿隔离、原子保存和损坏备份。
- 实际 localhost TCP/UDP、校时挑战应答、发送门、非阻塞重复停止和连接失败。
- 追踪失效、按键释放、UI 交互、必要视角和网络写入 watchdog，以及恢复后不自动发送。
- MORLA 回执关联、accepted 与 completed 区分、状态过期、协调器重复及字段缺失。

本次便携验证结果：**127 条断言通过**；资源元数据完整、无重复 GUID，原 AppManager GUID 保留，旧菜单默认关闭。`git diff --check` 通过。

当前环境没有 Unity Editor、Android 构建模块或已连接 Quest 3。**尚未完成 Unity/Meta API 的真实编译、APK 构建、双眼 UI 视觉检查、原生键盘、权限与 SHAC/IOBT 真机验收，也没有启动实体机器人进行控制测试。** 便携检查通过不代表这些步骤已经通过。

`[MORLA Startup]` 日志记录透视请求、服务准备、工作区首个渲染帧、各源首次有效、必要数据流首次写入和视频首帧。它们测量 Unity 进程内阶段；完整冷启动仍需结合 adb/logcat 与录屏。诊断中的源频率表示观察到的更新次数，不宣称每次都是新的传感器测量。

原计划阶段 F 的主机发现/二维码、腕部入口、多机器人 Profile、新画质档与额外源过滤属于可选增强，本版本未启用。完整验收仍按设计计划第 14 节逐项执行，尤其是 MORLA 失流保护和重新接管行为。
