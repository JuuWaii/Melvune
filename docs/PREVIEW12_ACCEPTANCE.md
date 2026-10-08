# preview.12 播放器布局交付记录

布局阶段提交 `b72d8d0`，固定包 `dist/FnMusic-0.2.0-preview.12-win-x64-verified/`，程序版本 `0.2.0-preview.12+b72d8d0d9f38ac3c0669ff3018f6235a2f9600a2`。包不覆盖 preview.11。

- 参考 `img/ui示意图.png` 的区域分布处理 `img/9.png` 五项反馈，保留 WinUI 黑白和系统主题：左侧歌曲信息、中间圆形播放按钮与进度、右侧设备/喇叭/音量/队列；状态说明放在详情弹层，播放错误继续主动提示。底部约 101 像素，时间位于进度两端，侧栏内容增加 20 像素滚动条间距。
- Debug/Release 构建零警告零错误；完整原生窗口 620/620，包含 1360/980/820 宽度、黑白主题、主页面/歌词共用控制、队列、动画、状态详情内容与侧栏边距。报告 `.review-audit/player-balanced-verified-app/collection-window-verification.json`，时间 `2026-10-08T14:48:48.9482622+08:00`，stage=complete、error 为空。这是隔离程序内检查。
- 实际 Release 鼠标操作确认 273 首缓存歌曲、静音播放推进至 43 秒、封面进入/返回歌词、右侧队列与 Escape、完整状态详情；停止并恢复原静音状态后正常关闭，八个配置/会话文件哈希保持，见 `.review-audit/player-balanced-runtime.json`。
- 锁定依赖还原与 Release 发布成功；551 个清单文件加清单本身共 552，发布后和实际运行后完整哈希检查通过，见 `.review-audit/preview12-integrity-before.log`、`preview12-integrity-after.log`。FFmpegInteropX SHA-256 为 `3DBE6C7083A78C8129BD994C5499385098645235A5E0B23B5F0E6E7C1585B803`。
- 独立包进程 PATH 仅包含 System32，实际恢复 273 首本地曲目并静音播放至 6 秒；七个播放模块均来自包内。停止、恢复原静音状态、正常关闭，八个现有配置/会话文件哈希保持。报告 `.review-audit/preview12-runtime.json` 时间 `2026-10-08T14:54:24.0912085+08:00`，unchanged、normalClose、allModulesPackaged 均为 true。
- 隐私检查 8/8，提交前源码扫描 243 文件通过。此次仅修改应用布局，上一阶段启动索引的核心 121/121 与原生缓存 12 项结果见 [索引交付](PREVIEW11_ACCEPTANCE.md)，未把历史检查描述成本次重跑。

双击包内 `Start-FnMusic.cmd` 启动；保留完整目录。继续验证更多 DPI、系统通知、真实格式/网络和长期播放矩阵。本轮未输入飞牛账户、未修改服务端设置，尚未宣称全部重点需求或正式分发完成。
