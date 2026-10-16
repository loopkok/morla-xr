# MORLA XR（Quest 3 全身追踪推流）

一个运行在 **Meta Quest 3** 上的 Unity 追踪推流 App。通过 OpenXR + Meta Movement SDK 采集**双手、头部、身体(IOBT)、手柄**的 6DoF 位姿与关键点，经 UDP / TCP 实时推流到 PC 端，供下游应用（虚拟主播、动捕、机器人遥操作等）使用。

- 引擎：Unity 6000.0（URP 17）
- SDK：Meta XR SDK 72.0.0 + OpenXR + Unity XR Hands 1.7.2
- 目标设备：Quest 3 / 3S

---

## 功能特性

| 数据源 | 内容 | 输出 |
|---|---|---|
| **手部** | 手腕位姿 + 21 个关键点（拇指/食指/中指/无名指/小指） | `Left/Right wrist:` / `Left/Right landmarks:` |
| **手柄** | Touch 手柄 6DoF 位姿 | `Left/Right controller:` |
| **手柄按键** | 扳机/握把模拟量 + 摇杆 + 按键位掩码 | `Left/Right buttons:` |
| **头部** | 头部位姿 | `Head pose:` |
| **身体 IOBT** | 全身骨骼关节（髋/脊柱/肩/臂/腿/脚等 31 关节） | `body iobt \| fid=High: ...` |

发送的坐标系随追踪模式不同：

- **Mixed 模式**：手/头/手柄均在 **Unity 世界系**（Quest stage 原点，X右/Y上/Z前）。
- **IOBT 模式**：hips 关节发 **世界系**；手/头/手柄/其余 body 关节均发 **髋部(躯干)系**（以 hips 为原点，X右/Y上/Z前）。详见下文「坐标系」。

---

## 追踪模式

App 提供两个互斥的追踪开关，由菜单里的 **Mixed Input** 和 **IOBT Body** 两个 Toggle 控制。另有 **90 Hz** 开关（默认关）：开启后请求 90 Hz 显示刷新（设备不支持或热限时自动回落最近可用档），手/柄/头/身体的发送率同步从 72 Hz 提到 ~90 Hz（帧间隔 13.9→11.1 ms）。切换瞬间屏幕闪一下属正常。

### 1. Mixed 模式（SHAC：手柄 + 手同时）

- 开启 `Mixed Input`，关闭 `IOBT Body`
- **一只手拿手柄、另一只手裸手**，两边数据同时输出
- 手柄在手 → 发该侧手柄；手柄放下 → 该侧自动切回手部追踪
- 输出：`L=ctrl R=hand`（或任意组合）

### 2. IOBT 模式（全身身体追踪）

- 关闭 `Mixed Input`，开启 `IOBT Body`
- 全身骨骼追踪（fidelity High / FullBody 请求）
- 身体追踪与手部追踪可共存；手柄与手部则**二选一自动切换**：
  - 手柄沉睡 → 发手部数据
  - 按下手柄按钮唤醒手柄 → 系统切到手柄、发手柄数据
  - 放下手柄 → 切回手部

### ⚠️ Meta 平台硬限制

| 组合 | 是否支持 |
|---|---|
| 手柄 + 手（SHAC / Mixed） | ✅ |
| 身体 IOBT + 手 | ✅ |
| 身体 IOBT + 手柄 | ✅（与手二选一自动切） |
| **身体 IOBT + 手柄 + 手 三者同时** | ❌ 不支持 |

原因：手柄的「在手」检测依赖 **SHAC（SimultaneousHandsAndControllers）**，而 **SHAC 与 IOBT 互斥**（`OVRBody` 检测到 SHAC 会自禁用）。这是 Meta 运行时的既定行为，代码无法绕过。

---

## 网络配置

菜单里可选择协议与 IP / 端口：

| 协议 | 说明 | 默认地址 |
|---|---|---|
| 0 - UDP | 广播 | `255.255.255.255:9000` |
| 1 - TCP 有线 | ADB / USB | `127.0.0.1:8000` |
| 2 - TCP 无线 | 局域网 | `192.168.1.1:8000` |

数据以纯文本行发送，每条消息以换行结尾。示例：

```
Left wrist:, x, y, z, qx, qy, qz, qw
Left landmarks:, x,y,z, x,y,z, ... (21 组)
Left controller:, x, y, z, qx, qy, qz, qw
Left buttons:, trigger, grip, stickX, stickY, mask
Head pose:, x, y, z, qx, qy, qz, qw
body iobt | fid=High: hips,x,y,z,qx,qy,qz,qw spine-lower,... chest,... left-shoulder,...
```

