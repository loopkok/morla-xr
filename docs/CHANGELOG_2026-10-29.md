# 2026-10-29 — 真机探针 + 「暂停发送」点不动修复

两份独立工作：一个是只加在 `Tools/` 的主机侧验收工具（不改 App 行为），一个是 `Assets/Scripts` 里的真实缺陷修复（需重新构建 APK 才生效）。

## 一、主机侧实时探针 `Tools/morla_live_probe.py`

### 动机

用户需要"在这台主机上开接收脚本、开 ADB 反向转发、监听语音与各类数据并可视化，判断现在能不能正常发信息"。仓库原有的 `skeleton_viewer.py` 无法用于 1.1：

- 只监听 8000，`listen(1)` 单连接；而 1.1 是**每个源一条连接**（left / right / controller / head / body 共 5 条位姿 + 1 条语音）。
- `parse_line` 按原始前缀比较（`'Left wrist'`、`'Left landmarks'`、`'body iobt'`），不认 `@q3m <session> <sequence> <monotonic_us>|` 外层，所以 1.1 的每一行都解析不出来。
- 完全没有 8001 语音通路。

### 做法

新脚本 `Tools/morla_live_probe.py`：

- 8000 与 8001 各起一个**多连接** TCP 服务（每连接一线程），共享一把锁的状态缓存。
- 剥 `@q3m` 外层；标签容忍协议调试头 `Left wrist | f = 123 | t = 456:`；按 `Left/Right wrist`、`Left/Right landmarks`（63 个 float）、`Left/Right controller`、`Head pose`、`body iobt` 分类。
- 每个标签统计：累计帧数、**1 秒窗口实测 Hz**、**序号连续性（丢帧数）**、包内 `monotonic_us` 差值的**中位帧间隔**。
- 语音 8001：解 `VoicePCM:16000:<base64>` → PCM16LE，统计每秒帧数 / 峰值 / RMS / 有声样本占比，并把整段滚存成 `Tools/voice_capture.wav`（16 kHz 单声道 16-bit，最多 10 分钟），每秒落盘一次。
- 主动发 `@q3sync` 挑战，统计应答数与 RTT，用来验证上行/下行双向。
- matplotlib GUI：3D 骨架（IOBT 时以 hips 为原点，因为 App 除 hips 外都已转成躯干系）+ 手 21 点 + 头 + 手柄 + 语音波形；`--no-gui` 只看控制台，`--seconds N` 定时退出，`--host 0.0.0.0` 供无线直连。

配套 `Tools/transcribe_voice.ps1`：用 Windows 自带 SAPI（本机装有 `zh-CN / MS-2052-80-DESK`）把 `voice_capture.wav` 转成文字，**只用于验收"语音链路里有没有人声"**，不是产品级转写。

### 验证

- 合成数据自测（8100/8101 假端口）：5 类标签各 83 Hz、序号连续、帧间隔中位 12.00 ms、语音 25 帧/s、RMS -15.3 dBFS、`@q3sync` 应答 1 次 —— 解析/统计/WAV 全通。
- 反向转发实测：在头显里 `nc 127.0.0.1 8000` 发一行 `@q3m ADBTEST 1 1000000|Head pose:...`，探针正确计入；同时探针发出的 `@q3sync` 挑战被设备端收到 → `adb reverse` 双向可用。
- SAPI 转写自测：Huihui 合成"请开始发送位姿数据 现在测试语音识别"→ 识别为"请开始发送为此数据现在可是语音识别"（置信度 34%），链路可用。

## 二、真机实测结论（装机器上的是 1.1.0 / versionCode 8）

设置：`protocol=1`、`host=127.0.0.1`、`posePort=8000`、`voiceEnabled=true`、`videoEnabled=false`、`protocolDebug=true`、`trackingMode=2`（FullBody/IOBT）、`autoConnect=false`。

位姿链路**正常**（23:57:57–23:58:37 连续 40 s）：

| 源 | 实测 | 序号 | 帧间隔中位 |
|---|---|---|---|
| Head pose | 89–90 Hz | 连续，丢 0 | 11.18–11.21 ms |
| body（IOBT） | 89–91 Hz | 连续，丢 0 | 11.16–11.23 ms |
| Left/Right wrist | 82–85 Hz | 连续，丢 0 | 11.23–11.31 ms |
| Left/Right landmarks | 82–85 Hz | 连续，丢 0 | 11.23–11.35 ms |

5 条位姿连接 + 1 条语音连接同时在线，`@q3sync` 应答 RTT **1.0–1.2 ms**。90 Hz 档位的健康值就是 11.1 ms，实测吻合；手部 ~84 Hz 是 `HandLandmarkStreamer` 的 100 Hz 采样阈值 × 90 Hz 帧率共同结果。

