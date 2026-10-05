# preview.9 构建与验收

2026-10-05。可用于本机验证的固定目录为 `dist/FnMusic-0.2.0-preview.9-win-x64-verified/`，源码提交 `c3fff2f127b8c0dbd67de947188af372fac565ed`，ProductVersion 为 `0.2.0-preview.9+c3fff2f127b8c0dbd67de947188af372fac565ed`。保留整个目录，双击 `Start-FnMusic.cmd` 启动。验证指南见 [PREVIEW_VALIDATION.md](PREVIEW_VALIDATION.md)。

初始 preview.9 目录保留作构建记录，源码为 `85fbcba00bac869bc887fb6d364a0f77639ec5b9`，不含随后修复的模态齿轮导航保护；本次验收和推荐运行对象是上面的 verified 目录。旧 preview.7/.8 固定包保留，各包的运行证据分别记录。

## 构建及完整性

- 锁定依赖 restore 和 Release publish 通过，Debug/Release 构建零警告零错误。最终窗口回归 575/575，报告 `modal-fixed-app/collection-window-verification.json` 时间 `2026-10-05T17:22:40.9260406+08:00`、stage=complete、error 为空；覆盖实际加载的原生控件、模态保护和此前功能。核心 Debug 107/Release 106、封面窗口专项 21 的报告及范围分别见 [封面接口可靠性](NATIVE_COVER_RELIABILITY.md)和[顶部导航](UI_TOP_NAVIGATION_2026_10_05.md)，不把不同范围合并成一次运行。
- 启动前和正常退出后，`check-preview.ps1` 核对 551 个清单文件加清单本身，共 552 个文件；全部哈希、额外文件、XAML、版本、解码 DLL 和补丁构建材料检查通过，运行没有改写包目录。
- 子进程仅保留 System32 PATH，窗口成功启动并响应；实际播放时 coreclr、Microsoft.UI.Xaml、FFmpegInteropX、avcodec、avformat、avutil、swresample 七个模块全部从包内加载，没有依赖仓库 SDK 或系统 FFmpeg CLI。
- 本次使用既有测试会话及已配置本地库。临时切换深色再恢复原浅色，静音播放后停止并恢复原未静音状态；正常关闭自建进程。配置目录运行前后均为 8 个文件，所有相对路径及 SHA-256 一致，没有增加或改写配置、会话文件。

## 当前包实际操作

Computer Use 逐次鼠标/键盘操作并观察刷新截图，共 39 项观察检查通过；这些是当前 verified 包的操作记录，不继承旧 preview.8 的 23 项报告。

- 标题栏品牌放大、返回和搜索在上方，侧栏重复品牌移除；点击两类歌单类别分别进入对应管理页。本地新建命名弹窗打开后，点击标题栏齿轮和返回均保持原管理页，取消后没有新建歌单。
- 飞牛卡片打开现有歌单并读取一首歌曲，顶部返回恢复所属管理页。从管理页在顶部输入中文并回车，进入所有音乐搜索，读到 15 首（飞牛 7、本地 8）；点击清空后恢复汇总 546 首（飞牛 273、本地 273）。这些是本次读取计数，不保证以后曲库不变。
- 喇叭图标实际静音，双击飞牛歌曲开始播放；缩略图、底部封面及点击封面进入的应用内歌词均加载，高亮随进度变化，进入时观察到向上覆盖。歌词播放器图标在同一行，队列按钮前没有此前妨碍操作的垂直滚动条。
- 最右队列图标实际打开右侧覆盖层，双击本地来源同名歌曲切到队列第 12/15 项，来源标记、封面和歌词更新。顶部返回先收起队列并保持歌词播放，再返回原搜索页并继续播放；清空查询也不重建当前队列。
- 齿轮打开统一设置；浅色/深色切换落定后，标题栏、侧栏、设置和播放器配色一致。恢复原浅色偏好。未通过修改 Windows 设置来验收系统主题通知。
- 音乐库管理显示本地和飞牛两个卡片及编辑/删除图标。本地编辑打开对应名称、分组和目录，取消后原库保持；新建默认本地字段，切换飞牛后显示 NAS 目录、元数据策略和歌词选项，当前普通账号不能保存；切换其他音乐显示后续支持说明并禁用保存。取消后仍为两个库，顶部返回恢复原外观设置分类。

扫描读取计数和取消入口实际可见，但扫描在点击取消前已完成，本次不计为取消成功；取消保护仍只有既有专项/夹具证据。全程静音，不声称实际听音对齐。歌单和音乐库弹窗均取消，没有向 NAS 写入或删除真实资源；未验证管理员和禁止访问账号的新界面切换、真实新菜单写入及完整登录输入。

本机未跟踪记录位于 `.review-audit/`：`preview9-verified-integrity-before.json`、`preview9-verified-integrity-after.json`、`preview9-verified-runtime-modules.json`、`preview9-verified-runtime-playback-modules.json`、`preview9-verified-input-verification.json`、`preview9-verified-profile-integrity.json` 与 `preview9-verified-process.json`。操作结果记录只保留固定检查、包身份和计数，不复制凭据、私人曲名或目录。

## 保留的交付边界

这是当前 UI 优化及基础可靠性的本机验证阶段，不代表全部重点或所有历史需求完成。真实 NAS 新菜单写入、不同权限界面、更多 DPI、Windows 系统主题通知、真实连续拖动/网络异常/听音、素材库重关联完成计数及正式安装升级与完整分发材料仍按 [开发清单](NATIVE_BACKLOG.md)跟踪；第三方源、桌面歌词和通用信息接口保留后续。目录内测试指南是构建时材料，当前包的实际启动和操作结果以本页为准。
