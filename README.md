# 飞牛音乐 Windows 原生客户端

当前代码版本为 **0.2.0-preview.5**，包含多目录音乐库、两类歌单、飞牛设置及封面/时长回退修复。使用范围和步骤见 [测试指南](docs/PREVIEW_VALIDATION.md)。可通过 `./scripts/package-preview.ps1` 生成独立测试目录；尚非正式安装发行版。此前 preview.4 的发布包验收见 [阶段记录](docs/PREVIEW4_ACCEPTANCE.md)。

使用 C#、WinUI 3/XAML 和 FFmpeg 构建的非官方 NAS 音乐客户端，目前处于阶段 4 开发预览。

已实现原生登录与加密会话恢复、分页曲库、播放/暂停/停止、进度拖动、音量与输出设备、双击切歌、当前页队列和四种播放模式。队列管理支持查看、播放所选、移除和清空；移除当前曲目或清空队列会停止播放。

## 构建与运行

```powershell
./scripts/native.ps1 bootstrap
./scripts/native.ps1 build
./scripts/native.ps1 test
./scripts/native.ps1 run
```

本机已有匹配 SDK 时可跳过 bootstrap。原生播放器使用固定版本的 FFmpeg 动态库，构建与独立发布目录包含这些依赖；日常播放无需 PATH 中的 ffmpeg.exe。部分合成音频验证工具使用开发机已有的 FFmpeg 可执行文件生成样本。依赖和许可边界见 [解码器说明](docs/NATIVE_DECODER_DEPENDENCIES.md)。

[原生工程说明](native/README.md) · [阶段进度](docs/PHASE_STATUS.md) · [开发清单](docs/NATIVE_BACKLOG.md) · [FFmpeg 播放层](docs/FFMPEG_PLAYBACK.md)

多音乐源、音乐库分类、两类歌单、飞牛设置同步、主题及对外音乐/歌词信息接口的最新排期见 [产品路线图](docs/PRODUCT_ROADMAP.md)。收藏基础已接入，验证边界见 [收藏状态](docs/NATIVE_FAVORITES.md)。

歌曲搜索与分页已通过真实 NAS 只读验收；回车、空结果、多页和迟到请求隔离已通过合成窗口验收，见 [搜索状态](docs/NATIVE_SEARCH.md)。专辑/歌手列表、基础详情和详情歌曲播放已接入，真实列表及详情歌曲分页接口验收通过，见 [专辑与歌手状态](docs/NATIVE_COLLECTIONS.md)。多库、收藏、歌单和设置已有阶段实现，真实账户/目录及扩展验收继续跟踪；FN Connect、OAuth、桌面集成和正式发布保留后续。

## Electron 归档

旧客户端已从活动源码和构建入口移除，归档位置及恢复说明见 [清理记录](docs/ELECTRON_ARCHIVE.md)。原生配置位于 `%LOCALAPPDATA%/FnMusic.Native/`；旧应用用户数据未删除。

辅助检查无需安装 npm 依赖：

```powershell
node --test tests/*.test.cjs
node scripts/check-privacy.js
```

本项目与飞牛官方无关联或背书。许可见 [LICENSE](LICENSE)，依赖及参考归属见 [第三方声明](THIRD_PARTY_NOTICES.md)。
