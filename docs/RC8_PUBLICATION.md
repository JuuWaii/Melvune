# Melvune rc.8 公开发布回执

2026-10-17 用户授权整理项目文件、重写 README、补充 AI 声明、推送并通过 GitHub Actions 构建和发布。

## 源码与文档

- 发布源码：`9b03797c43baa520546e45c8b4ebe63314d5ab6c`。
- 标签：`v0.2.0-rc.8`，注释标签对象 `1245563b83530f5b7c2eb99aff1670a78635adcd`；未移动历史标签。
- [新版 README](../README.md)增加下载、功能边界、入门、文档和 AI 声明；两张截图使用独立合成数据。
- 12 份已完成任务清单移入 `docs/history`，增加文档索引与贡献指南，修正相关链接和使用指南的过时描述。
- 用户原始反馈、图片资料、个人数据、本地历史发行包与验证证据不随源码推送。

## 验证与发布

本轮本地集中通过发布工具 14 项、Node 24 项及便携完整性 24 项回归；18 份入口/历史文档链接检查通过，源码隐私无发现，历史审查阻断项 0。整包发布演练修复 Windows/Linux 文件名排序差异，并完整复核通过。

[本次 GitHub Actions](https://github.com/JuuWaii/Melvune/actions/runs/39043923696) 从上述标签源码重新构建，集中检查后生成 ZIP 与便携 EXE，验证文件摘要、CRC、来源、便携格式与图标。独立发布作业在全部七个附件的远端大小和 SHA-256 一致后才公开。云端包不同于此前本机包，以本次 Release 自带校验和为准。

## 公开结果

[Melvune 0.2.0-rc.8 Release](https://github.com/JuuWaii/Melvune/releases/tag/v0.2.0-rc.8) 已公开为 Pre-release，非草稿，Release ID `415875354`。package 与 publish 作业全部成功；云端核心121项、Node24项、便携完整性24项、发布工具14项及 HTTP/JSON/队列专项通过。603个载荷文件、ZIP CRC、逐文件摘要、便携来源和两个 EXE 的七档图标核对成功。历史 rc.1 的9个附件ID、大小和digest均未改变。

| 公开附件 | 字节数 | SHA-256 |
| --- | ---: | --- |
| AI_DISCLOSURE.md | 962 | `33f659b69ce65fa6c72374dcfdaf8c703d0230d28822f907d599884dc7105a80` |
| Melvune-0.2.0-rc.8-win-x64-portable.exe | 277665090 | `d84699048b24a8c78d3430197e691f3886e11debb7274729cd69c87078009448` |
| Melvune-0.2.0-rc.8-win-x64.zip | 239893708 | `150efbb3eb28dc9075b37a7cda4fae9b39c13cf9bda36af28c977ed9a6eb3a6d` |
| release.json | 1198 | `2902dea0d897f44f10d5470246bf0937f6d34bf62899d2856eee29fb9c8de22a` |
| RELEASE_NOTES.md | 3889 | `76033bcf05ea57edf9dd5f4c48190dcc091d66a71b84a917c810fda422bece65` |
| SHA256SUMS.txt | 534 | `da65cf31ee58889cd8b8c4f9180a76417b73b55f548cbc80756a89adedacc67e` |
| USER_GUIDE.md | 14058 | `c4e7e07f12c1209ce9139c34b65ae746c6f7344117886c91603826cf1fe9de2d` |

公开下载核验：五个文档/校验附件 GET 返回 HTTP 200 且完整 SHA-256 匹配；ZIP 与便携 EXE 的公开 HEAD 入口均返回 HTTP 200。公开 release.json 的来源与 Actions 地址已读取确认，SHA256SUMS 的全部六行与对应远端附件 digest 一致。两个大文件在本机的完整重下载因传输较慢停止，未列为已完成检查；二进制完整摘要比对已由云端发布作业完成。


## 验收边界

本轮未修改应用功能，不重复上一轮全量窗口回归。本次新截图仅证明合成界面的展示，不代替真实服务器和听音验收。此版本未签名，公开为 Pre-release；完整安装器、代码签名和自动更新后置。真实服务、硬件、DPI、实体媒体键和长期运行的未验证项仍以 rc.8 用户验证单为准。
