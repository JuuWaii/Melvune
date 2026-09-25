# 随机访问播放验证

独立的 Windows x64 控制台验证工程，不改变正式 App 的播放路径。固定 FFmpegInteropX 2.1.0.81200 与 Windows SDK 投影包版本，并提交 NuGet 锁文件。CsWinRT 显式读取 NuGet SDK 的 winmd，避免依赖机器注册表中的 SDK 安装。

从仓库根目录执行：

```powershell
$env:DOTNET_CLI_HOME = Join-Path (Get-Location).Path '.tools/dotnet-home'
$env:NUGET_PACKAGES = Join-Path (Get-Location).Path '.tools/nuget'
./.tools/dotnet/dotnet.exe restore native/tests/FnMusic.SeekProbe/FnMusic.SeekProbe.csproj --locked-mode
./.tools/dotnet/dotnet.exe run --project native/tests/FnMusic.SeekProbe/FnMusic.SeekProbe.csproj --no-restore
```

生成临时 10 分钟、48 kHz 单声道 WAV，通过计量文件流和 IRandomAccessStream 交给原生解码器。MediaPlayer 静音运行，依次跳转 570、30、450 秒；每次必须达到 Playing 状态且位置越过目标 250 毫秒，读取量必须大于零且不超过一分钟音频。每步等待上限 15 秒，媒体失败立即终止；退出时释放播放器、媒体源及流，并删除临时文件。需要可用的 Windows 媒体播放环境。

计时包含至少 250 毫秒播放推进和 25 毫秒轮询，不能作为纯 seek 延迟。该验证覆盖原生加载与本地流随机读取，不证明听感正确、WinUI 界面兼容、远程取消或压缩格式性能。后续应接入受控 HTTP Range 输入和更多格式，再进入 App。

依赖包元数据：Desktop.Lib 2.1.0 声明 Apache-2.0；Desktop.FFmpeg 8.1.2 声明 LGPL-2.1-or-later AND Zlib AND MIT。正式分发前仍需整理第三方声明与对应源码信息。

## Range 与压缩格式矩阵

运行参数 `--range` 使用真实 HttpRangeStream 与内存中的受控 HttpMessageHandler，严格响应显式字节区间；没有实际网络请求。`--format=flac`、`--format=mp3`、`--format=ogg` 会使用现有 FFmpeg 可执行文件将合成 WAV 转码，然后执行同一定位序列。例如：

```powershell
./.tools/dotnet/dotnet.exe run --project native/tests/FnMusic.SeekProbe/FnMusic.SeekProbe.csproj --no-restore -- --range --format=mp3
```

Range 模式额外验证打开媒体时分别阻塞 HTTP 响应头和响应正文、随后关闭输入流：请求必须收到取消，原生打开任务必须在 3 秒内失败。此项尚不覆盖播放中停止或连续拖动。

验证工程直接链接 App 的 SeekablePlayback 和 NativeMusicPlayer 源文件，额外检查正文阻塞时替换播放源、旧打开任务不得清除新源、重复停止的清理任务，以及调用者 CancellationToken 取消打开。生产配置使用准确定位并禁用解码器自行打开外部协议；`--fast-seek` 仅影响前面的实验对照。

随后覆盖活动跳转正文阻塞时的停止，以及 60 次打开、快速连续定位、停止清理。等待 MediaOpened 后才检查播放属性，每轮必须到达最终位置并清空播放源。内存检查比较第 20 次后的私有内存，容许后续增长不超过 96 MiB，并输出第 20/40/60 次快照；这是有限轮次压力检查，不证明无限时长无泄漏。

压缩格式可能完全命中内部缓冲，允许定位期间新增读取量为零；统一读取上限只是防止异常读取，不能证明网络高效。单次读取超过文件一半时输出 NOTICE。2026-09-24 样本如下（字节数为 Range 响应总量，包含重复读取）：

| 格式 | 文件字节数 | 570 秒定位 | 30 秒定位 | 450 秒定位 |
| --- | ---: | ---: | ---: | ---: |
| WAV | 57,600,044 | 524,288 | 524,288 | 524,288 |
| FLAC | 7,614,405 | 798,661 | 1,048,576 | 786,432 |
| MP3 | 4,800,428 | 4,456,448 | 262,144 | 262,144 |
| OGG | 1,953,102 | 118,094 | 262,144 | 1,572,864 |

四种格式均完成播放位置推进与打开取消检查（取消约 12–16 毫秒）。MP3 首次尾部定位、OGG 部分定位读取量偏高，下一步调查索引及定位策略，不能据本地低延迟将它们视为慢网问题已解决。

## 快速定位对照（实验选项）

追加 `--fast-seek` 将 FFmpeg `fflags` 设为 `+fastseek`。MP3 同一样本三次定位均只读取 262,144 字节，总读取 1,048,577 字节；响应头/正文阻塞后的退出分别约 12 / 0 毫秒。

该选项没有进入正式播放器，也不默认启用。FFmpeg 的 [MP3 解复用源码](https://github.com/FFmpeg/FFmpeg/blob/master/libavformat/mp3dec.c) 表明快速模式使用 TOC 或文件比例定位，VBR 可能不精确。当前恒定频率样本只验证位置推进，不能验证实际音频与目标时间一致。下一步须用带可识别时间标记的 CBR/VBR 样本检查内容误差后，才能决定产品定位策略。此选项不同于 FFmpegInteropX 为视频关键帧提供的 General.FastSeek。
