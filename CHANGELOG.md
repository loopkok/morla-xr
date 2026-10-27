# Changelog（morla-xr）

日期使用Asia/Shanghai。历史版本说明继续保留于README；源码版本、APK产物和设备验收分别记录。

## 2026-10-27 — 工作区1.1源码

- 透视工作区、统一工具栏、六页设置、手/柄指针、窗口拖动与滚动；旧菜单默认隐藏，关闭重复启动图。
- 类型化配置、旧偏好迁移、端点历史、草稿/生效分离、损坏备份；追踪、位姿发送、视频和麦克风会话独立管理。
- 每源后台网络通道、最新样本优先、过期丢弃、UI输入隔离与释放/新样本门、前后台和必要数据流watchdog；原位姿/语音封包契约继续复用。
- 视频实际track选择、缩略/隐藏/世界固定与冻结提示；MORLA HTTP状态与operation_id完成回执核对，不把accepted当完成，不自动重试控制。
- 收录中文字体及随附授权、Unity Editor资源准备/Android构建菜单和.NET8便携验证工具。
- 源码版本1.1.0/versionCode8；旧MORLA-XR.apk未修改。Windows Unity/Meta真实编译、APK重建及Quest真机验收尚未完成。
- 本次127条便携断言通过；147个.meta/唯一GUID检查通过，新资源元数据完整，AppManager GUID保留。详见[详细修改与验证日志](docs/CHANGELOG_2026-10-27.md)。
