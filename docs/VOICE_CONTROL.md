# Quest 语音控制（配合 MORLA 主机）

使用 Unity **6000.0.65f1** 在 Windows 编译 Android/ARM64 APK。1.1 工作区改版保留原有追踪/语音载荷及默认 8000 / 8765 端口，`AppManager` 初始化工作区与 `VoiceAudioStreamer`，不需要手工给场景挂组件。

1.1 版本在工具栏和“设置 → 语音”提供麦克风上行，默认关闭；首次开启申请 Android `RECORD_AUDIO`。语音会话独立于位姿发送，可在主机可用时单独开启。进入设置交互、关闭麦克风、断开主机、切后台或失焦时立即结束录音、关闭连接并丢弃待发音频；关闭设置不自动恢复录音。工具栏中的录音指示来自实际采集状态。

原有 `SavedVoiceMic` 偏好迁移到版本化配置；语音地址默认跟随位姿主机，可在连接设置中单独配置。HUD 多行诊断已改为设置内的语音状态与最近结果，便携检查和 Windows 构建步骤见 [工作区实现说明](WORKSPACE_IMPLEMENTATION.md)。

Ubuntu 主机先启动机器人 Profile 和新的语音输入节点：

```bash
source /opt/ros/humble/setup.bash
source /home/loopkok/MORLA/install/setup.bash
adb reverse tcp:8001 tcp:8001
ros2 launch morla_operator_control operator_control.launch.py \
  start_router:=false voice_source:=quest
```

`robot.launch.py` 默认随 Profile 启动统一命令路由器，故这里使用 `start_router:=false`。旧入口没有路由器时改为 true。`voice_source:=both` 同时开启 Ubuntu 麦克风和 Quest 麦克风；两端听到同一条指令会去重。主机模型安装和完整流程见 MORLA 的 `docs/operator_control.md`。

说完整的 **“机器人开始跟随”**，状态确认后再说 **“机器人开始采集”**；结束说 **“机器人停止采集”**。另支持暂停跟随、暂停采集、继续采集、丢弃本段、确认丢弃、取消操作、交还控制。语音不提供自动使能、工作位或 HOME。丢弃须五秒内二次确认。首次使能和工作位仍在 Web 完成。

头显 HUD 显示 Voice 网络状态及主机执行结果，并发出短提示音。`submitted` 只表示接收请求，不播放成功音；主机确认遥操已开始／实际采集保存完成后才反馈成功。中文反馈文字需要项目的 TMP 字体包含中文字符；网络状态本身为英文，提示音不依赖字体。

传输协议：同一主机地址的 TCP **8001**，独立于位姿连接；有线默认使用 `adb reverse`。如使用无线，主机语音配置的 `quest_bind` 须改为可接入地址（默认仅监听 127.0.0.1）。麦克风为 16 kHz、单声道、signed PCM16 little-endian，每帧 640 样本（40 ms），Base64 后发送：

```text
@q3m <session> <sequence> <capture_monotonic_us>|VoicePCM:16000:<base64>
```

复用 `QuestPacketEnvelope` 的单调时钟握手；`WrapCaptured` 使用实际音频采集时间，不能把排队音频标记为刚采集。发送队列最多四帧，超过 200 ms 的帧丢弃；主机也检查顺序、时钟及 500 ms 时效，断连后重置识别器。原始音频只用于实时识别，不在采集数据中落盘。

主机回传单行 UTF-8 JSON（与时钟握手共用该连接）：

```json
{"type":"feedback","phase":"confirmed","message":"遥操已开始"}
```

Windows 验收：允许麦克风后 HUD 从 connecting 进入 microphone uplink connected；主机 `/morla/operator/sources` 应显示 voice_quest/listening。先在主机使用 `dry_run:=true` 检查 `/morla/operator/transcripts`，确认中文识别后再关闭 dry_run。Ubuntu 当前没有 Unity Editor，因此未宣称已构建 APK 或完成 Quest 真机麦克风验收。
