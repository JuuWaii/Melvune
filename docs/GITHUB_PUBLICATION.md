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
执行者将在实际核实后更新最终状态，不以草稿创建替代公开成功。

预定入口：[Release](https://github.com/JuuWaii/fnmusic_pc/releases/tag/v0.2.0-rc.1)
与 [Actions](https://github.com/JuuWaii/fnmusic_pc/actions/workflows/windows-packages.yml)。
