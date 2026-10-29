# MORLA XR 1.1 修改、验证与提交记录

日期：2026-10-27，Asia/Shanghai。基线 `c0fcf738fe2af4d07beb7df3295159f8c66b8c48`，分支main，远端 https://github.com/loopkok/morla-xr.git。提交前fetch确认本地与origin/main一致。本轮整理已有工作区源码改版，并重跑现有便携验证，没有连接Quest、启动机器人或执行新APK构建。

## 启动与工作区

旧流程将追踪、连接及各项开关集中在启动菜单；本轮从原AppManager场景入口创建透视工作区和底部工具栏，旧菜单的Scene.unity节点默认隐藏。开始/暂停发送、视频、麦克风与设置有独立入口，设置采用概览、连接与传输、追踪输入、视频与显示、语音、诊断与关于六页。窗口支持拖动、回到面前、滚动、字号及对比调整。

`WorkspaceUIController`、`UiKit`、`MorlaTextStyle`统一原生uGUI控件；`WorkspaceIcon`用Unity UI网格绘制图标；`WorkspacePointer`统一手柄射线/手部捏合与uGUI事件，`WorkspaceWindowDrag`处理窗口拖动。操作入口保留原场景序列化引用，业务配置不再依赖旧Toggle的实时值。

`ProjectSettings.asset`关闭重复Unity、VR和Android启动图，版本改为1.1.0 / Android versionCode 8。保留Meta透视设置，真实启动耗时和双眼显示仍需设备测量。源码中的启动阶段日志不等于已完成冷启动优化验收。

## 配置、追踪与会话分离

- `AppSettings / SettingsStore`提供类型化配置、草稿复制、校验、旧PlayerPrefs一次迁移、每种协议端点历史、原子写入和损坏备份；持久化文件为设备端 `morla-settings.json`。
- `TrackingCoordinator / SessionStatus`维护手、柄、头部和身体的有效性、新鲜度及观察频率。标准、Mixed/SHAC和IOBT按显式选项互斥，左右输出侧及头部独立配置。本地追踪可在网络发送关闭时运行。
- `SessionCoordinator`负责明确开始/暂停、UI交互门、输入释放与新样本门、应用前后台及必要数据流watchdog。关闭设置、追踪恢复和网络恢复不自动恢复发送。
- `AppManager`负责服务装配、主线程事件队列、模式与权限状态、媒体会话及启动阶段记录。

修改默认值不等于重置所有已安装设备；此次包含旧偏好迁移与端点历史，按实现说明覆盖安装后检查配置，不要求为新版UI默认卸载。权限失败不阻塞进入工作区，身体权限、SHAC/IOBT实际可用性仍需Meta运行时检查。

## 网络发送与协议边界

手部、手柄、头部、身体streamer改为使用 `TelemetryChannel`，每源保留独立通道。连接/写入在后台线程，最新样本优先，100ms过期样本丢弃；发送门关闭时不缓存待恢复的旧动作，重复停止不阻塞主线程。

载荷行、坐标转换、数值精度、`@q3m`封包及校时challenge/response继续复用原契约；`QuestPacketEnvelope.cs`本轮未修改。封包在采样/提交时完成，不在队列出队时更新样本时间。IOBT原坐标校正四元数保留，身体参考增加250ms新鲜度与失效清理。没有顺带调整原限流器数值或将观察频率当作传感器实际新值频率。

## 媒体与主机状态

- 视频独立于位姿发送，支持实际track选择、主画面/缩略/隐藏与世界固定，继续使用源FOV和画面比例。必要视角冻结或信令异常可暂停位姿发送，状态明确显示过期。
- 麦克风独立opt-in，用户偏好与实际录音状态分开；进入设置、后台/失焦、关闭麦克风或断开主机时停止采集和连接。语音线程退出不再在主线程Join等待。
- 原StatusHud、网络/手部日志和骨骼显示按新版诊断选项管理，诊断UI与协议debug字段分开，避免用HUD文本作为业务状态。
- `MorlaRobotClient / MorlaControlSnapshot`复用现有GET `/api/v1/state`与POST `/api/v1/control/{pause,start}`。accepted只表示请求接受；核对Profile、operation_id、命令、completed/result和armed状态后才确认完成。无新鲜、完整、单协调器状态时显示未知，不自动重试控制请求或恢复发送。

MORLA HTTP采用8080独立端口，原8000位姿、8001语音、8765视频信令不插入新控制命令。USB使用时需额外转发8080；ADB端口存在不等于服务、WebRTC媒体或机器人回执已可用。

## 资源与Windows构建

