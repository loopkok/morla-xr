# 2026-11-02 — 手柄只有 20 Hz、左侧掉回"识别成手"

## 现象

- 手的数据 ~90 Hz，手柄只有 ~20 Hz。
- 拿手柄时左侧经常被判成"手"，界面提示"等待左侧信息停止发送"，实际手柄在手上且有电；有时按一下 trigger 才恢复。

## 实测（`Tools/morla_pose_compare.py`，8000/TCP，`adb reverse`）

一次会话，先手后手柄各约 30 秒：

| 时段 | Left wrist | Right wrist | Left controller | Right controller | Head |
|---|---|---|---|---|---|
| 13:52:47–13:53:12（手） | 84 Hz | 84 Hz | — | — | 90 Hz |
| 13:53:54–13:54:28（手柄） | — | — | **52–74 Hz** | **9–20 Hz** | 90 Hz |

关键判读：

- **连接丢包全程 0**，`变化Hz ≈ Hz` → 不是网络丢包，也不是"数据不刷新"，是 **App 自己跳过了发送帧**。
- 手柄阶段手腕帧数为 0 → 那一侧确实没有手被追踪，`HandTracked` 为 false。
- 右侧只有 9–20 Hz、左侧 52–74 Hz，间隔中位 45 ms（右手柄）→ 发送被 `ControllerActive()` 逐帧挡掉。
- 手阶段两腕各轴位移范围 0.24–0.31 m，手柄阶段 0.14–0.18 m，动作量级相当，频率差异不是"没动"造成的。

## 根因

`TrackingCoordinator.ControllerActive` 非 Mixed 分支：

```csharp
return !HandTracked(side) && OVRInput.GetControllerPositionValid(controller);
```

两个判据都不可靠：

1. **`!HandTracked` 把"手能看见"当成"没用手柄"**。SHAC 下（或 IOBT 期间手仍被追踪时）手和手柄会同时被追踪，手一出现这一侧就判给手 —— 这是"拿着手柄却识别成手"。
2. **`GetControllerPositionValid` 会短暂失效**。运行时不定期丢一下置信度，`ControllerActive` 当帧返回 false → `ControllerPoseStreamer.TrySendSide` 直接不发。右侧比左侧严重，说明两个手柄的判定不同步；按 trigger 会刷新控制器输入，所以"按一下就好了"。

两侧叠加：手一闪 → 判给手，手不断发 wrist；手柄一闪 → 不发 controller；下游看到左侧时有时无 → 250 ms 新鲜度 watchdog 让 `Ready()` 失败 → 会话暂停，界面显示"等待左侧信息停止发送"。

## 改动

1. **连接性先判**：`OVRInput.GetConnectedControllers() & controller`，没连上直接 false。
2. **`GetControllerPositionValid` 加 0.5 s 宽限**（`PoseGraceSeconds`）：短暂失效期间继续按最新位姿发送，超过 0.5 s 才判无效。避免一次抖动就触发整体暂停。
3. **手不再无脑赢**：手被追踪时不再直接判负，改用**手腕与手柄的世界距离**（`InHandRadius = 0.2 m`）。手柄在手上 → 距离小 → 手柄赢；手柄放桌上、手在别处 → 距离大 → 手赢。Mixed 模式仍走 SHAC 的真·在手检测。
4. **一侧只有一路源**：`HandLandmarkStreamer.OnHandUpdated` 改用同一个 `ControllerActive` 判据停发 wrist/landmarks（原来只在 `IsTouchPoseValid` 即 Mixed 下才停），避免同一侧同时发 `Left wrist` 和 `Left controller`。

## 第二轮实测（修完上面四条之后，用户重构建再测）

| 源 | 帧数 | 平均 Hz | 间隔中位 | >100 ms 间隙 |
|---|---|---|---|---|
| Left / Right wrist | 4443 / 4443 | 83.4 | 11 ms | 0 |
| Left controller | 3021 | 62.7 | **12 ms** | 0 |
| Right controller | 965 | **20.0** | 34 ms | 113 次（最大 323 ms） |
| Head pose | 9130 | 84.2 | 11 ms | 1 |

包内已核对含 `WristPosition` / `InHandRadius` / `PoseGraceSeconds` / `GetConnectedControllers`，确认新代码在跑。左手柄从 52–74 Hz 提升到间隔中位 12 ms（基本一帧一发），**右手柄仍是 20 Hz**，且间隙均匀分布在整段里（不是遮挡造成的成块丢失）。

