# CLAUDE.md — MORLA-XR Unity 工程（Quest 3 端）

Meta Quest 3 上的追踪推流 App：手部（21 关键点）/ 手柄（6DoF + 按键）/ 头部 / IOBT 全身 → UDP/TCP 文本行 → PC 端 `astral_ws`（`quest3_hand_mocap` 接收）。另有可选 WebRTC 视频回传（PC → Quest，信令 8765）、opt-in 麦克风上行（Quest → 主机，8001）和可选 MORLA HTTP 状态/控制（8080）。**本工程是遥操作链路的数据源头**，坐标系/频率/节奏的任何改动都会直接传导到下游 IK。

- 引擎：Unity 6000.0（URP 17）+ Meta XR SDK **72.0.0**（`com.meta.xr.sdk.all`）
- 设备：Quest 3 / 3S；源码可在任意平台修改，**构建必须在 Windows + Unity Editor**
- 对接：`astral_ws/src/quest3_hand_mocap`（ROS2 接收端，协议详见其 README）；主机侧 MORLA HTTP 见 `docs/WORKSPACE_IMPLEMENTATION.md`

## 当前状态（2026-10-27，源码 1.1.0 / versionCode 8）

- **界面**：1.1 起启动直接进入透视工作区（底部工具栏 + 六页设置），旧菜单节点在 `Scene.unity` 中 `m_IsActive: 0`，Unity/VR/Android 启动图已关闭；旧 `ControllerMenuPointer`/`MenuRecenter` 启动时禁用。
- **配置**：从 PlayerPrefs 改为 `Application.persistentDataPath/morla-settings.json`（类型化 `AppSettings`、校验、原子写入 + `.bak`、损坏文件备份）。旧 PlayerPrefs **仅在文件不存在时迁移一次** —— 覆盖安装不会重置配置，改默认值也只影响全新安装或删掉该文件的设备。
- **会话分离**：追踪、位姿发送、视频、语音、MORLA 状态各自独立。开始/暂停是显式动作；操作 UI 会先暂停发送，关闭设置**不会**自动恢复。
- **发送**：每源一个 `TelemetryChannel` 后台线程；只保留最新样本，采样到写入超过 **100 ms** 的丢弃；`@q3m` 在采样/提交时封装，出队不改写时间。
- **追踪**：`TrackingMode` = Standard / Mixed / FullBody，SHAC 与 IOBT 仍互斥；源有效性与 250 ms 新鲜度由 `TrackingCoordinator` 维护。
- **发送率 = 显示刷新率**：流内限流器只是 240 Hz 永不触顶的上限；90 Hz 默认开（`refresh90`）。
- **构建状态**：1.1 源码已在 Unity 真实编译出 APK，仓库内 `MORLA-XR.apk` 已更新为 **1.1.0 / versionCode 8**（101,813,202 字节；`aapt dump badging` 核对，且包内 `global-metadata.dat` 检到 `MORLA.XR.SessionControl`、"必要视角已隐藏"等本轮符号）。**尚未在 Quest 3 上装这个包做验收**；`Tools/Validation` 的 127 条断言与 Roslyn 静态编译通过也不代表运行时正确。仓库保留 APK 会逼近 GitHub 单文件 100 MiB 上限，后续建议改用 Release 附件。
- **链路实测（2026-10-29，设备装的是 1.1.0 / versionCode 8）**：USB + `adb reverse`（8000/8001/8765/8080），位姿头/身体 89–91 Hz、手 82–85 Hz、序号连续丢 0、帧间隔中位 11.1–11.4 ms、`@q3sync` RTT 1.0–1.6 ms；语音 25–27 帧/s、峰值 -8~-25 dBFS，faster-whisper 转出通顺中文。**工程与主机都没有 STT**，语音转文字在下游实现。工具见 `Tools/morla_live_probe.py` / `Tools/transcribe_voice.py`。

## 文件地图（Assets/Scripts/）

