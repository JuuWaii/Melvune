# 飞牛音乐 Windows 原生客户端

本轮候选版为 **0.2.0-rc.1**，目标推荐目录 `dist/FnMusic-0.2.0-rc.1-win-x64/`。保留整个目录并双击 `Start-FnMusic.cmd`；最终包证据与推荐 ZIP 以 [候选版交付记录](docs/RC1_ACCEPTANCE.md) 为准。此前 [preview.14](docs/PREVIEW14_ACCEPTANCE.md) 目录保持不变。包含统一界面、两类歌单、本地启动索引/后台扫描、封面与内嵌歌词，以及范围读取可靠性和缓存优化。

**当前进度统一查看 [发布开发清单](docs/RELEASE_CHECKLIST.md)**：目标为 0.2.0-rc.1 Windows x64 便携候选版，按批次开发、统一检查、交付一个推荐包。第三方源、桌面歌词、对外接口和全面扩展矩阵推迟；rc.1 随包提供对应源码、许可与构建材料，候选版不是正式安装发行版。旧 [开发清单](docs/NATIVE_BACKLOG.md) 和 [路线图](docs/PRODUCT_ROADMAP.md) 保留历史及后续需求。

使用 C#、WinUI 3/XAML 和 FFmpeg 构建的非官方 NAS 音乐客户端，目前交付 0.2.0 原生候选版。

已实现原生登录与加密会话恢复、飞牛/本地完整音乐列表及来源筛选、收藏、搜索、专辑/歌手、播放/暂停/停止、进度拖动、音量与输出设备、双击切歌和四种播放模式。两类歌单与音乐库有独立管理页；播放队列从右侧覆盖打开，移除当前曲目或清空队列会停止播放。

## 构建与运行

```powershell
./scripts/native.ps1 bootstrap
./scripts/native.ps1 build
./scripts/native.ps1 test
./scripts/native.ps1 run
```

本机已有匹配 SDK 时可跳过 bootstrap。原生播放器使用固定版本的 FFmpeg 动态库，构建与独立发布目录包含这些依赖；日常播放无需 PATH 中的 ffmpeg.exe。部分合成音频验证工具使用开发机已有的 FFmpeg 可执行文件生成样本。依赖和许可边界见 [解码器说明](docs/NATIVE_DECODER_DEPENDENCIES.md)。

后续源码构建已接入 MP3 定位预滚补丁；首次 build 需要 Python 3.11+ 和 Git，自动在项目内准备固定的 C++ 工具链。源码及 DLL 身份校验、回归范围见 [预滚修复](docs/NATIVE_MP3_PREROLL.md)。固定 preview.6 包保持不变。

[原生工程说明](native/README.md) · [阶段进度](docs/PHASE_STATUS.md) · [开发清单](docs/NATIVE_BACKLOG.md) · [FFmpeg 播放层](docs/FFMPEG_PLAYBACK.md)

多音乐源、音乐库分类、两类歌单、飞牛设置同步、主题及对外音乐/歌词信息接口的最新排期见 [产品路线图](docs/PRODUCT_ROADMAP.md)。收藏基础已接入，验证边界见 [收藏状态](docs/NATIVE_FAVORITES.md)。

歌曲搜索与分页已通过真实 NAS 只读验收；回车、空结果、多页和迟到请求隔离已通过合成窗口验收，见 [搜索状态](docs/NATIVE_SEARCH.md)。专辑/歌手列表、基础详情和详情歌曲播放已接入，真实列表及详情歌曲分页接口验收通过，见 [专辑与歌手状态](docs/NATIVE_COLLECTIONS.md)。多库、收藏、歌单和设置已有阶段实现，真实账户/目录及扩展验收继续跟踪；FN Connect、OAuth、桌面集成和稳定版保留后续。

## Electron 归档

旧客户端已从活动源码和构建入口移除，归档位置及恢复说明见 [清理记录](docs/ELECTRON_ARCHIVE.md)。原生配置位于 `%LOCALAPPDATA%/FnMusic.Native/`；旧应用用户数据未删除。

辅助检查无需安装 npm 依赖：

```powershell
node --test tests/*.test.cjs
node scripts/check-privacy.js
```

本项目与飞牛官方无关联或背书。许可见 [LICENSE](LICENSE)，依赖及参考归属见 [第三方声明](THIRD_PARTY_NOTICES.md)。

使用见 [用户指南](docs/USER_GUIDE.md)，一次反馈见 [集中验证单](docs/RC1_USER_VALIDATION.md)，公开发布正文见 [发布说明](docs/releases/0.2.0-rc.1.md)。软件使用 FFmpeg（LGPL 2.1 或更高版本），完整对应源码随候选版 ZIP 提供，见 [构建与替换说明](docs/BUILDING_SOURCES.md)。