结论：还有一处判据在否决右侧——**手部 `IsTrackedDataValid` 会变成"陈旧的真"**。手离开视野后该标志位仍为 true 一段时间，但 `WhenHandUpdated` 不再触发、手腕位姿停在几十秒前。于是新加的距离判定拿着陈旧手腕去比，判成"手在这一侧"，把手柄踢掉；同时因为手没有更新，也不会发出 wrist 帧——正好对应"没有手数据、手柄却只有 20 Hz"。

补的两条：

- `HandLandmarkStreamer.LastUpdateAt`：记录最近一次真正收到手部数据的时间。
- `TrackingCoordinator.HandTracked` 要求 **0.25 s 内更新过**（`HandFreshSeconds`）才算在场；陈旧的手不再参与这一侧的归属判定。
- 同时把"连接"与"位置有效"合并成一个 `usable`，任何一项抖一帧都不立刻停发，统一走 0.5 s 宽限。

## 第三轮：根因是"共用一个发送通道"，不是判定逻辑

用户重构建后再测，右手柄仍是 9–20 Hz，而 `[XRDIAG]` 显示两侧 `active=1` 一直成立——判定不是瓶颈。

看间隔分布才露馅：左手柄 580 次间隔 ≤15 ms（≈90 Hz），右手柄 11 / 46 / 112 / 145 ms 完全乱。原因是 `ControllerPoseStreamer` 两侧共用一个 `TelemetryChannel`，每帧提交 4 条消息（左手柄位姿、左按键、右手柄位姿、右按键），而该通道**只保留最新一条**（`TelemetryChannel.Submit` 覆盖 `_latest`）。同帧后面提交的把前面的挤掉，于是"有时右位姿丢、有时左按键丢"。

修法：整帧拼成**一条**消息提交一次（和手部 `wrist + landmarks` 的既有做法一致），一帧要么整帧到达、要么整帧丢弃。

## 第三轮：拿起手柄必现「等待有效追踪：左侧」

`[XRPAUSE] 等待有效追踪：左侧 t=25.53 left=手柄(stale 13ms) … head=头部(fresh)` —— 源 13 ms 前刚刷新、头部正常，却判 `left` 失效。

`TrackingCoordinator.ObserveSide` 的第三分支：

```csharp
else if (app.Status.sources[key].source != "手部") app.Status.sources[key].valid = false;
```

**"手还在追踪、但这一侧此刻不归手柄"** 的那一帧直接把 `valid` 置 false。拿起手柄的瞬间正好会闪出这一帧（手没消失、手柄刚被判定、手腕距离在 0.2 m 附近抖动），`Ready()` 当场失败；而暂停之后**永不自动恢复**，只能手动点「恢复发送」——这就是"必须点恢复发送"的原因。

修法（`ObserveSide` 重写）：

- 归手柄 → `Observe("手柄", true)`
- 手在追踪 → 交给 `HandLandmarkStreamer` 自己上报，**不再判失效**
- 两边都没有 → `SourceLossGrace = 0.5 s` 宽限，期间保持上一次的有效状态，过了才 `Observe("未检测", false)`

## 最终实测（第三轮修复后）

| 源 | 帧数 | 平均 Hz | 间隔中位 | p90 | >100 ms | 最大间隔 |
|---|---|---|---|---|---|---|
| Left controller | 4595 | 89.9 | 11 ms | 12 ms | 0 | 49 ms |
| Right controller | 4595 | 89.9 | 11 ms | 12 ms | 0 | 49 ms |
| Head pose | 4596 | 89.9 | 11 ms | 12 ms | 0 | 50 ms |

51 秒内没有任何一次「等待有效追踪」，只有用户主动点按钮的「位姿发送已暂停」。

## 附带修复

- `WorkspacePointer` 一侧只画一根射线：**手优先**（手在追踪就用手的射线、从食指+拇指捏合点出发），手不在才用手柄射线。曾一度按数据链路的 `ControllerActive` 归属，结果握着手柄时手部射线被清掉、只剩手柄射线（看起来"从手腕出来"），且捏合点不到按钮——指针归属必须和数据归属分开。
- 握着手柄时**不会**再发送手部数据（距离 ≤0.2 m 判在手），所以"握着手柄同时用手做手势"目前不支持；需要的话应做成设置项。