```text
AppManager.cs              ← 装配与生命周期：创建服务/工作区、追踪模式与刷新率、前后台、主线程事件队列；旧场景引用仅用于迁移
AppSettings.cs             ← AppSettings/SettingsStore：配置模型、校验、端点历史、PlayerPrefs 一次性迁移、原子保存+备份
SessionCoordinator.cs      ← 发送会话：RequestStart/Pause、UI 交互门、输入释放+新样本门、必要数据流 watchdog
TrackingCoordinator.cs     ← 手/柄/头/身体有效性与新鲜度、ControllerActive 两套判定、Ready 门
SessionStatus.cs           ← SourceStatus/SessionStatus：源状态、传输/视频/语音/MORLA 状态容器
TelemetryChannel.cs        ← 每源后台发送通道：最新样本优先、100ms 过期丢弃、重连/故障回调
HandLandmarkStreamer.cs    ← 手部：手腕+21关键点；100Hz 阈值（Update 计时），OnHandUpdated 仅刷新源状态
ControllerPoseStreamer.cs  ← 手柄：6DoF+按键行；在手判定已移到 TrackingCoordinator
BodyPoseStreamer.cs        ← IOBT：29 关节（StreamedJoints）；ToBodyRelative（躯干系重定向）是坐标系核心
HeadPoseStreamer.cs        ← 头部：跟随帧率
VoiceAudioStreamer.cs      ← 语音：opt-in 麦克风上行（TCP 8001，16kHz PCM16 / 40ms 帧），主机反馈 → 状态面板/提示音
WorkspaceUIController.cs   ← 透视工作区：工具栏、六页设置、窗口；运行时用 UiKit 组装，不依赖场景 UI 引用
UiKit.cs                   ← 原生 uGUI 控件工厂与统一样式
WorkspacePointer.cs        ← 手柄射线 / 手部捏合 → uGUI 事件；输入释放门（替代旧 ControllerMenuPointer）
WorkspaceWindowDrag.cs     ← 工作区 3D 抓取拖动（状态栏/标题栏）：跟手改远近 + 等视角缩放，鼠标退回平面拖动
WorkspaceIcon.cs / MorlaTextStyle.cs ← 图标绘制 / 字体样式
MorlaRobotClient.cs        ← MORLA HTTP 8080：状态轮询、pause/start、operation_id 回执关联、不自动重试
MorlaControlSnapshot.cs    ← /api/v1/state 严格解析与 Confirms 判定
VideoStreamManager.cs      ← WebRTC 回传会话（信令→多 track→面板布局）
QuestVideoReceiver.cs      ← WebRTC native 插件桥（Unity WebRTC Implementation）
VideoPanelRenderer/Stats/Signaling ← 视频面板/统计/信令
StatusHud.cs / LogManager / LogDisplay* ← 旧诊断 HUD 与日志面板（按新版诊断选项启停）
ControllerMenuPointer.cs / MenuRecenter.cs / UIButtonBreather.cs ← 旧菜单遗留，启动时禁用，勿接回
FistTracking.cs            ← 握拳手势（旧，独立 UDP 广播，疑似遗留）
```

```text
Assets/Editor/MorlaWorkspaceAssets.cs ← 准备 MorlaFont/MorlaWorkspace.prefab；MORLA/Build Quest APK
Tools/Validation/                    ← .NET 8 便携验证（语法检查 + 运行纯逻辑），不在 Assets 内
Tools/morla_live_probe.py            ← 主机侧验收探针：8000/8001 多连接、@q3m 解包、Hz/序号/帧间隔、语音 WAV、@q3sync RTT、3D 可视化（取代 skeleton_viewer.py 用于 1.1）
Tools/transcribe_voice.py            ← faster-whisper(CPU int8) 把 voice_capture.wav 转文字，模型从 ModelScope 拉到 ~/.cache/morla-asr；本工程仍不含 STT，产品转写在下游
Tools/transcribe_voice.ps1           ← Windows SAPI 兜底转写（精度很低，只用于确认"有人声"）
```

