# 原生解码依赖

preview.3 使用 NuGet 锁文件固定的动态库，不再要求用户安装 FFmpeg 命令行程序。库通过应用托管流读取媒体，账号与请求由应用负责。

| 包 | 固定版本 | 包声明许可证 | 源码版本 |
| --- | --- | --- | --- |
| FFmpegInteropX | 2.1.0.81200 | 组合包，见子包 | 见下列组件 |
| FFmpegInteropX.Desktop.Lib | 2.1.0 | Apache-2.0 | [841fc8e79346a235906f1dbd45be53034b2e2f52](https://github.com/ffmpeginteropx/FFmpegInteropX/tree/841fc8e79346a235906f1dbd45be53034b2e2f52) |
| FFmpegInteropX.Desktop.FFmpeg | 8.1.2 | LGPL-2.1-or-later AND Zlib AND MIT | 包元数据标识 FFmpeg commit `38b88335f99e76ed89ff3c93f877fdefce736c13`，上游 https://git.ffmpeg.org/ffmpeg.git |

打包脚本保留 Apache 2.0 全文，并从固定 FFmpeg NuGet 包复制其全部 licenses 文件（FFmpeg、bzip2、dav1d、iconv、liblzma、libxml2、OpenSSL、zlib）。它们不适用本项目 MIT 许可证。具体依赖分辨版本与内容哈希见 `native/src/FnMusic.App/packages.lock.json`，交付文件哈希见包内 `SHA256.json`。

rc.1 随包提供完整对应源码归档、固定子模块提交、实际 DLL 导出的 configure 参数、补丁与构建材料，以及自包含运行时许可和 NOTICE。材料生成与 DLL/归档哈希核对见 scripts/prepare-distribution.py、scripts/check-preview.ps1；构建及替换见 [对应源码说明](BUILDING_SOURCES.md)。这记录实际材料与技术对应关系，不将包元数据称为独立法律审查。开发测试 CLI 不进入包。

2026-10-01 后续源码构建使用 FFmpegInteropX 的 MP3 预滚补丁，桥接 DLL 版本 `2.1.0.1`。上述 NuGet 继续提供 C# 投影、WinMD 和原版 FFmpeg 动态库；桥接 DLL 的身份改由 `native-decoder-build.json` 记录。固定 preview.3～preview.6 保持原版 DLL，不受影响。补丁源码、构建依赖哈希、验证及许可材料见 [预滚修复](NATIVE_MP3_PREROLL.md)。
