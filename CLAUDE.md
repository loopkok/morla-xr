# CLAUDE.md — MORLA-XR Unity 工程（Quest 3 端）

Meta Quest 3 上的追踪推流 App：手部（21 关键点）/ 手柄（6DoF + 按键）/ 头部 / IOBT 全身 → UDP/TCP 文本行 → PC 端 `astral_ws`（`quest3_hand_mocap` 接收）。另有可选 WebRTC 视频回传（PC → Quest 显示）。**本工程是遥操作链路的数据源头**，坐标系/频率/节奏的任何改动都会直接传导到下游 IK。

- 引擎：Unity 6000.0（URP 17）+ Meta XR SDK **72.0.0**（`com.meta.xr.sdk.all`）
- 设备：Quest 3 / 3S；开发机 Windows + Unity Editor（本 Linux 侧只改代码，构建在 Windows）
- 对接：`astral_ws/src/quest3_hand_mocap`（ROS2 接收端，协议详见其 README）

## 当前状态（2026-09-03）

- **发送率 = 显示刷新率**：限流器只是 240Hz 永不触顶的上限，实际节奏由帧率决定；菜单「90 Hz」开关请求 90Hz（默认开，`Saved90Hz` 持久化，不支持自动回落 72）；
- 手柄按键推流已上线：位姿行后同帧追加 `{side} buttons:, trigger, grip, stickX, stickY, mask`；
- 菜单可用**手柄射线**（`ControllerMenuPointer`：蓝线+末端小球+扳机点击）或手部捏合（Interaction SDK hand ray）操作；
- 默认配置（仅全新安装生效，**PlayerPrefs 跨重装保留，覆盖安装前先 adb uninstall**）：TCP 有线 / Head Pose 开 / IOBT 开（Mixed 默认关）/ Landmarks 开 / Debug Info 开 / 90Hz 开；
- 构建产物 `MORLA-XR.apk` 与源码同仓库提交（~94MB/版）。

## 文件地图（Assets/Scripts/）

```text
AppManager.cs              ← 主控单例：菜单/网络配置/追踪模式互斥(SHAC×IOBT)/自动创建运行时组件/90Hz 请求
HandLandmarkStreamer.cs    ← 手部：手腕+21关键点，100Hz 阈值事件驱动（OnHandUpdated，非 Update 轮询）
ControllerPoseStreamer.cs  ← 手柄：6DoF+按键行；IsControllerActive 是 Mixed/IOBT 两套判定的心脏
BodyPoseStreamer.cs        ← IOBT：31 关节；ToBodyRelative（躯干系重定向）是全链路坐标系核心
HeadPoseStreamer.cs        ← 头部：跟随帧率
VoiceAudioStreamer.cs      ← 语音：opt-in 麦克风上行（独立 TCP 8001，16kHz PCM16 / 40ms 帧），主机反馈 JSON → HUD + 提示音
ControllerMenuPointer.cs   ← 菜单手柄射线指针（自包含 controller→uGUI 桥，不依赖 Interaction SDK rig）
StatusHud.cs               ← 顶部状态覆盖层（PT/SHAC/Mix/按键行/ray 调试行/IOBT）
LogManager/LogDisplay*     ← HUD 日志缓存与左右面板
VideoStreamManager.cs      ← WebRTC 回传会话（信令→多 track→面板布局；静音面板缩左下角）
QuestVideoReceiver.cs      ← WebRTC native 插件桥（Unity WebRTC Implementation）
VideoPanelRenderer/Stats/Signaling ← 视频面板/统计/信令
MenuRecenter.cs            ← 菜单回到面前
FistTracking.cs            ← 握拳手势（旧，独立 UDP）
UIButtonBreather.cs        ← 防连点
```

## 架构不变量（改动前先确认不破坏这些）

