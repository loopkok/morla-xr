# 2026-11-01 — 手柄输入被工作区吃掉：握把/摇杆/ABXY 交还下游

## 现象

「我要把手柄的 grip 键当下游输入，但只要一按 grip 就暂停发送，像是按了 grip 就开始调整窗口位置。」

## 根因（两条，都会独立成立）

1. **握把就是搬运键**。`WorkspaceGrab.GripHeld()` 读 `Axis1D.PrimaryHandTrigger > 0.7`，按下即 `Begin()` → `Session.EnterUiInteraction()` → `Pause("设置操作中：位姿发送已暂停")`。而按不变量 8，UI 交互引起的暂停**不会自动恢复**，必须显式点「开始发送」。
2. **握着握把时恢复门也是关的**。`WorkspacePointer.Update` 的"输入是否已释放"判据原来是：

   ```csharp
   if (down || OVRInput.Get(OVRInput.Button.Any, controller) ||
       OVRInput.Get(OVRInput.Axis1D.PrimaryHandTrigger, controller) > 0.2f) _released = false;
   ```

   `SessionCoordinator.Update` 里 `if (!WorkspacePointer.InputsReleased()) { _releaseSince = -1; return; }`——只要握把还按着，`InputsReleased()` 恒为 false，点「开始发送」也起不来。表现就是"一按 grip 就停了，松手也不回来"。

换句话说：握把同时被当成"搬运面板的键"和"没松手的证据"，而它其实是下游要用的数据。

## 全量输入盘点

手柄每帧随位姿行发出 `{side} buttons:, trigger, grip, stickX, stickY, mask`（`ControllerPoseStreamer`，mask：bit0=ABXY 主键、bit1=副键、bit2=摇杆按下、bit3=Menu、bit4=扳机点击、bit5=握把点击）。凡是出现在这行里的，用户都可能拿去操控下游，**不能**再绑到工作区 UI 上。

| 输入 | 修改前是否会影响应用 | 现在 |
|---|---|---|
| **握把**（`PrimaryHandTrigger`） | ① 按住 ≥0.7 开始搬运 → 暂停发送；② 计入"未释放" → 恢复门常闭 | **不参与任何 UI**，纯下游数据 |
| **扳机**（`PrimaryIndexTrigger`） | 点击/拖动 UI；指向**任意** UI（含面板背景、视频）按下就暂停 | 只在**可交互控件**上暂停（`IsInteractive`：祖先有 `IPointerClickHandler` 或 `IDragHandler`）；仍是点击与拖动的主输入 |
| **摇杆**（`PrimaryThumbstick`） | 射线压在**任意** UI 上推摇杆就暂停；不在列表上也会发 scroll 事件 | 只在射线落在 `ScrollRect` 上时滚动并暂停 |
| **摇杆按下**（`PrimaryThumbstick` 键） | 计入 `Button.Any` → 阻止恢复 | 不参与 UI |
| **ABXY/X/Y** | 计入 `Button.Any` → 阻止恢复 | 不参与 UI |
| **☰**（`Button.Start`，仅左手） | 短按开/关设置（会暂停）；长按 0.45 s 搬运（会暂停） | **不变**：这是唯一保留的手柄工作区入口 |
| **手部捏合** | 点击/拖动 UI，计入"未释放" | 不变 |
| 手柄姿态/位置 | 射线命中 | 不变（悬停不算 UI 意图） |

`ControllerMenuPointer`（旧菜单的射线指针）读到握把/扳机/摇杆，但 `AppManager.Awake()` 里 `oldPointer.enabled = false`，不参与运行时，无需改动。

## 改动

1. `WorkspaceGrab`：删掉握把入口与 `GripThreshold`、`GripHeld`、`_side`、`_byMenu`，搬运只剩**长按 ☰ 0.45 s**；仍只认左手柄（`Tracking.ControllerActive(false)`）。
2. `WorkspacePointer._released`：改为只认**扳机**与 **☰**：

   ```csharp
   if (down || OVRInput.Get(OVRInput.Button.Start, controller)) _released = false;
   ```

3. `WorkspacePointer.Process`：新增 `ScrollRect` 判定与 `IsInteractive()`，暂停判据收紧到

   ```csharp
   ((down && !pointer.down && IsInteractive(hit)) || (Mathf.Abs(scroll) > 0.5f && scrollable))
   ```

   滚动事件也只在 `scrollable` 时派发。
4. **扳机仍计入释放门（有意保留）**：`InputsReleased()` 里的 `down`（扳机 > 0.2）不删。含义是"一手常按扳机当下游输入时，另一手点「开始发送」要先把那只手的扳机松开才会生效"。2026-11-01 与用户确认：保持现状，不做成"只等 UI 按下结束"。

## 仍然会暂停发送的入口（设计如此，都是显式 UI 操作）

- 点击按钮 / 开关 / 输入框（`WorkspaceUIController` 各回调）。
- 拖动状态栏或设置标题栏（`WorkspaceWindowDrag.OnBeginDrag`）。
- ☰ 短按开设置、长按搬运（`WorkspaceGrab`）。
- 这些暂停**不会自动恢复**：点「开始发送」，且需要松开扳机/☰（`InputsReleased`）并等到新样本。

## 验证

- Roslyn 静态编译（Unity 自带 csc、editor 与 android 两套 define）：**0 错误**。
- **仓库 APK 已更新为本轮源码的构建**（2026-11-01 22:16，101,833,178 字节，`com.loopkok.morlaxr` / `versionCode 8` / `versionName 1.1.0`）。核对 `global-metadata.dat`：有 `IsInteractive`，没有 `GripHeld`、`SourceHeld`——旧的握把入口确实不在包里。**仍未经真机验收。**
- 需要在设备上确认：
  1. 按住/松开握把，发送**不**暂停，`grip` 值照常出现在下游；
  2. 握着握把时点「开始发送」能起来；
  3. 摇杆在面板背景上推不动列表时不再暂停；
  4. 扳机指向面板空白处不再暂停，指向按钮仍然可用；
  5. 长按 ☰ 仍能搬运工作区，短按仍能开设置。
