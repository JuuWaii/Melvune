# 0.2.0-rc.1 Windows x64 候选版交付

2026-10-09。D01–D12 已完成，唯一推荐产物：`dist/FnMusic-0.2.0-rc.1-win-x64.zip`（205,511,043 字节，约 196 MiB）。解压完整目录，双击 `Start-FnMusic.cmd`；也可使用同名完整运行目录。preview.14 保留，不覆盖。

| 身份 | 结果 |
| --- | --- |
| 应用版本 | `0.2.0-rc.1+ae0dad41468dbd20122158fdbe131d2cc2f48d6f` |
| 交付源码提交 | `ae0dad41468dbd20122158fdbe131d2cc2f48d6f`；开发收尾提交 `d0adeac` |
| ZIP SHA-256 | `5BDD6CE34D57E357ACE34F2EA4469E2716348B35FC637C0CF0EB3019FDEF4235` |
| 哈希文件 | `dist/FnMusic-0.2.0-rc.1-win-x64.SHA256SUMS.txt` |
| 文件清单 | 包内 SHA256.json，587 个清单条目加清单共 588 文件 |
| 解码器 SHA-256 | `3DBE6C7083A78C8129BD994C5499385098645235A5E0B23B5F0E6E7C1585B803` |
| 对应源码归档 SHA-256 | `5591348E33978107323B39FC50CA0B7868E8887814C4768A9F243FC6027CADD9` |

## 本轮开发与统一检查

后台任务按来源/Id/类型/CreatedAt/LibraryId 保持身份，进度变化保持选择；消失、身份复用或刷新/操作失败后清除可操作快照，保留原身份用于恢复，用户可以明确选择其他任务。首次读取不自动选择任务。启动收尾统一了独立配置目录参数，补空配置、损坏设置保留及显式恢复/失败重试回归。

Debug/Release 零警告零错误，核心 124/124、脚本 21/21、完整窗口 637/637、268 文件源码隐私扫描通过。窗口首轮新增断言通过后，测试继续重关联前未刷新失效的素材库快照；补显式刷新后仅重跑窗口，最终 stage=complete、error 为空。首轮失败报告保留，未修改保护逻辑或降低断言。之后的未选择任务提示文字调整由最终 Publish 编译与实际窗口冒烟覆盖，没有再跑全套。

本轮日志集中在 `.review-audit/rc1/`：restore.log、debug-build.log、release-build.log、core-tests.log、node-tests.log、privacy-final-development.log、debug-app/collection-window-verification.json、window-first-failure.json。最终 Publish 成功，未沿用历史包的通过数字。

## 最终 ZIP 实际运行验收

从本次 ZIP 解压到独立审计目录并校验所有文件，启动子进程 PATH 仅保留 System32。先使用空独立配置启动首页、浏览空本地来源并正常退出；再使用已有配置及 DPAPI 会话的隔离副本恢复，不输入新凭据、不操作认证页面、不使用提取凭据的探针。

- 恢复飞牛 273 / 本地 273，共 546 首；实际鼠标双击飞牛曲目，静音走时超过 51 秒并暂停。
- 飞牛大封面和同步歌词显示正常，打开/关闭右侧队列，返回原浏览页保持暂停位置。
- 筛选本地来源 273 首并双击播放，实际走时；鼠标拖动至约 1:30，继续走时到约 1:42；停止后归零。
- 音乐库管理、两类歌单管理和统一设置可进入；飞牛成员角色设置仅查看，后台任务读取成功为空，继续保留“消失不代表成功”提示，未执行 NAS 写入。
- 手动浅色主题生效，正常退出并重启后仍为浅色；默认正式配置 1376 个文件全部哈希保持，文件数量保持。
- 8 个已加载解码模块均来自本次解压包；全部自建验收进程正常退出。打包目录、ZIP 解压后与运行后的完整性通过，最终包未写入配置、索引、会话或报告。

证据：runtime-acceptance.json、runtime-modules.json、formal-config-integrity.json、package.log、dist-integrity.log、extracted-integrity-before.log、extracted-integrity-after.log、extracted-integrity-final.log。观察来自 computer-use 实际操作；这不替代听音、首次登录输入、实际 OS 主题通知或个人升级验证。

## 分发材料与边界

包内提供 USER_GUIDE.md、RC1_USER_VALIDATION.md、发布说明、BUILDING_SOURCES.md、LICENSE、THIRD_PARTY_NOTICES.md、NATIVE_DECODER_DEPENDENCIES.md、第三方许可和自包含运行时 NOTICE。对应解码源码归档包含 21069 个文件条目及 10 份保留原链接/模式的原始归档，ZIP CRC 检查通过；固定提交/静态依赖与桥接子模块相符，实际 FFmpeg 导出 n8.1.2、LGPL 2.1 或更高版本及禁用 GPL/version3 的 configure 参数，材料与实际 DLL 哈希匹配。提供源码、补丁、构建与动态库替换方式；不将 NuGet 元数据称为独立法律审查。

公开发布正文见 [发布说明](releases/0.2.0-rc.1.md)，一次用户反馈见 [集中验证单](RC1_USER_VALIDATION.md)。首次输入登录、系统主题通知、实际听音、个人升级与常用 DPI 尚待用户验证，单子未将其写成通过。MP3 远距离首次定位读取量、重关联缺完成计数保留未确认、第三方源/桌面歌词/桌面集成/对外接口/安装器/自动更新及全面扩展矩阵继续延期。本包未签名，支持范围见用户指南。

发布阻断项：无。本轮只完成本地提交与可交付/可发布材料，没有推送或上传 GitHub。

工作区收尾：自动审批拒绝了包含递归清理测试配置副本的命令，仅返回 blocked by policy。`.review-audit/rc1/runtime-restored-profile/` 仍含隔离测试副本（包括加密会话），位于交付包之外，需用户手动删除；未重新尝试被拒绝的清理。没有遗留验收进程，正式配置保持。