## 架构不变量（改动前先确认不破坏这些）

1. **手柄判定两套并存，不能合并**：`TrackingCoordinator.ControllerActive` 中，Mixed 用 `AppManager.IsTouchPoseValid`（`GetControllerIsInHandState` 真·在手检测）+ 位置有效；非 Mixed 用代理信号 `!HandTracked && GetControllerPositionValid`。SHAC 只在 `TrackingEnabled && MixedInputEnabled` 时开启（`ApplyTrackingMode`），所以 Mixed 分支只在追踪生效时有意义——后台/未追踪时调用会恒 false，不能据此判断手柄状态（踩过：按键 HUD 恒 none）。
2. **限流器用减法，禁止把阈值卡在帧周期**：手柄/头用 `_timer -= frequencySeconds`；手部 100 Hz（10 ms）与身体 240 Hz 上限用清零安全，阈值远小于帧周期、永不触顶。任何阈值降到接近 1/72 都会隔帧丢包（踩过：手柄 ~50 Hz、wrist_gap 20 ms、p95 28 ms）。实际节奏还受 `TelemetryChannel` 的 100 ms 过期与帧率约束。
3. **IOBT 坐标系重定向 `_hipsBoneToTorsoFix = (-0.5, 0.5, 0.5, 0.5)` 不许动**：Meta `FullBody_Hips` 骨头局部轴是 +X下/+Y前/+Z左，重定向后才是自然躯干系（+X右/+Y上/+Z前）。手/头/手柄（IOBT 时）与全部非 hips body 关节统一经 `ToBodyRelative` 出躯干系；hips 本身发世界系（作为下游 hips_pose 根）。下游 `unity_pose_to_robot` 轴映射建立在“发出的就是躯干系”之上。
4. **左右手骨轴约定镜像**：wrist rotation 是 `IHand.GetRootPose`（HandWristRoot），右手 +X指尖/+Y手背/+Z小指→拇指，左手镜像。下游 Wuji 手 retargeting 依赖此约定。
5. **协议行格式是契约**：位姿载荷仍为 `{Side} wrist:|landmarks:|controller:|buttons:` / `Head pose:` / `body iobt | fid=...:`，逗号后空格、TCP 换行分隔；每行外层使用 `@q3m <session> <sequence> <monotonic_us>|`。接收端 challenge/response 自动估算时钟偏移，无需系统 UTC 对时。`TelemetryChannel.Submit` 在采样/提交时封装，**出队不得改写时间**。改外层或载荷必须连 `quest3_hand_mocap` 和独立脚本一起改。

   并行端口彼此独立：位姿 **8000/TCP** 或 **9000/UDP 广播**；语音 **8001/TCP**（16 kHz 单声道 PCM16 LE、640 样本/40 ms、Base64 后 `VoicePCM:16000:<base64>`，时间戳用实际采集时刻 `WrapCaptured`）；视频信令 **8765**。MORLA HTTP **8080** 是新增的独立控制面，**不得把控制命令塞进 8000/8001/8765**。