**手柄按键行（`Left/Right buttons:`）**，紧跟该侧位姿行同一帧发出：

| 字段 | 含义 |
|---|---|
| `trigger` | 食指扳机模拟量 0.0~1.0 |
| `grip` | 中指握把模拟量 0.0~1.0 |
| `stickX, stickY` | 摇杆二维向量 -1.0~1.0 |
| `mask` | 数字按键位掩码：bit0=主键(X/A)、bit1=副键(Y/B)、bit2=摇杆按下、bit3=Menu、bit4=扳机点击、bit5=握把点击 |

---

## 菜单页手柄操作

菜单 UI 既可用**手部捏合**（Interaction SDK hand ray）也可用**手柄射线**（`ControllerMenuPointer`）操作：

- 拿起手柄 → 自动出现蓝色射线 + 末端命中点小球，指向菜单。
- 扣**扳机** = 点击（Button / Toggle / Dropdown / InputField）。
- 手柄放下约 3 秒无操作 → 射线自动隐藏；一动作即恢复。
- 该指针为自包含实现（不依赖 Building Blocks 的 Controller Ray Interactor，本 SDK 版本未提供该 rig），由 `AppManager` 启动时自动创建，无需场景手动配置。

---

## 坐标系

### 手腕局部系（OVR 手骨约定）
`HandLandmarkStreamer` 发的 wrist rotation 是 `IHand.GetRootPose`（HandWristRoot 关节）的朝向，遵循 Meta 手骨轴约定，**左右手镜像**：

- 右手：+X 指尖、+Y 手背法向、+Z 小指→拇指
- 左手：+X 朝腕、+Y 手心法向、+Z 拇指→小指

下游做手部 IK / 驱动 Wuji 手时需按此约定对齐到机器人手 TCP。

### IOBT 髋部(躯干)系
`BodyPoseStreamer.ToBodyRelative` 把手/头/手柄表达到 hips 父系下。Meta 的 `FullBody_Hips` 骨头局部轴是 **+X下 / +Y前 / +Z左**（非自然躯干系），直接用会导致手/头落在怪朝向。代码用常量四元数 `_hipsBoneToTorsoFix = (-0.5, 0.5, 0.5, 0.5)` 把骨头系重定向成自然躯干系 **+X右 / +Y上 / +Z前**：

- `_lastBodyReferencePose` = `hipsRot * _hipsBoneToTorsoFix`（重定向后的躯干系），`ToBodyRelative` 自动落到躯干系。
- body 包里的 hips 朝向也发重定向后的值，下游用同一父系保持一致。
- body 包里其余关节也已在 Quest 端用 `ToBodyRelative` 转成髋部相对，下游不再二次转换。

直立面朝 +Z 时，躯干系 = Unity 世界系（手往右/上/前移，X/Y/Z 分别增）；转身时躯干系随髋部转，手相对躯干坐标稳定。

---

## 项目结构

```
Assets/Scripts/
├── AppManager.cs              # 主控：菜单、网络配置、追踪模式切换(SHAC/IOBT 互斥)
├── HandLandmarkStreamer.cs    # 手部：手腕 + 21 关键点推流
├── HandLandmarkVisualizer.cs  # 手部关键点可视化
├── ControllerPoseStreamer.cs  # 手柄：6DoF + 按键/摇杆推流（Mixed 用「在手」检测，IOBT 用代理信号）
├── ControllerMenuPointer.cs   # 菜单页手柄射线指针（自包含 uGUI 桥，扳机点击，命中点小球）
├── BodyPoseStreamer.cs        # 身体 IOBT：全身关节推流（OVRBody 懒加载）
├── HeadPoseStreamer.cs        # 头部推流
├── FistTracking.cs            # 握拳手势检测
├── LogManager.cs              # HUD 日志缓存（pin/log 机制）
├── LogDisplayHands.cs         # 手部面板显示
├── LogDisplayNetwork.cs       # 网络/左侧面板显示
├── StatusHud.cs               # 状态覆盖层
├── MenuRecenter.cs            # 菜单重定位
└── VideoStreamManager.cs 等   # 可选视频推流（WebRTC）
```

---

## 构建配置（关键项，已校准）

以下配置是本项目正常运行的前提，改动前请留意：

