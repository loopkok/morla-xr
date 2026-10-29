# MORLA XR 工作区视觉改版（2026-10-28）

范围：只改外观与控件形态，不改操作语义。开始/暂停、UI 交互暂停发送、关闭设置不自动恢复发送、输入释放门、失联自愈、窗口拖动目标、六页内容与回调全部保持原样。

## 动机

1.1 的工作区是浅灰面板 + 深色文字，在透视画面里对比度低；开关是一整行"标签 + 开启/关闭"的文字按钮，单选是三颗并列按钮且只有选中态填色，状态栏与提示行只是裸文字。用户反馈"太丑且不清楚"。

## 设计基线（参考 macOS / visionOS 的深色 vibrancy）

- **材质**：深色半透明面板（`Window` = 黑色 88%），1 单位发丝描边（白色 14%），投影为 SDF 生成的柔化圆角阴影。所有圆角与阴影由代码按带符号距离场生成（`UiKit.Sdf` / `ShadowSprite`），9 宫格切片，不新增任何美术资源，也不依赖已导入的 `MorlaPanel`。
- **色板**：主文本白 96%，次级文本白 60%（高对比模式提升到 82%），强调色 `#0A84FF`，危险 `#FF453A`，成功 `#30D158`，警告 `#FFD60A`。
- **控件**：主按钮填强调色；次按钮为白色 10% 胶囊；悬停/按下用 `Selectable` 的乘法 ColorTint（静止态 0.82 亮度、按下 0.65），因此不需要每帧改材质。
- **开关**：`SwitchRow` —— 左侧标签、右侧 74×40 轨道 + 32 圆形滑块，开启填强调色；整行可点。
- **单选**：`Segmented` —— 650×56 分段轨道，选中段为白色 24% 抬升块，未选中段透明，替代原来"三颗独立按钮靠填充色区分"的写法。
- **侧栏**：`QuietButton` 列表行，选中为强调色 26% 胶囊 + 白字，未选中仅悬停可见（静止 3% 白）。
- **文字层级**：页标题 32 加粗、控件标签/说明 22 次级、正文 26 白、数据块 23–26 白。设置页动作行改为左对齐（仅默认动作居中）。
- **主 HUD**：状态栏与工具栏改成带投影的浮动胶囊；状态栏左侧加状态圆点（发送=绿、连接中=黄、暂停=橙、故障=红），提示行加半透明底板并随「暂停跟随」按钮让位；工具栏的麦克风录音中变红底红图标、视频播放中变蓝底。
- **窗体**：标题栏改成整条可拖动（原来只有标题文字可拖），左上角为 macOS 红色交通灯关闭点（保留原 `HideSettings`），标题栏与页脚各有一条发丝分隔线，侧栏与内容区之间有竖向分隔线。

## 兼容性

- `UiKit` 的公开 API 仍保留 `Surface/Subtle/Selected/Ink/Blue` 与 `Panel/Text/Button/Input/ScrollPage/FlowText/FlowButton/FlowRect/ApplyTheme/SetReadable`，新增 `QuietButton/SetSelected/SwitchRow/Segmented/SegmentedRow/Dot/Divider/Shadow`。
- `MorlaTextStyle` 增加 `secondary` 标记，`ApplyTheme` 据此在高对比模式间往返时不丢次级文字透明度；窗口面板透明度同理按 RGB 识别。
- `WorkspaceUIController` 的 `Toggle`/`Choices` 改为开关行/分段控件，重建页面（`SelectPage`）的既有行为保留；导航选中状态改由 `UiKit.SetSelected` 统一处理。
- 麦克风/视频工具栏按钮新增底色状态，只读，不改变原有按钮行为。

## 验证与边界

Windows Roslyn（`csc.dll`，editor 与 Android player 两套宏，397 个引用）静态编译 **0 错误**，仅剩既有 CS0618/CS0649/CS0414 警告。便携验证工具需要 .NET 8 SDK，本机只有运行时，未执行；**未在 Unity 中真实编译、未重建 APK、未在 Quest 3 上检查双眼观感与射线命中**。发丝描边在 1 m 距离约 1.3 mm，若真机偏细/偏糊，优先调 `UiKit.Stroke` 的 alpha 与 `Sdf` 的环宽（`1.3f`）。

# 真机反馈修复（2026-10-28 补丁）

来自 Quest 3 实测的四条反馈：手部射线看不清落点、射线比系统手射线低、视频信令反复阻断"开始/恢复发送"、点击"恢复发送"后射线离开按钮立即暂停。

## 1. 手部射线落点与位置

- **症状**：手捏合时看不清射线末端落在哪个控件上；射线看起来比 Quest 3 系统界面的手射线更靠下。
- **根因**：射线起点用的是 `OVRHand.PointerPose` 的位置（靠近腕部，且该 pose 在本场景无父节点，需要自行从 tracking space 换算），系统的捏合射线则以拇指与食指指尖之间的捏合点为轴心；同时整条射线只有 `startWidth 0.002 / endWidth 0.001` 的一条线，没有任何落点标记，而且只在已经压到 UI 上时才显示（`enabled = hit != null || down`），瞄准过程中完全没有反馈。
- **修法**（`WorkspacePointer`）：
  - 手部数据整体改走 Interaction SDK 的 `IHand`（经 `HandLandmarkStreamer.Hand` 按左右手取用）：`GetPointerPose` 已经是世界坐标，不再做 `ToWorldSpacePose(Camera.main)` 的手工换算；捏合判定用 `GetIndexFingerIsPinching()`。
  - 射线**轴心改为拇指尖与食指尖的中点**（`HandThumbTip` / `HandIndexTip` 世界坐标），方向仍取 pointer pose 的朝向；与 pointer pose 距离超过 30 cm 时（数据异常）回退到 pointer pose，避免把射线甩飞。
  - 新增**落点光标**：画布上的实心圆点（内芯 13–17 单位、外圈深色光晕），指向 UI 时为白色、按下时变强调蓝并缩小，未压到控件时为 62% 白。它是画布子物体且 `raycastTarget = false`，每帧 `SetAsLastSibling` 置顶；局部 z 抬到 -5 单位（约 4.5 mm）避免与共面的面板 z-fighting。
  - 射线本体加粗到 3.5 mm→1.5 mm、加端点圆角、关闭投影；**只要射线命中画布平面就显示**（6 m 内），不再等到压到控件；近手端留出按距离缩放的 5 mm–5 cm 间隙。
