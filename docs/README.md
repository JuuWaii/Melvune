# 文档索引

## 使用与下载

当前已公开的安装版见 [rc.9交付回执](RC9_PUBLICATION.md)，以及 [rc.9更新说明](releases/0.2.0-rc.9.md)、[验收范围](RC9_ACCEPTANCE.md)与[用户验证单](RC9_USER_VALIDATION.md)。rc.8资料保留为历史交付快照。

- [使用指南](USER_GUIDE.md)：连接来源、音乐库、歌单、歌词、队列和设置。
- [rc.8 云端发布回执](RC8_PUBLICATION.md)。
- [rc.8 更新说明](releases/0.2.0-rc.8.md)、[验收范围](RC8_ACCEPTANCE.md)、[用户验证单](RC8_USER_VALIDATION.md)。
- [命名与数据目录](MELVUNE_MIGRATION.md)：独立配置、重新登录与旧数据保留。
- [桌面联动](DESKTOP_INTEGRATION.md)、[音频输出](AUDIO_OUTPUT_ACCEPTANCE.md)、[队列恢复](QUEUE_SESSION.md)。

## 来源与扩展

- [本地音乐](NATIVE_LOCAL_MUSIC.md)、[多音乐库](NATIVE_MULTI_LIBRARIES.md)、[本地索引](NATIVE_LOCAL_INDEX.md)。
- [飞牛歌单](NATIVE_NAS_PLAYLISTS.md)、[飞牛素材库与设置](NATIVE_NAS_SETTINGS.md)。
- [Navidrome](NAVIDROME_ACCEPTANCE.md)、[Emby](EMBY_ACCEPTANCE.md)、[Subsonic](SUBSONIC_ACCEPTANCE.md)、[HTTP/JSON](HTTP_JSON_SOURCES.md)。
- [播放信息与歌词协议](PLAYBACK_INFORMATION_PROTOCOL.md)：与 Windows SMTC 独立的只读接口。

## 开发与发布

- [当前开发清单](NEXT_DEVELOPMENT_CHECKLIST.md)：当前任务状态的统一入口。
- [贡献指南](../CONTRIBUTING.md)、[发布流程](RELEASING.md)、[Windows 封装](WINDOWS_PACKAGING.md)。
- [源码与动态库替换](BUILDING_SOURCES.md)、[解码依赖](NATIVE_DECODER_DEPENDENCIES.md)、[第三方声明](../THIRD_PARTY_NOTICES.md)。
- [路线图](PRODUCT_ROADMAP.md)：可能的后续方向，不代表当前版本已实现。
- [AI 生成与辅助开发声明](../AI_DISCLOSURE.md)。

## 历史资料

[历史任务清单](history/README.md)保存已完成阶段的执行记录。根目录中的 `PREVIEW*_ACCEPTANCE.md`、`RC1`–`RC7` 验收、日期命名的反馈及迁移文档同属历史快照，不能替代当前 Release 的功能说明。

[rc.8 本地交付回执](RC8_DELIVERY.md)记录此前本机包的摘要；它与随后 GitHub Actions 重新构建的公开附件不同，下载核验请使用该次 Release 自带的 SHA256SUMS 和 release.json。[旧版 GitHub 发布记录](GITHUB_PUBLICATION.md)仅记载 rc.1 的历史流程。

## 项目目录

| 目录 | 用途 |
| --- | --- |
| `native/` | WinUI 应用、核心逻辑、基础设施、测试与信息接口示例 |
| `packaging/` | 便携启动器与历史安装器源码 |
| `scripts/`、`tests/` | 构建、封装、隐私与发布验证 |
| `assets/branding/` | 正式应用与来源图标 |
| `docs/`、`docs/releases/`、`docs/history/` | 当前说明、版本更新与历史任务记录 |
| `licenses/` | 第三方许可证材料 |
| `.github/workflows/` | 云端构建与发布 |

`dist`、工具缓存、个人资料、反馈原件与验证产物不进入源码发布。
