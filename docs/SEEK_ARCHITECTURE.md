# 长音频跳转：通用方案调研

2026-09-24。状态：完成源码调研，尚未接入或验证性能。

## 结论

优先验证 FFmpegInteropX 的可随机访问流入口，复用现有 HttpRangeStream 与 MediaPlayer。当前 FFmpeg 子进程读取 pipe:0，输出端 -ss 必须解码并丢弃前缀；把 -ss 移到输入端不会让管道变成可随机访问文件。短曲 PCM 缓存继续保留，直到替代链路通过验收。

FFmpegInteropX 官方声明支持 WinUI 3 Desktop，公开了 IRandomAccessStream → FFmpegMediaSource.CreateFromStreamAsync → CreateMediaPlaybackItem 的路径。源码 CreateMediaStreamSource(IRandomAccessStream) 将流桥接为 IStream，并在 avio_alloc_context 注册 FileStreamRead / FileStreamSeek；因此它具备通用容器定位所需的输入能力。实际速度仍取决于媒体索引、服务器 Range 响应和网络。

## 方案比较

| 方案 | 判断 |
| --- | --- |
| FFmpegInteropX + 随机访问流 | 首选验证；可保留应用内鉴权和现有 MediaPlayer，需验证 .NET 10、流桥接与关闭取消 |
| 自建 FFmpeg AVIO 原生桥接 | 同样通用，但需维护原生 ABI、内存及解码生命周期，作为备选 |
| 下载完整文件后交给 FFmpeg | 普通文件可定位，但首次等待、磁盘占用及清理成本较高，不作为默认路径 |
| 继续扩大 PCM 缓存 | 只能加速已解码区域，内存随时长增长，不能解决首次远距离跳转 |
| 按格式手工计算字节偏移 | 无法统一处理压缩格式和容器索引，不继续扩展 WAV 专用实现 |

本轮 WAV 实验未通过解码音频一致性检查，已撤回，没有进入正式播放链路。

## 接入与验收顺序

1. 在隔离验证工程中固定依赖版本，验证 net10.0-windows / win-x64 的编译、原生 DLL 加载及部署。调研当天 NuGet 展示 2.1.0.81200；master 源码不等于该包，接入时核对对应版本 API。分别核对桥接库与随附 FFmpeg 的许可证。
2. 将本地 Stream 和 HttpRangeStream 桥接为 IRandomAccessStream，调用流入口；强引用媒体源直到播放结束。鉴权仍由现有 HTTP 层负责。验证嵌套媒体引用不能绕过现有文件/网络访问边界。
3. 用长 WAV、FLAC、MP3、OGG 做首次向后跳转、反复前后跳转及尾部定位；记录 Range 请求区间、读取总量、耗时与播放位置。不能只以进度条到达目标认定播放成功。
4. 受控慢响应下验证切歌、停止、连续拖动和请求取消；特别检查同步 IStream 桥接是否阻塞关闭。原生 interrupt callback 不能替代正在进行的托管读取取消。
5. 验证非法媒体、401、重定向、Range 不支持、长度变化和资源释放；与现有路径比较启动时间、内存及音频连续性。通过后再决定默认切换，保留明确的失败回退。

## 一手资料

- [FFmpegInteropX 项目与流接入说明](https://github.com/ffmpeginteropx/FFmpegInteropX)
- [FFmpegMediaSource 自定义 IO 源码](https://github.com/ffmpeginteropx/FFmpegInteropX/blob/master/Source/FFmpegMediaSource.cpp)
- [NuGet 包](https://www.nuget.org/packages/FFmpegInteropX)
- [FFmpeg pipe / fd 协议说明](https://ffmpeg.org/ffmpeg-protocols.html)
- [FFmpeg -ss 选项](https://ffmpeg.org/ffmpeg.html#Main-options)
- [官方 AVIO 自定义读取示例](https://ffmpeg.org/doxygen/trunk/avio_read_callback_8c-example.html)（示例本身没有注册 seek 回调）