1. **手柄判定两套并存，不能合并**：Mixed（SHAC 开启时）用 `GetControllerIsInHandState`（真·在手检测）；IOBT/菜单用代理信号 `!IsHandTracked && GetControllerPositionValid`（SHAC 的在手 API 在无 SHAC 时恒 false）。**SHAC 分支仅在「推流中 + Mixed 开」时启用**——菜单页 Mixed 勾着但 SHAC 未启用（`AppManager.Start` 里 `DisableShac()`，SHAC 只在 Start Stream 后开），走错分支手柄判定会失效（踩过：按键 HUD 恒 none）。
2. **限流器必须用减法 `_timer -= frequencySeconds`，禁止 `_timer = 0f`**：阈值若等于/接近帧周期，帧时间抖动会隔帧丢包（踩过：手柄 1/72 阈值实测掉到 ~50Hz，wrist_gap 20ms p95 28ms；手部 100Hz 阈值不受影响）。当前全部 240Hz 上限，实际节奏 = 刷新率。
3. **IOBT 坐标系重定向 `_hipsBoneToTorsoFix = (-0.5, 0.5, 0.5, 0.5)` 不许动**：Meta `FullBody_Hips` 骨头局部轴是 +X下/+Y前/+Z左，重定向后才是自然躯干系（+X右/+Y上/+Z前）。手/头/手柄（IOBT 时）与全部非 hips body 关节统一经 `ToBodyRelative` 出躯干系；hips 本身发世界系（作为下游 hips_pose 根）。下游 `unity_pose_to_robot` 轴映射建立在“发出的就是躯干系”之上。
4. **左右手骨轴约定镜像**：wrist rotation 是 `IHand.GetRootPose`（HandWristRoot），右手 +X指尖/+Y手背/+Z小指→拇指，左手镜像。下游 Wuji 手 retargeting 依赖此约定。
5. **协议行格式是契约**：载荷仍为 `{Side} wrist:|landmarks:|controller:|buttons:` / `Head pose:` / `body iobt | fid=...:`，逗号后空格、TCP 换行分隔；每行外层使用 `@q3m <session> <sequence> <monotonic_us>|` 包头。接收端 challenge/response 自动估算时钟偏移，无需系统 UTC 对时。下游解析先剥包头再按载荷解析（含 `| f = | t =` debug 头格式），改外层或载荷必须连 `quest3_hand_mocap` 和独立脚本一起改。

   另有一条**独立于位姿链路**的语音流：同一主机地址 **TCP 8001**（有线默认 `adb reverse tcp:8001 tcp:8001`），16 kHz 单声道 signed PCM16 LE、640 样本/帧（40 ms），Base64 后为 `VoicePCM:16000:<base64>`；外层复用 `@q3m ...|`，但时间戳必须用**实际采集时刻**（`QuestPacketEnvelope.WrapCaptured`），不能把排队音频标成刚采集。主机在同一连接回传单行 UTF-8 JSON `{"type":"feedback","phase":"...","message":"..."}`（与时钟握手共用连接），Quest 只显示 HUD/播提示音，不做识别。语音开关默认关（`SavedVoiceMic`），打开始申请 `RECORD_AUDIO`。
6. **SHAC 与 IOBT 互斥是 Meta 平台限制**：`OVRBody` 检测到 SHAC 会自禁用；菜单两 Toggle 互斥（`_applyingMutex` + `OnMixedChanged/OnIobtChanged`），“手柄+手+身体三者同时”不存在绕过方案，别再尝试。
7. **`ControllerMenuPointer` 点击判定**：命中用画布自身 `worldCamera` 的 `GraphicRaycaster`（用 `Camera.main` 会瞄 A 点 B，踩过）；松开扳机对**按下时捕获的物体**触发（`pressed == hit` 要求会因射线漂移丢掉大部分点击，踩过）；Button/Toggle 直接 `Invoke` 兜底。射线**线**的显示受 3s 闲置门控，但点击不受——拿起来扣扳机必须能点。
8. **视频面板尺寸由 FOV 动态推导**（`VideoPanelRenderer`）：改布局参数（`minimized*` 等）别写死像素，Quest FOV 与 PC 端 `size_multiplier` 都在起作用。

## 已解决的坑（症状 → 根因 → 现有防护）

