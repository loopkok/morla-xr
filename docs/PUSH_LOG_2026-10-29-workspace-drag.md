# morla-xr 推送日志：2026-10-29 工作区 3D 拖动

核对时间：2026-10-29T16:1x+08:00（Asia/Shanghai）。本日志记录工作区 3D 拖动这一组提交的推送；日志自身由后续提交保存，其 SHA 以 Git 历史为准。

## 本仓库实际回执

- 远端：`git@github.com:loopkok/morla-xr.git`（origin，SSH）。
- 目标分支：`main`，正常推送，没有 force，没有改远端配置。
- 推送前远端 main：`4a9a8201d392b38a9c6c493f8dd309410c011a04`。
- 推送后 main：`bea62c6a103fdaaaf6e0a483ead3c6d57e4ccc0e`。
- Git 返回退出码 0：`4a9a820..bea62c6  main -> main`。
- 远端只给出体积告警，没有拒绝：`File MORLA-XR.apk is 97.12 MB / 97.11 MB; this is larger than GitHub's recommended maximum file size of 50.00 MB`，以及 GH001（建议 LFS）。本区间含**两个** APK blob（`45eb42c` 与 `963ae72`），各约 101.8 MB。
- 第一次推送失败并已记录：`FATAL: recv() failed, 10054` / `send-pack: unexpected disconnect while reading sideband packet`，退出码 128，远端未变（仍是 `4a9a820`，没有半截状态）。带 SSH keepalive 重试后成功：

```bash
GIT_SSH_COMMAND='ssh -o ServerAliveInterval=20 -o ServerAliveCountMax=6 -o IPQoS=throughput -o Compression=no' \
  git push origin main
```

- 推送后 `git ls-remote origin refs/heads/main` = `bea62c6a…`，与本地 HEAD 一致。

## 本轮提交

| SHA | 主题 | 范围 |
|---|---|---|
| `db30744` | feat(workspace): 工作区可 3D 抓取拖动，远近等视角缩放 | `WorkspaceWindowDrag`、`WorkspacePointer`（`_dragRays` / `TryGetDragRay`）、`WorkspaceUIController`（`ScaleForDistance` / `ApplyDistanceScale`） |
| `d2dd691` | docs: 记录工作区 3D 拖动与不变量 13 | `CHANGELOG.md`、`CLAUDE.md`、`docs/CHANGELOG_2026-10-29-workspace-depth-drag.md` |
| `45eb42c` | feat(workspace): 手柄按住侧键/长按 ☰ 直接搬运整块工作区 | 新增 `WorkspaceGrab.cs`(+`.meta`)；`AppManager` 移除 `Button.Start` 处理并挂组件 |
| `624a286` | fix(workspace): 修正 3D 拖动的倾斜、卡顿、光标不跟手与"看不出缩放" | 第一轮真机反馈的四处修正；新增 `AppSettings.angularWindowScale` |
| `9135855` | fix(workspace): 远近方向与 Quest 对齐、深度加速、窗口转向用户、列表滚动调平 | 第二轮真机反馈的五处修正；`UiKit.ScrollPage` 关闭 `ScrollRect` 惯性 |
| `dad9631` | docs: 补齐工作区 3D 拖动的提交清单与构建对应关系 | `docs/CHANGELOG_2026-10-29-workspace-depth-drag.md` |
| `963ae72` | chore(apk): 更新 MORLA-XR.apk（含 3D 拖动方向/倾斜/深度改动） | 构建产物 101,828,190 → 101,836,358 字节 |
| `bea62c6` | docs(readme): 补充工作区 3D 搬运操作说明 | `README.md`「工作区操作」 |

源码范围：`AppManager`、`AppSettings`、`UiKit`、`WorkspaceGrab`(新)、`WorkspacePointer`、`WorkspaceUIController`、`WorkspaceWindowDrag`。

## APK 与源码的对应关系（必须如实记录）

- 仓库里的 `MORLA-XR.apk` 是 2026-10-29T15:24 的本机构建（101,836,358 字节，`com.loopkok.morlaxr` / `versionName 1.1.0` / `versionCode 8` / minSdk 32 / targetSdk 33，`aapt dump badging` 核对）。
- 用 `global-metadata.dat` 核对：包内有 `WorkspaceGrab`（3 处）、`FaceHead`（1 处）、"默认等视角"、"拖远后的大小"、"等视角（远看变大）"、"自然（远看变小）"、"长按 ☰"；`FaceUser` 为 **0**。说明它是第二轮（`9135855` 之前）源码的构建，不是旧的 1.1.0 包。
- 但它**不含**此后才改的 `scroll.inertia = false`（以及同一批里删掉的两行只读属性赋值）。**要与当前源码完全一致，需在 Unity 里重新 Build。**
- 该包**未安装到 Quest 3 验收**。

## 交付限制（不要据推送成功宣称完成）

- 本轮全部改动只做过 Roslyn 静态编译（Unity 自带 csc + editor/android 两套 define，**0 错误**），**没有在 Unity Editor 真实编译**，也没有在设备上验收这套最终源码。
- 手柄/手部拖动、等视角缩放、`FaceHead` 朝向、`DepthGain = 2`、列表关惯性后的手感，全部只有静态编译支撑，属于**待真机确认**。
- `Tools/` 下的探针产物、ASR 模型缓存不进仓库（`.gitignore` 既有规则）。

## 网络与认证

- 使用本机既有 SSH 密钥直连 `github.com:22`；未执行任何 `git config` 改写；仓库与日志中不含密钥、令牌或密码。
- 复验命令：

```bash
git ls-remote origin refs/heads/main
git rev-parse HEAD origin/main
git status --short --branch
```