| 配置 | 位置 | 值 | 说明 |
|---|---|---|---|
| `handSkeletonVersion` | `Assets/Resources/OculusRuntimeSettings.asset` | **1 (OpenXR)** | 必须与场景 OVRSkeleton 的 XR 骨骼类型匹配，否则手部每帧 NRE |
| `handTrackingSupport` | `Assets/Oculus/OculusProjectConfig.asset` | **1 (ControllersAndHands)** | 允许手柄+手 |
| `bodyTrackingSupport` | 同上 | **1 (Supported)** | 不能是 Required(2)，否则强制身体追踪 |
| `launchSimultaneousHandsControllersOnStartup` | `Scene.unity` OVRManager | **1** | 打包时开启 SHAC 支持 |
| `requestBodyTrackingPermissionOnStartup` | 同上 | **0** | 身体追踪权限改为流推 IOBT 时懒请求 |

---

## 更新日志

### 2026-10-16

**工程更名为 MORLA-XR，脱离上游、迁入自有仓库**
- **动机**：本工程从上游 `xinglingdongli/astral-tracking` 独立出来，迁到自有仓库 `https://github.com/loopkok/morla-xr`，以此为 origin 继续开发，需要一套自己的命名。
- **改名范围**：Unity `productName` → `MORLA XR`（即 Quest 应用显示名 —— 自定义 `AndroidManifest.xml` 用的是 `android:label="@string/app_name"`，`app_name` 由 Unity 按 `productName` 生成，**manifest 无需改动**）；Android 包名 → `com.loopkok.morlaxr`；`companyName` → `loopkok`；`metroPackageName`/`metroApplicationDescription` → `MORLA XR`；`Assets/Scripts/` 下 20 个脚本统一包进 `namespace MORLA.XR`；场景内菜单标题文本 → `MORLA XR`；工程文件夹 → `D:\unity_data\MORLA-XR`。
- **为什么改名是安全的**：全部脚本原本处于全局命名空间，改写前已确认**没有**任何按字符串查找类型的代码（无 `GetComponent("...")`/`SendMessage`/`Resources.Load<T>`/`typeof(...).Name`）；场景对脚本的引用是 `.cs.meta` 里的 GUID + fileID，与命名空间无关。无 asmdef，全部编进 `Assembly-CSharp`，程序集名不变。
- **未改动**：`Standalone`/`iPhone` 的包名、`AndroidMinSdkVersion: 32`、`AndroidTargetSdkVersion: 33`、`AndroidBundleVersionCode: 7`；下游 PC 侧工程名 `astral_ws` / `quest3_hand_mocap` 与本端协议（`@q3m` 包头、载荷行格式）均不受影响。
- **⚠️ 对设备的副作用**：Android 包名变更意味着设备上是一个**全新应用** —— 旧 `com.astral.astraltracking` 不会被覆盖，需手动 `adb uninstall`；PlayerPrefs 全部重置，那套「仅全新安装生效」的默认值重新生效；**推流目标 IP/端口需重新填写**。
- **验证边界**：本次只改源码、工程设置与文档，**未在 Windows Unity 6000.0 编译、未构建/安装 APK**，改名后的构建与实机表现尚未确认。

### 2026-10-15

**Quest 遥操数据行增加 session/序号/单调采样时间及自动校时应答**
- **动机**：PC 接收端新增源端年龄与防重放校验后，旧 `@q3 <session> <sequence> <utc_ms>|` 方案要求 Quest 和 PC 手动同步 UTC；时钟偏移会让有效包被误判为过期或来自未来。
- **封包格式**：新增 `QuestPacketEnvelope`，进程启动时生成 GUID session；按载荷流名分别维护递增 sequence；用 `Stopwatch.GetTimestamp()` 换算单调微秒时间。手腕/landmarks、手柄姿态与按键、头部及 IOBT body 行统一附加 `@q3m <session> <sequence> <monotonic_us>|`，载荷文本保持不变。单行高频路径直接封装以避免 `Split/Join` 分配；多行 body 消息逐行编号后再以 TCP 行分隔符连接。
- **时钟握手**：接收端发送 `@q3sync <nonce> <pc_send_monotonic_ns>`；Quest 回送 `@q3sync_ack <session> <nonce> <quest_receive_us> <quest_send_us>`，提供 PC 往返两端和 Quest 收发两个单调时刻，供接收端估算相对偏移与 RTT。TCP 使用后台读取任务响应服务端 challenge，限定累积行缓冲；UDP 使用异步接收回调识别 challenge 并回发到请求端。手、柄、头、身体四类 streamer 都接入相同封包/校时工具。
- **并发处理**：TCP 移除阻塞式 `ReceiveTimeout` 轮询，保留长连接上的后台 challenge listener；`WriteTcp` 和校时应答共用 stream 锁，避免遥操数据与 ACK 并发写入造成字节交织。UDP listener 在处理应答后继续挂起接收。新增 Unity `.meta` 文件以纳入脚本资源标识。
- **兼容与验证边界**：旧 APK 不发送新包头，需 PC 接收端显式打开 legacy 才能接收，且没有源年龄、序号和 session 防重放保护。此次提交只更新 Unity 源码；按本次要求未运行测试，也未在 Windows Unity 6000.0 编译或生成/安装 APK，因此 Quest 端握手和运行时效果尚未现场确认。当前没有新增 RTT 或丢包率实测值。