语音第一轮：帧率**精确 25 帧/s**（40 ms/帧），但多数整秒的峰值是 0.00（数字静音），只有零星几秒出现峰值 0.06–0.10。第二轮（用户对麦克风说话，探针新增 WAV 落盘）逐秒电平：27–51 s 连续语音，**峰值 -8 ~ -25 dBFS、RMS ≈ -25 dBFS**，帧率 25–27 帧/s —— 麦克风、PCM 编码、8001 上行、反向转发全部正常。第一轮的"静音"只是当时没说话。

**本项目不含任何语音识别代码**（全仓库搜不到 whisper / vosk / STT / 语音识别），本机也没有下游 `astral_ws` / `quest3_hand_mocap`，"语音转文字"没有任何程序在执行——这是"没转成文字"的唯一原因，不是链路丢数据。

### 验收用的转写工具

- `Tools/transcribe_voice.ps1`：Windows 自带 SAPI（本机 `zh-CN / MS-2052-80-DESK`）。能证明"有人声"，但桌面听写模型精度太差（同一段只出"沈卓有走路我准备日子而是说"，置信度 5%），只作无依赖兜底。
- `Tools/transcribe_voice.py` + `faster-whisper` **small** 模型（int8 / CPU）：模型从 ModelScope 拉（HuggingFace 与 hf-mirror 在本机均不可达，PyPI/ModelScope 可达；`--download` 自动取），缓存在 `~/.cache/morla-asr/`，不进仓库。
- 本机 numpy/onnxruntime 各带一份 OpenMP，导入 ctranslate2 前必须设 `KMP_DUPLICATE_LIB_OK=TRUE`（脚本内已设）。

同一段 55 s 录音的实际转写（语言判定 zh，p=1.00）：

```text
[ 27.60- 28.60] 你好
[ 29.88- 31.76] 我现在要走了
[ 32.89- 34.49] 我现在要走了
[ 34.49- 37.01] 我准备离开实验室了
[ 37.45- 38.73] 嗯 再见
[ 40.14- 41.14] 不发送
[ 41.86- 42.86] 不发送
[ 46.44- 48.60] 你好 你好
[ 49.46- 51.22] 喂 嘿嘿
```

结论：语音链路端到端可用，收到的是可辨识的人声；缺的只是下游的 ASR 服务。

## 三、「暂停发送」点了不暂停

### 症状

用户反馈：在 Quest 里点「暂停发送」一直暂停不了。探针侧证据：23:58:37–23:58:56 这 19 秒里，6 条连接（5 位姿 + 1 语音）被**成对地断开又立刻重连，重复 15 次**，每次约 1.3 s，期间序号一直连增（3509 → 4661），说明不是崩溃而是"停一下又马上开始"。

### 根因

`WorkspacePointer` 在**按下瞬间**调用 `SessionCoordinator.EnterUiInteraction()` → `Pause()`（不变量 8：UI 交互先暂停）。工具栏发送按钮的 `onClick` 在**松开时**才执行，而它的判断是：

```csharp
if (App.Session.Requested) App.Session.Pause(...); else { HideSettings(); App.Session.RequestStart(); }
```

按下已经把 `Requested` 清成 false，于是松开时的点击必然走 `RequestStart()` 分支 —— 一次点击 = 先暂停再恢复，按钮看上去"永远暂停不了"，同时把整套连接重开一遍（就是上面那 15 次重连）。

### 修法

把"会话控制按钮"从"按下即暂停"的规则里显式排除：

- `UiKit.cs` 新增标记组件 `SessionControl`，`UiKit.Button(..., sessionControl: true)` 时挂上。
- 工具栏发送按钮挂该标记（`WorkspaceUIController.cs`）。
- `WorkspacePointer.Process` 只在命中对象**不是** `SessionControl` 时才 `EnterUiInteraction()`；滚动仍按旧规则。

这样 `Requested` 在点击时保持原值，暂停/恢复各按各的分支走。其它 UI（设置、视频、麦克风）仍会先暂停发送。

### 状态

Roslyn 静态编译 0 错误（editor + Android player 两套 define）。写这段时**未在 Unity 真实编译、未重建 APK、未真机复测**；装机器上的 1.1.0 仍带该缺陷，要复测必须重新构建。

### 后续

同日稍晚已构建出新的 APK 并提交：`1.1.0 / versionCode 8`、101,813,202 字节，包内 `global-metadata.dat` 检到 `MORLA.XR.SessionControl`，说明本节的修复已进入产物（详见 [CHANGELOG.md](../CHANGELOG.md) 同日条目）。**仍未在 Quest 3 上装这个包复测**，所以"暂停发送现在能暂停了"依旧只是源码推断，不是验收结论。