- `Assets/UI/Fonts/`收录NotoSansSC-Regular.ttf及随附OFL文本和.meta。
- Editor入口 `MorlaWorkspaceAssets`提供 `MORLA/Prepare Workspace Assets`、`MORLA/Build Quest APK`，导入时准备动态中文TMP字体与工作区prefab，缺字体时构建预检报错。
- `.gitignore`允许便携验证的csproj入库，排除其bin以及现有Unity缓存/obj等输出；提交源码、Assets/.meta、Packages和ProjectSettings。
- 目标为Windows Unity6000.0.65f1、MetaXR72、Android/ARM64。详细步骤见 [实现说明](WORKSPACE_IMPLEMENTATION.md)，原设计提案见 [设计计划](MORLA_XR_UX_UI_REDESIGN_PLAN.md)。

仓库根目录 `MORLA-XR.apk`仍是原二进制，没有随1.1源码重建。本次已核对其内容未变，SHA-256为 `99ef6f8307d5ba9dcd202952619545bf16f11f53a5319e75e017425375378135`。不能安装该旧APK来验收新版UI，后续Windows新构建应单独记录构建结果和产物提交。

## 2026-10-27 本次实际复验

运行：

```bash
/tmp/morla-dotnet/dotnet run --project Tools/Validation/Morla.Validation.csproj -- /Users/a1234/Desktop/morla-xr
```

可在其他主机用已安装的.NET8 `dotnet`替代本地临时路径。结果：127条断言通过。

范围包括所有Assets C#的C#9语法与Android条件分支，以及指定语言控制流错误检查；用小型Unity shim编译运行配置迁移、会话门、回执解析和实际localhost TCP/UDP/校时逻辑。并非所有Unity/Meta类型已被真实编译：验证器明确不将缺少引擎程序集的诊断当作Unity编译成功。

额外资源检查：147个.meta文件、147个唯一GUID，无重复；新增Assets文件及Editor/UI/Fonts目录元数据齐全；原AppManager.cs.meta与基线字节一致。暂存前diff检查通过，生成bin/obj未纳入提交。

全量暂存后的空白检查另提示新增Unity .meta的空字段尾空格、OFL随附原文的一处尾空格，以及设计稿开头的Markdown双空格换行；这些资源/原文格式原样保存。对其余代码及文档执行排除这些路径的暂存diff检查通过；不将全量暂存检查表述为零提示。

未完成：Unity/Meta API真实编译、Android APK构建、Quest双眼UI/原生键盘/权限、SHAC/IOBT、WebRTC/ICE和真实主机控制回执测试。没有设备wrist_gap或启动延迟实测值，不编造改版前后性能对比。

## 提交与推送

本轮提交包含全部已有源码改版、场景/版本设置、字体授权/资源、便携验证工具、设计/实现文档及本日志；旧APK保留。README增加同日复验入口，历史日志不删除。

主内容提交后经SSH443和已配置本机代理推送main，再fetch核对SHA；实际回执保存 `docs/PUSH_LOG_2026-10-27.md`，单独提交该记录。不强推，不更改永久Git远端配置，不以推送成功替代APK交付或设备验收。

## 工作区首次放置（进入即落到脚下）

真机反馈：进入工作区时窗口出现在接近脚面的位置，必须点“回到面前”。根因是首次放置只做一次：`WorkspaceUIController.Update` 在第一帧就调用 `Recenter()` 并置 `_placed`。本场景相机为 FloorLevel（`Scene.unity` 中 `_trackingOriginType: 1`），XR 应用首个头部位姿之前 `CenterEyeAnchor` 仍停在 tracking space 原点，也就是地面；于是“眼前 1.2 m、下移 8 cm”实际落在脚下 1.2 m 处。旧的失联判定只看距离 > 2.5 m 或点在身后，脚下 1.2 m 两个条件都不满足，`_placed` 又已锁定，故整场不再自愈。另外 `HideSettings()` 会经 `SavePose()` 把这个地面位姿写进 `morla-settings.json`（`hasWindowPose`），而恢复只看距离 < 2.5 m，所以会每次启动复现。

修法（`Assets/Scripts/WorkspaceUIController.cs`）：
- 首次放置等待有效头部：`HeadPoseReady()` 要求头部源 250 ms 内有效，或眼锚已离开 tracking space 原点 > 20 cm（此时位姿必然已应用），不再在第一帧盲放。
- 新增 `Reachable()`：距眼 ≤ 2.5 m 且不低于眼睛 1 m；失联自愈、保存位姿恢复、`SavePose()` 三处共用。低于眼睛 1 m 的位姿既不恢复也不再写入，已存在的错误存档无需手动删除，下一次放置即覆盖。
- 失联自愈沿用 2 s 门槛与输入释放门，不改变“关闭设置不自动恢复发送”的既有行为。

静态编译（Unity 自带 Roslyn，editor/player 两套宏，397 引用）0 错误，只剩既有的 CS0618/CS0649/CS0414 警告。未在 Unity 真实编译、未重建 APK、未做 Quest 真机验收。