旧版 `@q3` UTC-ms 包头曾要求 Quest 与 PC 对时；本次改为 `@q3m` 单调时钟及 challenge/response 自动校准，接收端改动见 `astral_ws/src/quest3_hand_mocap`。

### 2026-09-15

**位姿量化修复（F4/F3 → F7/F6，消除 0.1mm 慢速阶梯）**
- 动机：真机遥操慢速平移"抖抖的"、快速流畅。用遥操诊断 JSONL（`teleop_teleop_left.jsonl`，117.7s 会话）实测定位。
- 根因：`HandLandmarkStreamer.AppendVector3`/`AppendQuaternion`（**手/柄/头/身体全部位姿流共用**）位置用 `ToString("F4")`（0.1mm 量化）、四元数用 `F3`（0.001 量化）。下游日志实测：VR 腕位原始目标 **36% 时间零速**（台阶停帧）、步长呈 0.1mm 整数倍、76Hz 到达；`pos_smoothing=0.25`（τ≈14ms）只把高频抖动滤掉一半（raw 22.7 → cmd 10.7 mm/s）；慢速段（33mm/s）抖动占比 ~1/3 肉眼可见，快速段被淹没。实测 7 关节均带高频抖动（p95 最高 690 mrad/s）——抖动真实传到实体臂。IK/安全/人肘先验排除（metrics 计数干净，仅 `hard_follow`）。
- 修复：`F4→F7`（0.1µm @1m ≈ float 原生精度）、`F3→F6`（1e-6）。**协议行结构不变**（仍逗号分隔 float），`quest3_hand_mocap` 解析无需改动（`float()` 吃任意精度）。
- 数值：F4 时慢速 cmd 高频抖动 med 10.7 / p95 42.1 mm/s；F7 后阶梯在源头消失，剩余仅为 Quest 追踪本身连续噪声。
- 待验证：**Windows 重建 APK 并重新部署后**，重录一段慢速平移，预期 `vr` 零速占比大幅下降、cmd 抖动显著减小。也可先用下游 `pos_smoothing 0.6`（τ≈39ms，抖动 10→5mm/s，代价 ~40ms 滞后）临时缓解，无需重建 APK。

---

### 2026-08-28

**发送节奏修复（手柄 ~50Hz → 72Hz）**
- 根因：`ControllerPoseStreamer`/`BodyPoseStreamer` 限流阈值 `1/72` 恰等于 72Hz 帧周期，帧时间抖动导致隔帧丢包，手柄实测 wrist_gap 20ms（p95 28ms）≈ 50Hz，而手部为 13.9ms。
- 修复：`_timer = 0f` → `_timer -= frequencySeconds`（自我校正不丢帧），手柄/身体/头部三处同修。

**头部 30Hz → 跟随帧率**
- `HeadPoseStreamer` 原为写死的 30Hz，改为跟随显示刷新率。

**90 Hz 显示刷新支持**
- 菜单新增「90 Hz」开关（PlayerPrefs 持久化）：`OVRPlugin.systemDisplayFrequency` 请求 90 Hz，从 `systemDisplayFrequenciesAvailable` 选最近可用档，不支持自动回落。
- 三个推流器的发送上限统一提为 240 Hz（永不触顶），实际发送率 = 显示刷新率：72Hz 默认，开 90 Hz 后手/柄/头/身体均 ~90 Hz。

**默认配置调整**（仅对全新安装生效；PlayerPrefs 会覆盖默认值，旧设备需先卸载再装）
- 协议：默认 **TCP 有线**（无存档时 `protocolDropdown.value = 1`，并联动填入 127.0.0.1:8000）
- Head Pose：场景 `Tgl_HeadPose` 默认开
- IOBT：默认开（`SavedMixed` 默认改 0，Mixed 默认关，互斥后 IOBT 生效）
- Landmarks 可视化：场景 `Tgl_visualize` 默认开
- Debug Info：场景 `Tgl_debuginfo` 原本即默认开
- 90 Hz：`Saved90Hz` 默认 1

### 2026-08-26

