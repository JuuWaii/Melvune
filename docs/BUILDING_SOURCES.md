# 0.2.0-rc.1 对应源码、构建与替换

本包使用 FFmpeg，许可为 LGPL 2.1 或更高版本；它不适用 FNMusic 的 MIT 许可证。允许为修改这些库而调试、逆向工程和替换动态库，FNMusic 不附加限制这些权利的 EULA。保留原始版权和许可证。随软件一起分发本文件、第三方声明、licenses 目录及对应源码归档；公开下载页应指明 FFmpeg 许可和随包源码位置。无需向开发者索取源码或接受额外条款。

`licenses/corresponding-source/decoder-corresponding-source.zip` 包含 FFmpegInteropX 上游完整固定源码、所有本构建启用的 FFmpeg 依赖、构建辅助脚本、本项目 MP3 补丁及工具链锁。来源和归档哈希见同目录 `decoder-distribution.json`；FFmpeg 的实际版本、configure 参数、许可和每个 DLL 哈希也在该文件。归档内 FFmpeg 根目录 `FNMUSIC-BINARY-CONFIG.txt` 保留实际 DLL 导出的参数。本项目未修改 FFmpeg；只修改桥接库 `Source/FFmpegReader.cpp` 的 MP3 定位预滚。

## 构建 FFmpeg 和静态依赖

解压源码归档，在 `FFmpegInteropX` 目录操作。其 `Libs` 子目录已经包含固定子模块源码，无需跟随最新分支；不要更新子模块。upstream-archives 中另保留原始 tar.gz（含符号链接与文件模式），如构建环境需要这些信息可在对应 Libs 目录从原归档还原。阅读上游 `README-BUILD.md`。安装 Visual Studio C++、Windows SDK 26100、MSYS2/meson/ninja/nasm 与上游构建指南要求的工具。原包 FFmpeg 参数由 MSVC 工具链生成，默认 MSYS2 bash 路径为 C:/msys64/usr/bin/bash.exe。

```powershell
./Build-FFmpeg.ps1 -Platforms x64 -WindowsTargets Desktop -Configuration Release -SharedOrStatic shared -Gpl disable -Encoders disable -Devices disable -Programs disable -DisableParallelBuilds -FFmpegCommit 38b88335f99e76ed89ff3c93f877fdefce736c13
```

FFmpeg 源码提交 `38b88335f99e76ed89ff3c93f877fdefce736c13`，上游桥接提交 `841fc8e79346a235906f1dbd45be53034b2e2f52`，其 Libs 固定提交与包元数据一致。实际运行库导出 n8.1.2、LGPL 2.1 或更高版本，并禁用 GPL/version3。构建参数里的原机输出路径是上游构建路径，不需要在本机复用。构建后输出在 Output/FFmpegDesktop/x64。我们未重建这组上游 FFmpeg DLL，未声称编译器不同仍得到逐字节相同 DLL。

## 构建修改过的桥接与 Melvune

Melvune（原FnMusic）仓库含本项目MIT源码和NuGet锁文件。仓库执行 `scripts/native.ps1 bootstrap`、还原 `native/Melvune.sln` 后，`scripts/build-native-decoder.py` 自动获取锁定MSVC/SDK/C++WinRT依赖，校验哈希，检出固定桥接提交并应用 `native/decoder/mp3-preroll.patch`。可离线使用归档中的FFmpegInteropX源码初始化 `.tools/native-decoder/source` 并建立固定提交的Git检出；常规在线脚本直接从固定提交获取。脚本记录源码、补丁、编译材料和实际DLL哈希至native-decoder-build.json。源码构建方式详见仓库docs/NATIVE_MP3_PREROLL.md。源码内部的FnMusic命名为兼容标识，历史许可证和对应源码材料不因品牌更名重写。

```powershell
./scripts/native.ps1 build
./scripts/package-preview.ps1 -Version 0.2.0-rc.7 -Name Melvune-0.2.0-rc.7-win-x64
```

退出播放器后备份原 DLL，可用兼容 ABI 的自建 FFmpeg DLL/FFmpegInteropX.dll 替换包内同名文件。不要混合不同 FFmpeg ABI 主版本；本包使用 avcodec/avformat 62、avutil 60、swresample 6。替换后 SHA256.json 校验应报告差异，属于已修改包；不限制运行修改库。桥接 C# 投影/WinMD 来自锁定包，必须保持接口兼容。

## 其他运行时材料

licenses/corresponding-source/runtime-licenses 保留已还原 .NET、自包含 Windows App SDK、WebView2/CsWinRT 等包附带许可证和 NOTICE，runtime-license-index.json 列出材料。FFmpeg 各依赖全文在 licenses 根目录，桥接 Apache 2.0 在 FFmpegInteropX-Apache-2.0.txt。NuGet 组合包无独立运行时；Windows SDK BuildTools 仅构建时使用，不作为播放器工具分发。包来源与精确版本另见 licenses/packages.lock.json。

依据：[FFmpeg 官方分发清单](https://ffmpeg.org/legal.html)、[LGPL 2.1 全文](https://www.gnu.org/licenses/old-licenses/lgpl-2.1.html)。这里记录实际材料与技术对应关系，不将仅有包元数据称为独立法律审查。