6. **SHAC 与 IOBT 互斥是 Meta 平台限制**：`TrackingMode` 三选一；`OVRBody` 检测到 SHAC 会自禁用；`ApplyTrackingMode` 同时设置 `OVRManager.SimultaneousHandsAndControllersEnabled` 与 `OVRPlugin`。“手柄+手+身体三者同时”不存在绕过方案，别再尝试。
7. **UI 指针与点击判定**：`WorkspacePointer` 用画布自身 `worldCamera` 的 `GraphicRaycaster`（用 `Camera.main` 会瞄 A 点 B，踩过）；按下时缓存 `pointerPress/pointerDrag`，松开对**按下时捕获的对象**触发 click。手柄 pose 经 `ToWorldSpacePose(Camera.main)`；**手部走 Interaction SDK `IHand`**（`HandLandmarkStreamer.Hand`，`GetPointerPose` 已是世界坐标），射线轴心取**拇指尖与食指尖中点**（系统手射线就是这么摆的），别再退回 `OVRHand.PointerPose` 手工换算 tracking space。射线在命中画布平面（≤6 m）时**总是显示**，并在画布上加落点光标（`UiKit.Disc`，局部 z=-5 防 z-fighting）；点击不受显示门控——拿起来扣扳机必须能点。
8. **UI 操作会暂停发送，且不自动恢复**：`SessionCoordinator.EnterUiInteraction` 先 `Pause` 并停语音，触发点只有**按下瞬间**与**摇杆滚动**（`WorkspacePointer` 里 `down && !pointer.down` / `|scroll|>0.5`）、打开设置、拖动窗口、左手 Menu 键——**悬停不算 UI 意图**（踩过：射线扫过按钮就暂停，表现为"点恢复发送后一移开就停"）。`RequestStart` 要求输入释放 ≥150 ms，且追踪样本时间 ≥ 恢复时刻；应用设置、切前后台、失焦、`videoEnabled && criticalVideo` 时必要视角丢失都会暂停。不要加“关窗自动恢复发送”。

   **发送按钮例外**：工具栏「开始/暂停发送」挂 `UiKit.SessionControl` 标记，`WorkspacePointer` 命中它时**不**调 `EnterUiInteraction`。原因：按下先暂停会把 `Requested` 清成 false，松开时的点击于是走 `RequestStart` 分支——一次点击=暂停后立刻恢复，表现为"点暂停一直暂停不了"并重开全部连接（2026-10-29 真机实测 15 次/19 s）。新增"点一下直接控制会话"的控件时，一律挂这个标记，别让按下事件先改状态。
9. **发送通道线程边界**：`TelemetryChannel` 后台线程不得调用 Unity API；样本在 `Submit` 时封装并记时，队列只保留最新一帧，超过 100 ms 丢弃；`generation` 不匹配的旧通道不得再发。故障经 `AppManager.Enqueue` 回主线程处理。
10. **配置草稿与生效分离**：`ApplySettings` 先校验、再 `Session.Pause`、复制草稿、必要时重置 body 参考/视频/机器人；只有 `morla-settings.json` 不存在时才从 PlayerPrefs 迁移。改默认值不等于重置已安装设备。
11. **MORLA 控制：accepted ≠ completed**：POST 返回 `ok=true` 只表示接收；必须用相同 Profile、operation_id、command，且 `phase=completed`、`result=true`、armed 状态吻合才确认（`MorlaControlSnapshot.Confirms`）。状态超过 2 s 未更新视为未知并暂停发送；**不自动重试**，网络恢复不自动 start。
12. **视频面板尺寸由 FOV 动态推导**（`VideoPanelRenderer`）：改布局参数（`minimized*` 等）别写死像素，Quest FOV 与 PC 端 `size_multiplier` 都在起作用。
13. **工作区按 3D 抓取移动，尺寸等视角放大**：`WorkspaceWindowDrag` 从 `WorkspacePointer.TryGetDragRay(pointerId)` 取**手上真实的世界射线**——uGUI 的拖动事件只带"射线与画布平面的交点"，丢了手的远近，只用 `PointerEventData.position` 推算深度就退回"只能在一个平面里拖"。抓取点取 `pointerCurrentRaycast.worldPosition`，`_grabOffset = 抓取点 − 手原点` 让它钉在手上；距离夹在 0.45–2.4 m，画布朝向按水平方向 billboard。`WorkspaceUIController.ScaleForDistance` 让缩放 ∝ 距眼距离（基准 `PlaceDistance=1.2 m` / `BaseCanvasScale=0.0009`，系数夹 0.5–2.0），所以远近视角大小不变、实际尺寸随距离变化；**位置必须按缩放后的抓取点反算**（`anchor − rotation × (grabLocal × scale)`），否则放大时会把你捏住的那个点从手指下滑走。`Recenter`、恢复保存位姿之后都要 `ApplyDistanceScale`。状态栏与设置标题栏都可抓；状态栏点击仍打开概览页（拖过 8 px 阈值 uGUI 自动取消 click）。

