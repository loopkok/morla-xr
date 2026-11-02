# morla-xr 推送日志：2026-11-02 手柄频率与手/手柄切换修复

核对时间：2026-11-02T15:5x+08:00（Asia/Shanghai）。本日志记录"手柄 20 Hz / 拿起手柄必现暂停 / 手部射线"这一组提交的推送；日志自身由后续提交保存，其 SHA 以 Git 历史为准。

## 本仓库实际回执

- 远端：`git@github.com:loopkok/morla-xr.git`（origin，SSH）。
- 目标分支：`main`，正常推送，没有 force，没有改远端配置。
- 推送前远端 main：`90a46e316a0bb9afb93f7fcc62d708dd5121cc5a`。
- 推送后 main：`5fa384717f142f7102d64eaa8b1bc2bf59dc36d0`。
- Git 返回退出码 0：`90a46e3..5fa3847  main -> main`；远端只有体积告警（`MORLA-XR.apk is 97.12 MB`、GH001 建议 LFS），没有拒绝。
- **第一次推送失败并已记录**：`FATAL: Unable to connect to relay host, errno=10061` —— `~/.ssh/config` 里 `github.com` 走 Clash 的 `127.0.0.1:7897` SOCKS 跳板，当时 Clash 没运行。改用直连重试成功，**没有修改 ssh 配置**：

```bash
GIT_SSH_COMMAND='ssh -o ProxyCommand=none -o ConnectTimeout=15 \
  -o ServerAliveInterval=20 -o ServerAliveCountMax=6 -o IPQoS=throughput' \
  git push origin main
```

（同一条命令如果 Clash 开着，去掉 `ProxyCommand=none` 即可走原来的跳板。）

## 本轮提交

| SHA | 主题 | 范围 |
|---|---|---|
| `1f4beb9` | fix(tracking): 手柄 90 Hz、手/手柄无缝切换、掉源不再误暂停 | `ControllerPoseStreamer`（整帧一条消息）、`TrackingCoordinator`（只看连接 + 双宽限 + 诊断）、`HandLandmarkStreamer`（新鲜度、只在有效时上报）、`SessionCoordinator`（`[XRPAUSE]`） |
| `c49396a` | fix(ui): 手部射线从掌心出发、白/蓝配色，一侧只画一根射线 | `WorkspacePointer` |
| `6e2ffb9` | feat(tools): 手/手柄位姿对比探针 | `Tools/morla_pose_compare.py`、`.gitignore` |
| `a2c20ee` | docs: 手柄频率/切换暂停的五轮真机排查与手部射线规则 | `docs/CHANGELOG_2026-11-02-controller-rate.md`、`CLAUDE.md`、`CHANGELOG.md` |
| `5fa3847` | chore(apk): 更新 MORLA-XR.apk（本轮全部修复的构建） | 101,833,178 → 101,842,462 字节 |

## 五个根因（按发现顺序，前两个是判断错误）

1. `ControllerActive` 非 Mixed 分支要求 `!HandTracked` —— 手一被追踪就把这一侧判给手。
2. 同一判据要求 `GetControllerPositionValid` —— 该值短暂失效时当帧不发。
3. **两侧共用一个 `TelemetryChannel`，每帧提交 4 条消息而通道只保留最新一条** —— 同帧后面的把前面的挤掉，这才是右手柄 9–20 Hz 的真凶。
4. 两处"一帧抖动就判源失效"（`ObserveSide` 的 `valid=false`、`HandLandmarkStreamer` 的 `Observe(false)`）——暂停后永不自动恢复。
5. **运行时用手部位姿合成手柄位姿**：手柄放桌上、人转身时 `GetControllerPositionValid` 仍为真、位姿与手腕重合（dist=0.000），所以"位置有效"与"手腕距离"都不能判在手，只有 `GetConnectedControllers()` 能。

## 真机数据（最终构建）

| 源 | 帧数 | 时间范围 | 间隔中位 | >100 ms | 最大间隔 |
|---|---|---|---|---|---|
| Left / Right wrist | 621 / 621 | 0.0 – 7.4 s | 11 ms | 0 | 23 ms |
| Left controller | 1508 | 7.4 – 24.2 s | 11 ms | 0 | 35 ms |
| Right controller | 1488 | 7.7 – 24.2 s | 11 ms | 0 | 16 ms |
| Head / Body hips | 2175 / 2175 | 0.0 – 24.2 s | 11 ms | 0 | 36 ms |

切换处一帧不丢，全程 90 Hz，`[XRPAUSE]` 无任何「等待有效追踪」。

## 排查工具

`Tools/morla_pose_compare.py`（监听 8000/TCP，配合 `adb reverse tcp:8000 tcp:8000`）：按源统计 Hz、帧间隔、**位姿实际变化 Hz**（区分"没发"与"发了但数据没变"）、**每连接序号缺口**（区分 App 限流与网络丢包），CSV 边收边写。App 侧对应 `[XRDIAG]`（每秒一条）与 `[XRPAUSE]`（每次暂停一条）日志：

```bash
adb logcat -s Unity:V | grep -E "XRDIAG|XRPAUSE"
```

两条日志**只写 logcat，不进入任何位姿/语音 socket**，对下游接收没有影响（90 Hz 的数据就是在它们开启时测的），故保留在代码里。

## APK 与源码

- 仓库 `MORLA-XR.apk` 为 2026-11-02T15:41 的本机构建（101,842,462 字节，`com.loopkok.morlaxr` / 1.1.0 / versionCode 8），晚于全部源码改动（最后一次源码修改 15:38）。
- **未在设备上验收手部射线的新起点/配色**：15:41 的包已装入设备，但掌心起点与白/蓝配色的观感尚未反馈。

## 网络与认证

- 使用本机既有 SSH 密钥直连 `github.com:22`；未执行任何 `git config` 改写；仓库与日志中不含密钥、令牌或密码。
- 复验命令：

```bash
git ls-remote origin refs/heads/main
git rev-parse HEAD origin/main
git status --short --branch
```
