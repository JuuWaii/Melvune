# GitHub 发布记录 · 0.2.0-rc.1

2026-10-10，用户授权重写 README、整理推送文件、推送 GitHub 并发布 Release。

## 发布范围

目标公开仓库为 `JuuWaii/fnmusic_pc`，默认分支 `master`，候选版标签
`v0.2.0-rc.1`。README 改为下载/安装、功能、首次使用、配置迁移与源码构建
入口；使用指南和 Release 正文同步安装器与图标状态。

推送当前已审查的原生应用、锁定构建材料、图标、许可、文档、脚本和历史。
个人配置、会话、索引、审查日志、工具缓存和发行二进制不进入源码 Git。
用户 bug/ 删除/改名及 img/ 未审查图片保留在本机工作区。

## 发布流程与校验

标签触发 `.github/workflows/windows-packages.yml`。Windows 构建先执行源码/
历史隐私、核心与脚本回归，再生成 ZIP、便携 EXE、安装 EXE 和 MSI，逐文件
核对包内容、内嵌 MSI 与七档图标资源。独立发布作业下载这一构建的 artifact，
限定九个公开附件，确认来源提交与标签匹配以及全部 SHA-256。

发布器先创建 prerelease 草稿，附件上传后核对 GitHub 返回的大小和 SHA-256
digest；只有九个附件完整一致才公开。部分上传失败保持草稿，同一构建重跑
可继续补齐；已有不同附件不覆盖。不会将候选版标为稳定版或替换旧 Release。

源码与封装以本次标签提交构建；新的云端产物和本机历史包哈希可以不同，以
Release 中的 SHA256SUMS、release.json 和发布正文为准。图形安装、便携
图形入口和跨版本升级仍保留人工验收边界，见 [封装说明](WINDOWS_PACKAGING.md)。

## 远端状态

已成功推送 `master` 和注释标签 `v0.2.0-rc.1`，标签指向源码提交
`05236e32b68b63e89dc3556ea48f92da15fb36bd`。GitHub 已显示重写后的 README；
用户 bug/ 与 img/ 工作区保持原状。

[首次发布构建](https://github.com/JuuWaii/fnmusic_pc/actions/runs/38049853445)
的 package 作业已成功：隐私、SDK、核心/脚本检查、产物构建、包内容及图标
校验与 artifact 上传全部通过。发布作业创建草稿后按标签读取草稿返回 404，
因此在上传 Release 附件前中断，尚未公开。

发布器已改为认证后的 Release 列表查询草稿，后续按 Release ID 核验；增加
两项回归，离线发布完整性测试现为 9/9。补充的 `publish-verified-candidate.yml`
复用首次构建的 artifact，保持应用来源/标签不变，仅修正发布脚本，不重建包。
上述第一次失败记录保留；最终公开结果如下。

预定入口：[Release](https://github.com/JuuWaii/fnmusic_pc/releases/tag/v0.2.0-rc.1)
与 [Actions](https://github.com/JuuWaii/fnmusic_pc/actions/workflows/windows-packages.yml)。

## 最终公开交付（2026-10-10 20:02，Asia/Shanghai）

[0.2.0-rc.1 Release](https://github.com/JuuWaii/fnmusic_pc/releases/tag/v0.2.0-rc.1)
已公开为 **Pre-release**，不是草稿。九个发行附件上传完成，每个远端大小与
SHA-256 digest 都和已验证的构建产物一致；页面显示 11 个 Assets，另两个为
GitHub 自动生成的源码 ZIP/tar。

- 应用与封装来源：`05236e32b68b63e89dc3556ea48f92da15fb36bd`，标签未移动。
- 构建作业：[38049853445](https://github.com/JuuWaii/fnmusic_pc/actions/runs/38049853445) 的 package 成功。
- 发布作业：[38050468814](https://github.com/JuuWaii/fnmusic_pc/actions/runs/38050468814) 全部成功，发布脚本来自 `aa5bc75`。
- 云端原生检查 118 项、Node 脚本 24 项通过；恢复发布时的完整性测试 9/9。
- 包内容核验：应用 590 文件、MSI 592 文件、EXE 内嵌 MSI 一致；三个 EXE 的七档图标一致。
- 四个公开二进制下载链接 HEAD 请求均返回 HTTP 200；公开 release.json 已读取并核对来源、版本与未签名状态。

| 公开附件 | SHA-256 |
| --- | --- |
| FnMusic-0.2.0-rc.1-win-x64.zip | C67F4BDEADE1AF7084DD6792CFC3B4F670D7DF40437CAD06647F297DD8ACE1D1 |
| FnMusic-0.2.0-rc.1-win-x64-portable.exe | E896CB8C95B9DE816946211A8AA1C41FAC24534AF95E6733E75EAFFE7C6B8444 |
| FnMusic-0.2.0-rc.1-win-x64-setup.exe | 5FA27987670A4003EB112E7073183109AC07AB49EF7DB4E25B0A7D3872B69FA7 |
| FnMusic-0.2.0-rc.1-win-x64-setup.msi | 28B1470A40FDCA19FAEF5B6EA0CFA26EBEAE8FAF6845CA1037F4DF45534F1D44 |

同时公开 ZIP 校验文件、安装包 SHA256SUMS、release.json、WIX_LICENSE.txt
和 WIX_NOTICE.md。它们由附件白名单约束；本机 dist、.tools、.review-audit、
个人配置/索引、未审查图片及用户工作区改动均未推送到源码或作为附件上传。
本机历史包保留，以本次 Release 的附件和校验和为公开交付基准。

## 公开仓库文件清理（2026-10-12）

移除 `bug/` 下两份原始反馈、`logo/` 下两张旧图标，以及已完成任务的
草稿恢复发布工作流、旧 Release 删除工作流与删除脚本。常规构建发布工作流
`windows-packages.yml`、完整性校验发布器、正式 `assets/branding` 图标、
源码、测试、技术与验收资料、许可文件均保留。

`bug/`、`img/`、`logo/` 加入根目录忽略规则，防止本机反馈与未审查图片再次
提交。原始反馈的修复记录继续保存在 `docs/BUG_TRIAGE_*` 文档中；2026-10-10
的一次性 Actions 执行记录仍可在 GitHub 查看。

本次基于 GitHub 的 `6dbcaa2` 独立提交清理，本机尚未推送的 rc.2 提交与正在
修改的文件未随本次清理发布。已有 Git 标签、Release 附件和 Git 历史不改写。