## 第四轮：运行时用手部位姿"合成"手柄位姿

用户把手柄放在桌上、转过身、用手操作软件，`[XRDIAG]`：

```
15:11:26  L conn=0 pv=1 hand=1 dist=0.000 active=1
```

`GetConnectedControllers()` 说没连上（conn=0），但 `GetControllerPositionValid` 仍为真，位姿与手腕**完全重合（0.000 m）**，而且随手的动作移动（各轴幅度 0.3–0.5 m）。结论：手柄没被真正追踪时，运行时**用手部位姿合成一个手柄位姿**。

因此：
- `GetControllerPositionValid` 不能用来判"在手"（恒真）
- "手腕距离"判据也不能用（合成后恒 0）

最终判据只剩一条：**`GetConnectedControllers()`**（桌面 conn=0、拿在手里 conn=1），加 0.5 s 宽限。

## 第五轮：手部流自己把源判死

切换仍会暂停，日志：

```
15:18:33 [XRPAUSE] 等待有效追踪：左侧 t=23.79 left=手部(stale 63ms)
```

63 ms 在 250 ms 新鲜度窗口内却判失效 → 是 `valid=false`。源头是 `HandLandmarkStreamer`：手一丢失追踪就 `Observe("手部", false)`，绕过了 `ObserveSide` 的 0.5 s 交接宽限。改为**只在有效时上报**，失效统一交给 `ObserveSide`。

## 最终实测（第五轮修复后）

| 源 | 帧数 | 时间范围 | 间隔中位 | >100 ms | 最大间隔 |
|---|---|---|---|---|---|
| Left / Right wrist | 621 / 621 | 0.0 – 7.4 s | 11 ms | 0 | 23 ms |
| Left controller | 1508 | 7.4 – 24.2 s | 11 ms | 0 | 35 ms |
| Right controller | 1488 | 7.7 – 24.2 s | 11 ms | 0 | 16 ms |
| Head / Body hips | 2175 / 2175 | 0.0 – 24.2 s | 11 ms | 0 | 36 ms |

手在 7.4 s 停止、手柄在 7.4 s 接上，**中间一帧不丢**；全程 90 Hz；`[XRPAUSE]` 没有任何「等待有效追踪」。

## 手部射线：起点、方向与颜色对齐 Quest

真机反馈两轮：

1. 第一轮："射线虽然在拇指和食指之间，但上下动食指/拇指射线不跟手，只跟着手腕走。"
   → 起点当时是拇指+食指中点，**方向**取的是 pointer pose（手腕/掌根朝向），所以手指动作不改变方向。
2. 改成"方向取食指 metacarpal→tip"后："现在像是直接从食指出来，只动食指射线就跑，点不准；Quest 的手射线像是从掌心出来，平时白色，捏合点击时变蓝色。"

最终实现（`WorkspacePointer.ProcessHand` / `Aim`）：

| 项 | 实现 |
|---|---|
| 起点 | `HandJointId.HandPalm`（掌心关节）；与 pointer pose 距离超过 9 cm 时退回 pointer pose |
| 方向 | `pointer pose` 的前向（手/掌的朝向），**不用**单根手指 |
| 颜色 | 平时白色；食指+拇指捏合时变蓝（与光标点同一套提示）；手柄射线同样白→按扳机变蓝 |

## 工具

新增 `Tools/morla_pose_compare.py`（`--port 8000`）：多连接 TCP，按源统计 Hz / 帧间隔 / **位姿实际变化 Hz** / **每连接序号缺口**，阶段切分（回车）与方向汇总（同一动作的位移与欧拉角符号对比），CSV 带时间戳。`变化Hz` 这一列是用来区分"没发"和"发了但数据没变"的关键。

## 验证

- Roslyn 静态编译（editor + android 两套 define）：**0 错误**。
- **未在 Unity 真实编译、未重建 APK、未真机复测**。
- 复测要看：手柄阶段左右两侧是否都接近 90 Hz；手在视野里拿手柄时是否仍然判给手柄；把手柄放桌上是否回落到手；是否还会出现"等待左侧信息停止发送"。
- 方向对照还没做完：这次是一次会话跑完两段，没有逐动作分段。下次建议每个动作（前后 / 左右 / pitch / yaw / roll）各自按一次回车切段，脚本会逐段给出位移与相对转角，直接看手和手柄符号是否一致。
