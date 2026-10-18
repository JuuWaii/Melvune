# GitHub Actions 发布流程

当前候选为 `0.2.0-rc.9`，提供 Windows x64 ZIP、便携 EXE、安装 EXE 和 MSI，未签名。

日常开发仅按需要执行 `scripts/preview.ps1`，完成一批修改后集中检查。仅最终发布标签推送时自动完整打包，普通分支推送不触发；工作流手动入口仅在明确需要完整候选包时使用，不自动发布。

## 发布流程

1. 审查源码、更新说明、许可、AI 声明和隐私后，推送提交及对应新标签 `v0.2.0-rc.9`，不移动已发布标签。
2. Windows 构建作业检查标签/版本、当前与历史隐私，集中执行核心、来源、队列、便携、发布及预览清理回归。
3. 同一提交生成 ZIP、便携与安装器。验证完整载荷、CRC、摘要、便携尾部、版本/来源、MSI 全部文件、EXE 内嵌 MSI、安装用户范围、目录选择及三个 EXE 的七档图标。
4. 暂存并上传本次候选artifact后，在独立 Windows 安装作业下载同一份artifact、重验摘要与来源并展开ZIP。再在独立 Windows runner 实际执行 MSI 自选目录、EXE 默认/自选目录的静默安装和卸载，检查全部文件、快捷方式、安装目录记录及配置保留。
5. 候选artifact固定11个附件：四种入口、USER_GUIDE.md、RELEASE_NOTES.md、AI_DISCLOSURE.md、WIX_NOTICE.md、WIX_LICENSE.txt、release.json 和 SHA256SUMS.txt。
6. 独立发布作业下载本次 artifact，等待构建和安装作业均成功后，重新核对每个文件与远端标签提交。仅此作业拥有 contents:write，以临时令牌创建预发布草稿；上传后逐项比较远端大小、状态与 SHA-256 digest，完整一致后才公开，不设为稳定最新版。

SHA256SUMS 覆盖自身之外的全部10个附件，release.json 记载源码、工作流地址、MSI/WiX版本与内容摘要；GitHub 自动生成的 Source code ZIP/tar 不属于这11个附件。

## 失败与恢复

构建、安装或校验失败均阻止发布。上传失败保留草稿，同一来源与同一构建说明的草稿可重跑补齐缺少的文件；摘要不符、未知附件或已经公开的 Release 均拒绝修改。不覆盖附件，不删除历史 Release 或标签。草稿从认证列表查询，再按 Release ID 操作。

最终来源、状态、测试和未验证边界记录于当前开发清单。静默安装不替代图形安装、权限提示、SmartScreen、升级以及全部真实设备/服务验收。旧 rc.8 已公开附件保留原样。

发布与清理工具回归分别为 `python -m unittest discover -s tests -p test_release_publication.py` 和 `python -m unittest discover -s tests -p test_preview_cleanup.py`。预览清理规则见 [封装说明](WINDOWS_PACKAGING.md)，不会删除正式发行包。

安装验收与构建分为独立作业；同一运行的安装验收暂时失败时，可仅重跑失败作业，复用原artifact，无需重新构建应用。安装命令按 Windows Installer 的 PROPERTY="含空格值" 语法传递，避免整个属性被误当作参数；失败时输出当前安装日志。