## 已解决的坑（症状 → 根因 → 现有防护）

| 症状 | 根因 | 防护/现状 |
|---|---|---|
| 菜单点不了（手/柄全失效） | `handSkeletonVersion`=OVR(0) 与 OpenXR 骨骼类型不匹配，`FromOVRHandDataSource` 每帧 NRE | `OculusRuntimeSettings.asset` `handSkeletonVersion: 1`（OpenXR），**别改回** |
| 手柄按键 HUD 恒 none（旧菜单页） | Mixed 勾着但 SHAC 未启用，`IsControllerActive` 走 SHAC 分支恒 false | 不变量 1：Mixed 分支只在 `TrackingEnabled && Mixed` 时有效 |
| 手柄数据 ~50Hz（wrist_gap 20ms） | 限流阈值=帧周期，隔帧丢包 | 不变量 2（减法修正） |
| 旧菜单挡住工作区 / 重复启动图 | 1.1 改用新工作区，但场景节点和 splash 仍存在 | 旧菜单 `m_IsActive: 0`、启动图已关；改 Scene 时别重新激活 |
| 关掉设置后发送不恢复 | 设计如此：UI 交互先关门，恢复要显式 Start + 释放输入 + 新样本 | 不是 bug，别改成自动恢复 |
| 语音关闭时主线程卡顿 | 旧实现 `Join(1300)` 等网络线程 | 1.1 只关 socket、不 Join；**别加回** |
| 把 MORLA `accepted` 当成执行成功 | POST 只表示请求被接受 | 用 operation_id 关联 `phase=completed` + `result=true` + armed |
| 覆盖安装后没读到新默认值 | 配置已迁移到 `morla-settings.json`，只在文件不存在时迁移 | 要重置就删设备上的该文件，不是卸载重装 |
| 菜单射线瞄 A 点 B / 点击无效 | GraphicRaycaster 用错相机；`pressed==hit` 要求过苛 | 不变量 7（WorkspacePointer 已沿用修正） |
| 手上的蓝射线不是手柄发的 | Interaction SDK `RaycasterPinchVisual`；与手柄射线是两套 | 手柄射线 origin 是 `GetLocalControllerPosition`；隐藏去场景 `[BuildingBlock] Left/Right Ray` 下找 |
| 手柄模型与真实手柄对不上 | `[BuildingBlock] Controller Tracking` prefab 自带偏移；纯视觉非必需 | 调激活模型子物体 Transform（单位米，±0.01 起步）或删除两个 Block |
| Unity 6 编译 Safe Mode | `using System.Diagnostics` 与 UnityEngine 的 `Debug` 二义 | 全限定 `UnityEngine.Debug` 或 `using Stopwatch = System.Diagnostics.Stopwatch`；`FindObjectOfType` 换 `FindFirstObjectByType` |
| `pressEventCamera` 编译错 | Unity 6 中该属性只读 | 世界空间画布用 `canvas.worldCamera` 替代 |
| 透视黑屏/菜单黑屏 | passthrough 未开 | `ApplyPassthrough()` 启动强制开（`isInsightPassthroughEnabled` + layer） |
| IOBT 手 IK 下游炸（曾误判为坐标系问题） | 真因在下游：`frame_id` 中途 world↔body 切换，零点/当前跨系相减 | 本端保证 IOBT 激活后所有位姿发躯干系；下游 sticky latch/hold gate 见 `quest3_hand_mocap` README |
| 视频画面“放大镜”效果 | FOV 失配 + 面板尺寸推导错 | 面板按相机 FOV 动态推导 + `size_multiplier` |

> `AndroidTargetSdkVersion` 已是 33（旧的“需升级”条目已失效）。