- **未验证**：捏合点轴心与系统射线的观感差异需要在真机上比对；若仍偏低，下一步检查 `Hand.PointerPose` 的朝向是否与场景相机共面。

## 2. 视频信令阻断开始/恢复发送

- **症状**：即使已经关掉视频传输，点"恢复发送"仍会因为视频断开而暂停。
- **根因**（三处叠加）：
  1. `AppManager.StopVideo()` 只停会话，不清 `videoEnabled`；下次 `RequestStart` 看到 `videoEnabled && video == Off` 又悄悄 `StartVideo()`，信令连不上就 `VideoFault` → `Pause`。"停止视频接收"按钮和工具栏"视频"按钮都会走到这里。
  2. `VideoFault` 只看 `criticalVideo`，不看 `videoEnabled`；已经关掉视频后，旧会话迟到的 `FailFatal` 仍会把新的发送会话打断。
  3. `VideoStreamManager.FailFatal` 没有区分"已经拆掉的会话"：`StopVideoSession` 之后到达的错误仍会上报。
- **修法**：
  - 新增 `AppManager.DisableVideo()`（= 清 `videoEnabled` + 保存 + 刷新设置页 + `StopVideo`），"停止视频接收"改用它；工具栏"视频"按钮仍是"关着就开"的显式动作。
  - `VideoFault` 在 `!Settings.videoEnabled` 时只记一条提示，不置 `Faulted`、不暂停发送——避免关掉视频后 HUD 永久停在"视频异常"。
  - `StartVideo` 先记录 `videoEnabled = true` 再做地址校验，避免配置类故障被上面的新规则吞掉。
  - `FailFatal` 在 `_state == Idle` 时直接丢弃（属于已停止的会话代）；必要画面中断的暂停同样加上 `videoEnabled` 前置条件。
  - 必要视角提示改为可执行的两种解法：`画面显示 = 隐藏` 时提示"必要视角已隐藏：显示主画面，或在「视频与显示」关闭「主相机为必要视角」"，否则提示等待首帧 / 停止视频接收；`VideoFault` 的暂停原因也附上关闭"主相机为必要视角"的入口。
- **行为边界（有意保留）**：`videoEnabled && criticalVideo` 且画面隐藏或中断时仍会暂停发送——这是"必要视角"的安全语义，本次只把它说清楚，没有放宽。

## 3. 点"恢复发送"后射线离开按钮立即暂停

- **症状**：点"恢复发送"后只要射线还在按钮上就不停；射线一离开按钮立刻暂停。
- **根因**：`WorkspacePointer.Process` 把**悬停变化**当成 UI 意图：`if (hit != null && (hit != pointer.hover || down && !pointer.down)) Session.EnterUiInteraction()`。工作区就在用户面前，射线扫过任何控件都会触发一次 `Pause`。
- **修法**：只有**按下瞬间**（`down && !pointer.down`）或**摇杆滚动**（`|scroll| > 0.5`）才 `EnterUiInteraction()`；悬停只走 `pointerEnter/Exit`。按下才暂停仍然防止"目标点被点击的同时位姿继续下发"。
- **副作用检查**：设置页的开关/分段控件重建页面时 `hit` 会变化，之前每次切换都会顺带暂停一次发送，现在不会；拖动窗口（`WorkspaceWindowDrag.OnBeginDrag`）与左手 Menu 键仍显式暂停。

## 验证与边界

Windows Roslyn（editor 与 Android player 两套宏，397 个引用）静态编译 **0 错误**，仅剩既有 CS0618/CS0649/CS0414 警告。`Tools/Validation` 需要 .NET 8 SDK，本机仍只有运行时，未执行。**未在 Unity 中重新编译、未重建 APK、未在 Quest 3 上复测**：手部射线的观感、光标大小、视频关闭后的恢复发送、以及射线离开按钮后不再暂停，都需要真机确认。

# 连接错误提示（同日补丁）

- **症状**：点击"开始发送"后 HUD 显示 `位姿连接异常：left: mono-io-layer-error(111)`。
- **含义**：`111` 是 errno `ECONNREFUSED`——`left` 通道的 TCP 连接被拒绝。默认配置是 `USB / ADB` → `127.0.0.1:8000`，说明目标端点没有监听：主机侧接收器未启动，或主机没做 `adb reverse tcp:8000 tcp:8000`。这是环境/链路问题，不是本次改动引入的。
- **修法**：`TelemetryChannel.Describe` 把 `SocketException` 的 `SocketErrorCode`（以及 Mono 只暴露在文本里的 `111`）翻译成可执行提示，附带实际连接的 `host:port` 与模式相关的下一步（USB 模式提示 `adb reverse`，局域网提示放行端口）；超时、主机不可达、连接重置同理。返回值仍是纯文本，线程边界不变（错误字符串经 `Enqueue` 回主线程）。
- **验证**：Roslyn 静态编译 0 错误；未真机复测文案。
