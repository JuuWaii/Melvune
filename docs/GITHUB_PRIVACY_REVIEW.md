# GitHub 推送前隐私审查（2026-10-09）

目标仓库为 `JuuWaii/fnmusic_pc`，当前为公开仓库。本轮准备源码与封装材料，
不执行推送、打公开标签或上传 Release；保留用户现有工作区改动。

## 已检查范围

- 当前跟踪文件、未跟踪源码以及 `.github` / `packaging` 的新文件。
- HEAD 可达的 1,457 个历史文件版本；审查时本地比已刷新 origin/master 多
  113 个提交。本轮封装提交也在完成后重新扫描。
- Git 提交身份：全部使用 GitHub noreply 地址；未发现个人邮件身份。
- 应用 EXE/DLL/PRI/XBF 的字符串扫描：未发现个人用户目录、私钥、服务令牌
  或个人 FN Connect 域名。原包的 manifest、ZIP 与所有安装器内容逐项核验。
- 发行包文件名、源码材料、运行库许可和构建元数据；不包含本机 NAS 配置、
  会话、媒体索引、测试 profile、PDB、构建工具或审查日志。
- 已验证 `.tools/`、`.review-audit/`、`dist/` 及 native/packaging 构建输出由
  Git 忽略规则排除；安装包作为 GitHub Release/artifact 分发，不进入源码 Git。

`node scripts/check-privacy.js` 通过。历史检查脚本输出只包含位置、分类和
短指纹；私钥、令牌、个人地址/配置、用户目录路径和非 noreply 提交邮件等
未审查命中会使检查失败。邮件模式还会报告上游许可署名和示例地址，供人工
审查，不把这些必要版权归属当成凭据。

历史中 3 个凭据模式命中和 3 个 FN Connect 模式命中已核对为固定单元测试
合成值（浏览器探针的假会话、已归档存储测试密码、单字母远程地址示例）。
只按具体文件、分类和完整匹配内容指纹豁免；其他值仍会阻断，不整文件放行。
源码中邮箱命中为合成测试地址或第三方许可联系人，提交身份为隐私邮箱。

对应解码源码归档保留固定上游公开仓库中的历史签名材料；它不是本机用户
凭据，未用于签署本次产物。安装器的 WiX MS-RL 全文及固定源码来源已保留。

## 保留在本机且不纳入本轮提交

用户现有 `bug/` 删除/重命名改动及 `img/` 均不纳入本轮提交；未把未审查的
截图当成可公开资产。不能仅凭文本模式扫描确认图片里的隐私。

之前验收的 `.review-audit/rc1/runtime-restored-profile/` 含复制的本机配置，
已确认被 Git 忽略且不在任何发行包内。此前自动审批拒绝其递归清理；未重试，
用户可以手动删除。其他原始审查日志也只留在被忽略的审查目录。

## 可重复检查

```powershell
node scripts/check-privacy.js
python scripts/audit-release-privacy.py --output .review-audit/github-privacy.json
git diff --cached --check
git status --short
git push --dry-run origin master
```

提交时仅添加本轮已审查文件，不使用 `git add .`，不使用 `push --all` 或
`--mirror`。公开推送前再次确认目标分支及未跟踪图片/私人配置未进入提交。
模式扫描和人工核对降低已知泄露风险，不构成对任意未知秘密的绝对证明。
