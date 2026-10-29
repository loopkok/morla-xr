# 2026-10-29 — 工作区 3D 拖动（远近 + 等视角缩放）

## 需求

「整个界面想往远处拖，像 Quest 3 系统的界面一样，可以随着拖动远处放大缩小。现在只能在一个平面拖。」

现状确认为真：`WorkspaceWindowDrag`（设置标题栏）把窗口钉在**抓取瞬间的那个平面**上，`Plane(target.forward, start)` + `ClampMagnitude(..., 0.7f)`，深度完全不变，也不缩放。

## 根本约束：uGUI 的拖动事件里没有"远近"

`WorkspacePointer` 先把手/手柄射线打到画布平面，再把交点投影成 `data.position` 交给 `GraphicRaycaster`。于是 `IDragHandler` 拿到的只是**画布平面上的二维交点**：手往怀里收多少、往前推多少，这个事件里全丢了。只在 `PointerEventData` 上做算术，无论怎么改都只能得到平面内位移 —— 这正是"只能在一个平面拖"的原因。

## 做法

### 1. 把真实射线暴露给拖动处理器

`WorkspacePointer` 新增按 pointer id 缓存的射线表：

```csharp
private static readonly Dictionary<int, Ray> _dragRays = new Dictionary<int, Ray>();
public static bool TryGetDragRay(int pointerId, out Ray ray) => _dragRays.TryGetValue(pointerId, out ray);
```

拖动期间每帧在派发 `beginDrag/drag` 前写入 `_dragRays[data.pointerId] = ray`，在 `Release`/`ReleaseOutside` 里移除。按 id 分槽是为了两只手同时捏住时不会互串射线。鼠标（编辑器）没有这条路径 → 自动退回旧的平面拖动。

### 2. 抓取点钉在手上

```csharp
Vector3 grabWorld  = data.pointerCurrentRaycast.worldPosition;  // 抓到的那个世界点
_grabOffset = grabWorld - hand.origin;                          // 手到抓取点的固定偏移
```

之后每帧 `anchor = hand.origin + _grabOffset`。手朝自己收 → `anchor` 靠近 → 窗口靠近；手前推 → 窗口推远。横向/纵向同样跟手，等于把整块面板黏在手上（Quest 抓窗口的手感）。

### 3. 等视角缩放（越远实际越大）

`WorkspaceUIController`：

```csharp
public const float BaseCanvasScale = 0.0009f;   // 1100 面板单位 ≈ 1 m
public const float PlaceDistance = 1.2f;
public static float ScaleForDistance(float distance) =>
    BaseCanvasScale * Mathf.Clamp(distance / PlaceDistance, 0.5f, 2f);
```

缩放与距眼距离成正比 → **视角大小恒定**：字在任何距离上一样清楚，不再需要单独的缩放控件。`Recenter`、恢复保存位姿之后调用 `ApplyDistanceScale(cam.transform.position)`。

### 4. 位置要按"缩放后"的抓取点反算

放大是围绕画布轴心（面板中心）做的，如果只把轴心放到 anchor，你捏住的那一点会在放大时从手指下滑走。所以位置取：

```csharp
position = anchor - rotation * (_grabLocal * scale);   // _grabLocal 是画布本地坐标
```

`_grabLocal` 在开始拖动时用 `canvas.transform.InverseTransformPoint(grabWorld)` 取一次，之后乘当帧的 `scale` 还原世界偏移，抓取点始终停在手上。

### 5. 边界与朝向

- 距离夹在 **0.45–2.4 m**：不会推进脸里，也不超出 `Reachable`（≤2.5 m）导致 2 s 后被自动归位。
- 不高于眼睛下方 1 m（与 `Reachable` 的 `offset.y >= -1` 一致）。
- 朝向按水平方向 billboard（`ProjectOnPlane(..., Vector3.up)` + `LookRotation`），推远时面板始终正对你，不会保持旧倾角。
- 拖动开始仍走 `Session.EnterUiInteraction()`（按下即暂停发送，不变量 8）；结束 `OnEndDrag` 调 `SaveCurrentPose()` 落盘。