| 症状 | 根因 | 防护/现状 |
|---|---|---|
| 菜单点不了（手/柄全失效） | `handSkeletonVersion`=OVR(0) 与 OpenXR 骨骼类型不匹配，`FromOVRHandDataSource` 每帧 NRE | `OculusRuntimeSettings.asset` `handSkeletonVersion: 1`（OpenXR），**别改回** |
| 手柄按键 HUD 恒 none（菜单页） | 菜单页 Mixed 勾着但 SHAC 未启用，`IsControllerActive` 走 SHAC 分支恒 false | 不变量 1：SHAC 分支仅推流中+Mixed 生效 |
| 手柄数据 ~50Hz（wrist_gap 20ms） | 限流阈值=帧周期，隔帧丢包 | 不变量 2（减法修正） |
| 菜单射线瞄 A 点 B / 点击无效 | GraphicRaycaster 用错相机；`pressed==hit` 要求过苛 | 不变量 7 |
| 手上的蓝射线不是手柄发的 | Interaction SDK `RaycasterPinchVisual`（hand ray 视觉，从腕部 pointer pose 发出）；与手柄射线是两套 | 手柄射线 origin 是 `GetLocalControllerPosition`，手部蓝线属 hand ray，要隐藏去场景 `[BuildingBlock] Left/Right Ray` 下找 |
| 手柄模型与真实手柄对不上 | `[BuildingBlock] Controller Tracking` prefab 自带偏移；纯视觉非必需 | 调激活模型子物体 Transform（单位米，±0.01 起步）或直接删除两个 Block（pose/按键/射线均不依赖它） |
| Unity 6 编译 Safe Mode | `using System.Diagnostics` 与 UnityEngine 的 `Debug` 二义 | 用 `UnityEngine.Debug.Log` 全限定；顺手把 `FindObjectOfType` 换 `FindFirstObjectByType`（Unity 6 弃用） |
| `pressEventCamera` 编译错 | Unity 6 中该属性只读 | 世界空间画布用 `canvas.worldCamera` 替代 |
| Google Play 报 targetSdk<33 | `ProjectSettings.asset` `AndroidTargetSdkVersion: 32` | 纯项目设置问题，与代码无关；上架需改 33+（当前本地部署不拦） |
| 透视黑屏/菜单黑屏 | passthrough 未开 | `ApplyPassthrough()` 启动强制开（`isInsightPassthroughEnabled` + layer） |
| IOBT 手 IK 下游炸（曾误判为坐标系问题） | 真因在下游：`frame_id` 中途 world↔body 切换，零点/当前跨系相减 | 本端保证 IOBT 激活后所有位姿发躯干系；下游 sticky latch/hold gate 见 `quest3_hand_mocap` README |
| 视频画面“放大镜”效果 | FOV 失配 + 面板尺寸推导错 | 面板按相机 FOV 动态推导 + `size_multiplier` |

## 延迟观测（下游 `[Latency]` 行的 Quest 侧因素）

本端影响的是 `wrist_gap`（相邻包到达间隔≈发送节奏）：
- 72Hz 帧率下健康值 **13.9ms**（p95≈15）；90Hz 下 ≈11.1ms；
- 手部是事件驱动（`OnHandUpdated`），手柄/头/身体是 Update 轮询，节奏异常先查限流器；
- 90Hz + IOBT 是最吃算力组合，发热掉帧时 p95 先涨——先关 90Hz 对照；
- SHAC（Mixed）与 IOBT 各有几 ms 的运行时固有开销（追踪模型跑在 Quest 上），属平台成本，与推流代码无关。

## 修改流程约定

1. **协议改动双向同步**：本端加字段/改格式，同一天改 `astral_ws/src/quest3_hand_mocap`（解析+话题+README）与独立脚本 `quest3_mocap_standalone.py`，并在两边 README 日志记同日条目。
2. **每次实质改动更新 README 更新日志**（`MORLA-XR/README.md` 底部，日期标题——动机——根因——修法——实测数值）。“节奏修复”这类要有 `wrist_gap` 前后实测数字。
3. **玩家可感知表象进文档**：写“手柄射线瞄 A 点 B”不是“raycast 相机不一致”。
4. **数值默认值改动标注 PlayerPrefs 影响**：改默认值仅对全新安装生效，必须同时写“旧设备先卸载”。
5. **构建在 Windows**：Linux 侧改完代码同步到 Windows（scp/zip，注意 `Scene.unity` 与脚本都要带），Unity 编译无红字后 Build，APK 回传本仓库提交（`Update MORLA-XR.apk 构建产物` 单独一笔，注明含哪些功能）。
6. **场景改动走 `Scene.unity`**：Toggle 默认态、Canvas 配置等序列化在场景里，改默认值别只改 C#（本仓库直接文本编辑 `Scene.unity` 的 `m_IsOn` 等字段是既定做法，注意 YAML 结构别破坏）。

## 遗留事项 / 下一步候选

- 手部追踪数据在 90Hz 下是否真有 90Hz 新值取决于 Meta 运行时（可能只是 72Hz 数据发更密），未实测；
- `FistTracking.cs` 独立 UDP 广播（255.255.255.255:9000），与主链路协议无关，疑似遗留代码，待确认去留；
- 手柄模型对不上问题待用户回传激活模型子物体 Transform 后精调（或删除两个 Controller Tracking Block）；
- Google Play 上架需 `AndroidTargetSdkVersion` 升 33+（当前 32，本地部署不受影响）；
- Quest 端延迟只有节奏观测（wrist_gap），无端到端时间戳链路（下游 debug 头 `| f = | t =` 需开 Debug Info 才发，两侧利用不充分）。