## 延迟观测（下游 `[Latency]` 行的 Quest 侧因素）

本端影响的是 `wrist_gap`（相邻包到达间隔≈发送节奏）：
- 72Hz 帧率下健康值 **13.9ms**（p95≈15）；90Hz 下 ≈11.1ms；
- 手部采样由 `WhenHandUpdated` 驱动，发送仍按 Update 阈值计时；手柄/头/身体是 Update 轮询。节奏异常先查限流器、`TelemetryChannel` 的 100 ms 过期与连接线程；
- 90Hz + IOBT 是最吃算力组合，发热掉帧时 p95 先涨——先关 90Hz 对照；
- SHAC（Mixed）与 IOBT 各有几 ms 运行时固有开销（追踪模型跑在 Quest 上），属平台成本；
- 诊断里的“源频率”是观察到的更新次数，不代表每次都是新的传感器测量。

## 修改流程约定

1. **协议改动双向同步**：本端加字段/改格式，同一天改 `astral_ws/src/quest3_hand_mocap`（解析+话题+README）与独立脚本 `quest3_mocap_standalone.py`，并在两边 README 日志记同日条目。
2. **每次实质改动写更新日志**：根目录 `CHANGELOG.md` 记版本摘要，细节写 `docs/CHANGELOG_YYYY-MM-DD.md`（动机——根因——修法——实测数值）；README 保留历史条目。“节奏修复”这类要有 `wrist_gap` 前后实测数字。
3. **玩家可感知表象进文档**：写“手柄射线瞄 A 点 B”不是“raycast 相机不一致”。
4. **配置默认值**：`morla-settings.json` 存在时默认值不生效；迁移只在文件不存在时发生。文档要写清“全新安装/删除配置文件生效”，不要再写“卸载重装”。
5. **构建在 Windows**：同步完整工程（含 `.meta`、`Packages`、`ProjectSettings`），先 `MORLA → Prepare Workspace Assets`（缺 `MorlaFont`/`MorlaWorkspace.prefab` 会预检报错），再 `MORLA → Build Quest APK`（输出 `Builds/MORLA-XR.apk`）。也可用 `MorlaWorkspaceAssets.BuildQuest` 的 batchmode 命令。APK 回传本仓库单独提交（`Update MORLA-XR.apk 构建产物`，注明含哪些功能）。**注意**：每版 APK ~94MB，GitHub 单文件硬限 100MB、推荐 50MB，长期需考虑 LFS 或 Release 附件。
6. **场景改动走 `Scene.unity`**：旧菜单节点保持 `m_IsActive: 0`；`AppManager` 的旧序列化引用只为迁移保留，不要重新接回业务逻辑。
7. **先跑便携验证**：`dotnet run --project Tools/Validation/Morla.Validation.csproj -- <工程绝对路径>`（需 .NET 8）。它覆盖 C#9 语法、Android 条件分支和纯逻辑，但**不等于** Unity/Meta 真实编译。

## 遗留事项 / 下一步候选

- **1.1 尚未完成 Windows Unity/Meta 编译、APK 重建和 Quest 真机验收**（双眼 UI、原生键盘、权限、麦克风、SHAC/IOBT）；仓库 APK 仍是 versionCode 7。
- MORLA 状态/控制（8080）在 USB 下需要额外 `adb reverse tcp:8080 tcp:8080`；ADB 转发存在不代表服务已启动。
- 手部追踪在 90Hz 下是否真有 90Hz 新值取决于 Meta 运行时，未实测；
- `FistTracking.cs` 独立 UDP 广播（255.255.255.255:9000），与主链路协议无关，疑似遗留代码，待确认去留；
- 手柄模型对不上问题待用户回传激活模型子物体 Transform 后精调（或删除两个 Controller Tracking Block）；
- Quest 端延迟只有节奏观测（wrist_gap），无端到端时间戳链路（下游 debug 头 `| f = | t =` 需开 Debug Info 才发，两侧利用不充分）。
