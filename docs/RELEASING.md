# GitHub Actions 发布流程

当前候选为 `0.2.0-rc.8`，仅 Windows x64 ZIP 与便携 EXE，未签名。

## 发布入口

维护者审查源码、许可、AI 声明、更新说明与隐私检查后，推送发布提交和对应标签 `v0.2.0-rc.8`。`.github/workflows/windows-packages.yml` 在标签推送时构建并发布预发布版；手动运行工作流只构建产物，不创建 Release。不能把已有标签移动到另一提交来覆盖一次发布。

## 构建和公开

1. Windows 构建作业检查版本、标签、当前源码与 Git 历史隐私，执行核心、来源、队列、脚本和发布工具回归。
2. 从同一提交构建 ZIP 和便携 EXE，验证全部文件、CRC、摘要、便携尾部、来源元数据与七档图标。
3. `release-candidate.py stage` 再次核对完整便携载荷及标签源码，将固定七个附件暂存：ZIP、便携 EXE、USER_GUIDE.md、RELEASE_NOTES.md、AI_DISCLOSURE.md、release.json、SHA256SUMS.txt。
4. 独立发布作业下载本次构建的 artifact，重新核对每个附件摘要及远端标签提交。仅此作业拥有 `contents: write`，使用 Actions 的临时令牌。
5. 创建预发布草稿，上传固定附件，逐一比较 GitHub 返回的大小、状态和 SHA-256 digest。全部匹配后才公开为 Pre-release，不设为稳定最新版。

SHA256SUMS 覆盖除自身外的全部公开附件；release.json 记载源码、工作流地址与内容附件摘要。自动生成的 GitHub Source code ZIP/tar 不属于这七个发行附件。

## 失败与恢复

上传或校验失败时保留草稿，不公开半份内容。同一来源、同一构建说明的草稿可重跑继续上传缺少的附件；已有附件摘要不符、未知附件或已经公开的 Release 均拒绝修改。不使用覆盖上传，不删除历史 Release 或标签。草稿按认证后的 Release 列表查找，再按 Release ID 操作，避免按标签访问草稿时的 404。

重新构建的二进制可能因构建时间等原因不同。不同摘要的产物不能混入同一草稿；需要维护者核对失败原因和残留状态。软件运行、听音、系统媒体交互及真实服务器兼容性仍按用户验证单验收，云端包校验不替代这些检查。

离线回归：`python -m unittest discover -s tests -p test_release_publication.py`。本地可以 `stage` / `verify` 验证材料；`publish` 使用 GitHub CLI 和 `GH_TOKEN`，仅在授权发布时运行。
