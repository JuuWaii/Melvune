# Melvune NAS迁移与改名验收

本文件是2026-10-16迁移阶段的历史记录；其中兼容方案已由2026-10-17独立新身份方案替代。当前交付以 [rc.8回执](RC8_DELIVERY.md) 为准。
2026-10-16。本轮优先迁移本地开发工作副本并将应用更名为Melvune（合听），没有修改GitHub远端、推送、打标签或发布新包。NAS主仓库路径与本机回退路径只保存在忽略的迁移收据中；旧目录增加迁移提示，后续开发使用NAS。

迁移前的400个现存跟踪文件逐项摘要相同，原有4项bug/logo删除保持，10份img资料一致。NAS Git完整性检查通过，迁移时全部分支/标签引用与原副本一致。rc.6/rc.7的ZIP和便携EXE与本机原件SHA-256相同；另保留55份关键验收报告/日志。原本机副本保留，约103GB重复验证构建不再搬运。

应用标题、托盘、关闭提示、桌面歌词、新EXE和程序产品元数据改为Melvune，解决方案为native/Melvune.sln。新构建使用独立bin/Melvune目录；原源码命名空间、配置目录、凭据键、来源身份和信息协议保持兼容。便携程序支持沿用原FnMusic-portable/Data；包校验器同时识别历史FnMusic和新Melvune产品标识，拒绝产品标识不一致的组合。

NAS路径Debug构建以及独立信息客户端、便携启动器构建均零警告、零错误。核心120项、Navidrome/Subsonic及Emby协议/连接专项、Node24、发布完整性9、便携验证16项通过；隐私阻断0。窗口管理67、关闭选择37、桌面集成165及综合界面663项通过。用户手动确认NAS新名称EXE可以打开；普通配置保持原兼容路径，不自动迁移或合并个人资料。

检查中修复两处旧验证代码的并发计数，改用Interlocked，防止并发扫描偶发少计。原生构建脚本对映射盘符取得真实UNC Git路径，仅对子进程配置特定safe.directory，不修改全局信任。默认合成测试夹具放在本机临时目录，源码和构建输出保留在NAS。

第一次综合窗口验收在后台建库离开页面后出现InvalidOperationException；加上仅Debug的异常堆栈诊断后，同一663项套件完整复跑通过，未复现，未据此改动后台建库生产逻辑。保留首次摘要及隔离配置，后续若复现按具体堆栈追踪。独立动画检查发现诊断文件写盘打断短动画采样，已将写盘移到采样与关闭之后，并采用真实渲染帧采样，未修改生产动画时长。

独立动画22项复核通过，窗口合计954项通过。最终Debug及独立Release构建均零警告、零错误；独立信息客户端与便携启动器产品名均为Melvune。源码检查点为 `1e73718`，开发Release的完整版本为 `0.2.0-rc.7+1e737187b432debeb57dea7aa68ab92129f0b7ea`，已核对完整40位来源一致。入口为 `native/src/FnMusic.App/bin/Melvune/Release/net10.0-windows10.0.26100.0/win-x64/Melvune.App.exe`，同目录依赖不可拆散；可用 `scripts/native.ps1 run` 打开。

检查证据位于 `.review-audit/melvune-migration-20261016/`：verification-summary.json、migration-verification.json、retained-artifacts.json、retained-reports.json、release-build.json及各套件报告。最终验收收据提交只更新文档，不改变源码检查点。本轮不是新版本发布，既有rc.7包内容和名称不重写。

NAS中77项旧preview/rc.1–rc.5生成产物已获用户明确清理授权，但执行环境仍以“策略阻止”拒绝，尚未删除。精确清单和人工执行脚本放在私有迁移证据目录中；自动执行没有改用其他工具绕过。源码、Git历史、rc.6/rc.7及本机原件不在清理范围。

GitHub后续操作见 [改名方案](GITHUB_RENAME_PLAN.md)。更多硬件、听音、签名和正式新品牌发布不属于本轮验收。

2026-10-17更新：GitHub仓库已改名为Melvune，NAS origin与公开源码同步工作见 [GitHub改名验收](GITHUB_RENAME_ACCEPTANCE.md)。本文保留迁移阶段的构建与交付记录。