### 6. 多一个抓取入口

状态栏现在也能抓（`statusBar` 同挂 `WorkspaceWindowDrag`）：轻点是打开概览页（原有行为），拖过 8 px 阈值 uGUI 自动取消点击、改成移动整块面板。概览页说明里加了一行操作提示。

### 7. 手柄搬运（按住即抓）

第一版只能"射线对准状态栏/标题栏 + 扣扳机"才能拖，手柄用户反馈"按住菜单键拖不动"——因为源码里从来没有按键抓取这条路。新增 `WorkspaceGrab`（`AppManager` 挂在工作区根节点上）：

- **按住侧键（握把）**任意一只手 → 立刻抓住整块工作区，面板的相对位置固定在手上（`_offset = 面板位置 − 手柄位置`），推拉/横移 1:1 跟手，不需要先瞄到标题栏。
- **长按 ☰ 0.45 s** 同样进入搬运；**短按 ☰ 仍是开/关设置**。为了不打架，`AppManager.Update` 里原有的 `Button.Start` 处理已删除，☰ 统一由 `WorkspaceGrab` 负责：长按吞掉那次点击（`_menuUsedForGrab`），释放时才决定是开设置还是结束搬运。
- 距离夹 0.45–2.4 m、低于眼睛 1 m 抬起、按水平方向 billboard、缩放走同一个 `ScaleForDistance`——和手部捏合拖动完全一致的远近与"等视角"表现。
- 只有该侧**当前生效输入**的手柄能搬运（`Tracking.ControllerActive`），桌上的手柄不会误动面板；抓取瞬间同样 `EnterUiInteraction()`（暂停发送，不变量 8）。

### 8. 第一版真机反馈的四处修正

真机（1.1.0 带本轮改动）反馈：「扣扳机拖动一卡一卡、和手位置不一样、基本没放大缩小、右手点右端面板就往右倒」。逐条对回代码：

| 现象 | 根因 | 修正 |
|---|---|---|
| 右边点就往右倒、左边点就往左倒 | 朝向按 `head → anchor` 算，而 anchor 就是**抓取点**；抓右端 → 朝右端转 | `FaceUser` 只取 `ProjectOnPlane(camera.forward, up)` 的偏航，朝向与抓在哪无关 |
| 一卡一卡 | 两个来源：① 面板一旦转到射线背后，`Plane.Raycast` 失败 → `ReleaseOutside` 结束拖动 → 下一帧重新 pointerDown 再等 8 px 阈值；② 侧键握着时 `WorkspaceGrab` 和射线拖动同时每帧写位姿，互相抢 | ① 拖动已成立时平面未命中继续派发 drag 事件（射线照旧缓存）；② `Carrying`/`IsDragging` 双向互斥，侧键阈值 0.5 → 0.7 |
| 和手位置不一样 | 抓取点用固定世界偏移，手腕一转光标就滑走了 | 改成 `anchor = ray.GetPoint(抓取距离)`：抓取点始终在光标下，手前后移动才改远近 |
| 基本没放大缩小 | 等视角本身就"看起来一样大"——这是它的定义，不是没生效 | 默认改为**固定物理尺寸**（远处自然变小，Quest 系统界面的观感），设置页「拖远后的大小」可切回等视角 |

新增 `AppSettings.angularWindowScale`（默认 false）与「视频与显示」页的「拖远后的大小」分段控件；`Recenter`/恢复位姿后的 `ApplyDistanceScale` 也按这个开关走。

## 验证

- 静态编译（Unity csc + Unity/Meta 程序集，editor 与 android 两套 define）：**0 错误**。harness 在 `C:\Users\njk\AppData\Local\Temp\morla_check\compile.sh`（临时目录，不入库）。
- **未在 Unity 真实编译、未重建 APK、未真机验收**。设备上装的 1.1.0 / versionCode 8 不含本改动。
- 需要真机确认的点：手捏合抓取时的顺滑度（射线起点为捏合点，`_grabOffset` 约 0.7 m）、0.45 m 近端是否会糊脸、2.4 m 远端放大后是否遮挡环境、状态栏轻点/拖动的误触率。