**手柄按键/摇杆推流**
- `ControllerPoseStreamer` 在手柄位姿行后同帧追加一行 `{side} buttons:, trigger, grip, stickX, stickY, mask`（mask：bit0=主键 bit1=副键 bit2=摇杆按下 bit3=Menu bit4=扳机点击 bit5=握把点击），下游 `quest3_hand_mocap` 解析为 `sensor_msgs/Joy`。
- 新增 `ButtonHudLine`，菜单页与推流中均在 HUD 显示手柄按键状态（Mix 行下方）。

**菜单页手柄操作（ControllerMenuPointer，新文件）**
- 自包含 controller→uGUI 桥：从手柄（`OVRInput` 真实姿态）发蓝色射线，命中菜单控件时显示末端小球，扣扳机点击（Button/Toggle 直接 `Invoke`，Dropdown 等走 `ExecuteEvents`）。命中检测用画布自身 `worldCamera` 的 `GraphicRaycaster`，消除瞄 A 点 B 的偏移。
- 手柄放下约 3 秒无操作射线自动隐藏，一动作即恢复；点击不受闲置门控影响。
- 菜单页 `Mixed` 默认开但 SHAC 未启用，导致 `IsControllerActive` 走 SHAC 分支误判手柄不活跃 → 按键 HUD 显示 none。修复：SHAC 分支仅在「推流中 + Mixed」时启用，菜单页一律走「手未追踪 + pose 有效」判定。

### 2026-08-21

**IOBT 髋部坐标系重定向（骨头系 → 躯干系）**
- 根因：`ToBodyRelative` 直接用 `FullBody_Hips` 骨头局部系（+X下/+Y前/+Z左）当父系，导致 IOBT 模式下手/头落在怪朝向，下游 `unity_pose_to_robot` 轴映射也对错轴。
- 修复：新增常量 `_hipsBoneToTorsoFix = (-0.5, 0.5, 0.5, 0.5)`，把 hips 骨头系重定向成自然躯干系（+X右/+Y上/+Z前）。`_lastBodyReferencePose` 与 body 包里的 hips 朝向均用重定向后的值，整棵树（hips + body 关节 + head + wrist + controller）统一在躯干系。

**body 关节改为 Quest 端转髋部相对**
- 之前：body 包里所有关节发世界系，下游 `quest3_hand_mocap` 再用 `pose_in_parent_frame` 转髋部系。
- 现在：Quest 端在 `BuildAndSendPacket` 里对非 hips 关节直接 `ToBodyRelative`，发髋部相对；hips 仍发世界系（作为 body 根 + 下游 hips_pose 来源）。下游不再做 `pose_in_parent_frame`，只做轴约定转换。

### 2026-08-19

**修复手部追踪失效（菜单无法点击）**
- 根因：`handSkeletonVersion` 为 OVR(0)，与场景 OpenXR 骨骼类型(4/5) 不匹配，`FromOVRHandDataSource.UpdateDataPoses` 每帧抛 NRE。
- 修复：改为 OpenXR(1)。

**身体追踪 IOBT 接入**
- 身体追踪权限/特性改为可选（Supported），启动时不强制开启。
- `OVRBody` 改为懒加载，仅流推 IOBT 时启用；权限懒请求。
- 修复调试骨骼球使用 `Shader.Find("Standard")` 在 URP 下返回 null 导致每帧抛异常、打断身体数据发送（改用 URP Lit/Unlit）。

**手柄接入 + SHAC（Mixed 模式）**
- 手柄 6DoF 推流；Mixed 模式用 `GetControllerIsInHandState` 判断「在手」。
- IOBT 模式下该 API 失效，改用「手部追踪被关 + 手柄位姿有效」作代理信号。

**HUD 精简**
- 修复左右手日志源都指向 `Right` 导致 wrist 重复（左手改 `Left`）。
- IOBT 只显示在左侧面板、head 只显示在右侧面板。
- 两侧面板只显示最新 1 条日志，消除重复。

---

## 使用说明

1. 用 Unity 6000 打开项目，切到 Android 平台。
2. 在 `OculusProjectConfig` 确认手柄/身体追踪配置（见上表）。
3. Build 并部署到 Quest 3（需开发者模式 + USB 调试）。
4. 启动 App：菜单里设置协议/IP/端口、选择手部模式、勾选 Mixed 或 IOBT。
5. 点「开始推流」，PC 端监听对应端口接收数据。Unity 源码给每条数据行附加 session、序号与 Quest 单调采样时间；PC 接收端自动完成相对时钟校准，系统时钟无需手动对时。严格模式默认拒绝旧版无包头 APK。
