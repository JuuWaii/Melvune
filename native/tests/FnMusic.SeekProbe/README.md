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

2026-10-08 增加 `--verify-range-stress` 独立入口，补齐上述60轮使用本地流的边界。生成较大的10分钟MP3/OGG粉红噪声，每种60轮始终通过正式HttpRangeStream及强ETag条件检查，记录每轮累计响应字节、请求数、实际播放推进、释放与私有内存。120轮/367条件通过，JSON为 `range-stress-verification.json`；读取量仍偏高，不代表真实网络或鼠标拖动验收，见 [持续Range记录](../../../docs/NATIVE_RANGE_STRESS.md)。

追加独立`--profile-range-reads`仅每格式一轮，输出`range-read-profile.json`，包括第一轮打开后字节及Range起点/次数，不执行60轮或内存增长门槛。强版本近期块缓存使OGG同序列整轮响应从4.65 MB减少至2.42 MB，20项内容定位与120轮压力回归通过；弱/无标签保留单块，见 [缓存优化](../../../docs/NATIVE_RANGE_CACHE.md)。

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

## 音频内容定位诊断（2026-10-01）

新增独立入口，补足上文恒定频率样本的证据缺口：

```powershell
./.tools/dotnet/dotnet.exe run --project native/tests/FnMusic.SeekProbe/FnMusic.SeekProbe.csproj --no-restore -- --verify-content
./.tools/dotnet/dotnet.exe run --project native/tests/FnMusic.SeekProbe/FnMusic.SeekProbe.csproj --no-restore -- --verify-content --format=mp3-vbr --fast-seek
```

生成 10 分钟、48 kHz 单声道可识别时间位置的确定性噪声样本，交替改变复杂度和音量，避免 VBR 字节分布均匀。格式为 WAV、FLAC、128 kbps CBR MP3、q=2 VBR MP3、q=4 Vorbis OGG；每种依次定位 571、31、451、5 秒，覆盖前后定位及不同编码复杂度。所有输入经过真实 HttpRangeStream、受控 Range 响应及 MediaPlayer；没有真实网络请求或用户音乐。准确模式链接生产 SeekablePlayback，快速模式只修改实验配置。

解码器创建 MediaPlaybackItem 后，在它已有的 MediaStreamSource 上追加只读 SampleRequested 观察者，不改变提供给播放器的 Sample。样本参考由另一个 FFmpeg 进程从整个编码文件顺序解码产生，参考链路没有 seek；使用归一化互相关定位实际 PCM 内容，避免把有损压缩差异误判为时间错误。跨越目标的音频包裁掉目标之前的部分；最多收集目标后半秒的 32 帧。每项另用偏移 20 秒的参考作反向控制，确认错误内容不能匹配。

诊断目标为在 50 毫秒内出现稳定音频、内容误差不超过 50 毫秒、相关系数超过 0.8，且至少两帧以及恢复后的全部观察帧满足条件。50 毫秒是本探针的回归目标，不是主观听音或全部格式的产品保证。报告保留起始过渡时长、全部帧的内容误差/相关度、读取量、编码文件大小和包含至少 250 毫秒播放观察的耗时。未达到目标返回非零退出码并保留 JSON，不隐藏首帧过渡以制造通过结果。

当前准确模式报告时间 `2026-10-01T18:11:34.3290986+08:00`：20 项中 17 项通过，VBR MP3 的 571、451、5 秒三项未通过，起始恢复时长分别约 57、57、65 毫秒。CBR MP3 约 33～41 毫秒。全部格式恢复后的已观察帧与顺序解码参考的匹配偏移均为 0 个样本；这是本次有限合成样本的诊断证据，不能替代真实设备声音、边界处每个采样点或长期播放验证。

随后 VBR 快速定位对照中，571、31、451 秒对应实际内容偏移约 -599、+407、-138 毫秒，5 秒一项通过；准确模式没有同样的持续内容偏移。快速模式因此继续禁止进入默认配置。准确模式的起始解码过渡和较大的首次读取量仍需处理，不把本诊断标为全部定位验收完成。

输出位于可执行目录的 `seek-content-verification.json`；追加 --fast-seek 时另存 `seek-content-comparison.json`，保留主矩阵。临时数据仅为本探针创建的随机目录和生成音频，退出时校验目录范围再清理。JSON、PCM 和编码样本不进入版本库。

公开的 [FFmpegInteropX 常规定位实现](https://github.com/ffmpeginteropx/FFmpegInteropX/blob/master/Source/FFmpegReader.cpp) 在音频目标包定位后刷新解码状态，且 [公开接口](https://github.com/ffmpeginteropx/FFmpegInteropX/blob/master/Source/FFmpegInteropX.idl) 没有独立的音频 seek/preroll 方法。这支持后续调查解码预滚的方向，但 master 不是本次固定包源码，不能据它直接确认固定包的内部原因。

后续正式构建已使用固定提交的 MP3 预滚补丁库，20/20 内容矩阵通过；旧版原码对照仍为 17/20。首次构建须还原包后运行 `python scripts/build-native-decoder.py`，见 [修复及证据](../../../docs/NATIVE_MP3_PREROLL.md)。报告新增实际加载的 DLL 版本及 SHA-256。追加 `--near-seeks` 使用 5、6、7、8、7、6、0、1、579 秒序列；`--read-ahead` 额外启用实验读取缓冲，默认配置仍保持原设置。短距测试发现并修正曲首匹配器粗搜索相位问题，五个已知 PCM 及错误时间控制通过，诊断门槛未改变。

`--verify-content --etag` 使受控 Range 服务端返回强 ETag，并要求后续块读取携带匹配的 If-Match，覆盖正式输入的文件版本绑定。该模式输出 `seek-content-etag.json`，当前五格式、四定位点的 20 项通过。无标签模式仍用于兼容回归；更换同大小文件、丢失或降级标签等拒绝场景在基础 RangeRepresentationVerification 中验证，见 NATIVE_STREAM_RELIABILITY.md。

## 采样率和声道布局

2026-10-03 增加 `--sample-rate=44100|48000` 与 `--channels=1|2`，默认仍为 48 kHz 单声道。例如 `--verify-content --sample-rate=44100 --channels=2`。不同布局分别保存为 `seek-content-verification-44100-2ch.json` 等文件，不覆盖默认报告；可与格式、ETag 和短距参数组合。

双声道源的两个通道使用不同的确定性噪声及复杂度变化。独立顺序解码参考保留两个通道，观察 PCM 按实际通道提取；每个通道都必须在 50 毫秒内取得至少两帧正确内容，后续帧全部符合原来的相关度与时间误差门槛，报告逐帧记录 Channel。没有将双声道平均成单声道或只检查左通道。解码输出的采样率或通道数与预期不一致时明确失败。
