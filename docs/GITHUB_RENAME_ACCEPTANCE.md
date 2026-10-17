# Melvune GitHub改名验收

日期：2026-10-17。

- 原仓库 `JuuWaii/fnmusic_pc` 已直接改名为 [JuuWaii/Melvune](https://github.com/JuuWaii/Melvune)，仓库ID保持 `1351488925`；默认分支master，可见性仍为public，管理员与读写权限不变，未启用Pages。
- 仓库描述更新为“Melvune · 合听 — 多来源桌面音乐播放器，支持本地音乐、NAS、Navidrome、Emby与Subsonic兼容源”。NAS origin已更新为 `git@github.com:JuuWaii/Melvune.git`。
- 保留2个分支、3个历史标签、1个公开Release和9个原附件。改名前后对照Release ID、标题、正文、标签、状态，以及附件ID、名称、大小、SHA-256和上传状态均一致；Issues/PR为空，分支保护和规则集未变。
- 原网页地址实际跳转到新仓库，已从新地址下载历史Release的SHA256SUMS.txt并与GitHub记录的摘要核对一致。
- 本地开发历史与远端1019ae0清理提交存在分叉，已通过合并提交fb5efa7整合，随后普通推送到master；无强制推送、历史重写或重新打标签。
- 保留远端已清理的bug/logo与已完成的一次性发布/清理流程。未审查img继续只在本机保留，10份资料摘要在整合前后相同。原生源码树在整合前后相同。
- 源码提交c8cdd78已推送。当前README、下载链接、许可链接和未来Melvune便携产物校验路径已更新；历史附件文件名和配置/协议兼容标识保持原样。
- 本地发布完整性9项、便携完整性16项通过；源码隐私扫描通过，1978个历史文件对象审查阻断项0。整合后源码隐私扫描再次通过。

## 云端构建

[Windows candidate packages #2](https://github.com/JuuWaii/Melvune/actions/runs/39020118348)，来源c8cdd78，手动触发master，仅生成Actions构建产物，不创建Release。

状态：全部成功，总耗时5分50秒。版本/隐私检查、集中测试、ZIP与便携EXE构建、包内容/摘要及图标校验、Actions产物上传均通过。云端隐私审查覆盖1995个历史文件对象，阻断项0；核心120项及HTTP/JSON、队列恢复等协议检查通过。

构建产物 `Melvune-0.2.0-rc.7-win-x64`，ID `12095529757`，大小517548212字节，来源 `c8cdd7864ed538aedcece7ed18ebcad4f3ac6c5a`，外层Actions归档SHA-256为 `e5f871e97ad23356d299f13a2fd446e329ed11f2a018085c267ac73038914f24`。包含应用ZIP、ZIP摘要与便携分发文件；保留至2026-10-31。

云端校验记录：应用ZIP含602项，SHA-256 `EC640CE197681E43DF4D28C51D28C315D9B60737321CE23B2E416EF88D6070BC`；便携EXE摘要 `3EB1A4C0D58A4E9049B63128B440FA2470274262DA65C3189EA1E43079FC72CF`。便携尾部、CRC、文件摘要和源码一致性通过，两种EXE的七档图标资源一致。此次核对云端校验日志及产物元数据，未将517MB的Actions归档重复下载为本地交付包；外层归档摘要与内部ZIP/EXE摘要分别记录。

构建日志有两条原生工具链C4002警告和托管Action运行时Node.js 20弃用提示；GitHub已使用Node.js 24运行，作业整体成功。工具链维护留待后续。

正式Release发布另行进行；本轮未上传或覆盖Release附件。

历史NAS产物清理仍沿用迁移清单M06状态，不属于GitHub改名验收的完成项。
