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
