# 长音频跳转：通用方案调研

2026-09-24。状态：可随机访问流已接入 App，当前使用准确定位；快速定位仍仅为实验。preview.3 本机目录已生成并实际启动，仍在补充最终验收。

## 首轮可执行验证

新增 `native/tests/FnMusic.SeekProbe`，固定 FFmpegInteropX 2.1.0.81200。已在 .NET 10 / Windows x64 实际加载原生库，用 IRandomAccessStream 播放 10 分钟合成 WAV，向 570、30、450 秒定位并确认播放位置继续推进。首次样本每次读取 294,912 字节，约 280 毫秒（包含 250 毫秒播放观察）；总读取约 1.84 MB，源文件 57.6 MB。该结果只证明本地可随机读取路径，无真实 NAS 性能结论。具体运行步骤与限制见验证工程 README。

后续已完成受控 HttpRangeStream 桥接的 WAV/FLAC/MP3/OGG 定位，以及阻塞响应时关闭输入、取消原生打开的验证。MP3 首次尾部定位读取约 4.46 MB / 4.8 MB，OGG 一次定位读取约 1.57 MB / 1.95 MB；功能通过但网络效率仍有缺口。详细矩阵见验证工程 README。下一阶段优先调查媒体索引/定位策略，再验证响应正文取消与 App 生命周期；正式播放器仍保留原实现。

## 结论

App 现在异步创建 SeekablePlayback，持有原生媒体源直到停止。停止先从 MediaPlayer 脱离旧媒体项，再关闭托管输入、取消网络读取，最后在后台释放原生源；切歌与取消返回后检查实例身份，旧任务不能替换新源。FFmpeg 协议白名单为 `none`，仅允许应用提供的自定义输入，不允许解码器自行打开嵌套文件/网络 URL。正式播放不再走 stdin→PCM 预读链路；旧 CLI 解码器及缓存仍保留为基础回归工具。

2026-09-25 压力验证曾发现先关闭输入再脱离 MediaPlayer 会导致后续偶发打开失败。调整上述顺序后，60 次 MP3 打开与快速前后跳转通过；第 20/40/60 次私有内存约 77/98/83 MiB，末次相对热身后增加约 6.7 MiB。正文阻塞的活动跳转也通过停止与清理检查。界面在 MediaOpened 前不再读取未就绪播放属性。

集成 Debug 应用的合成音频和真实 NAS 回归均通过打开推进、暂停恢复、跳转、设备切换与停止；共享生产播放器代码的受控测试覆盖正文阻塞时切歌、重复停止。打包脚本和第三方声明尚需随新 DLL 依赖更新。

补充实验：FFmpeg `fflags=+fastseek` 将 MP3 首次尾部定位读取量从约 4.46 MB 降为 256 KiB，三次定位总读取约 1 MiB。但底层对 VBR 可能采用近似 TOC / 比例定位，仍需音频时间标记回归，不能仅依据播放时钟切换默认配置。阻塞响应正文时关闭输入的取消检查也已通过。实验参数与限制见 SeekProbe README。

采用 FFmpegInteropX 的可随机访问流入口，复用现有 HttpRangeStream 与 MediaPlayer。原 FFmpeg 子进程读取 pipe:0，输出端 -ss 必须解码并丢弃前缀；把 -ss 移到输入端不会让管道变成可随机访问文件。现已替换 App 中的该链路，CLI/PCM 类保留用于基础回归。

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
