# 内嵌歌词（2026-10-06）

对应 `bug/2026_10_5.md` 中“部分歌词已经内嵌到歌曲文件内”的反馈。复用现有歌词界面、时间轴和跳转，不修改音频文件。

## 选择与生命周期

本地优先使用有效、非空的同名 LRC；缺失、空内容、过大、无法解码或无法读取的附属文件回退到音频标签。内嵌的带时间文本与毫秒 SYLT 使用现有时间轴，纯文本明确显示无时间轴；多个内嵌字段中有效定时歌词优先于纯文本。

飞牛优先使用接口或元数据缓存提供的歌词。接口成功返回空内容时，回退到当前所选歌曲播放流的内嵌标签；接口认证或网络失败仍显示原失败，不掩盖错误。解析与解码共用同一个受控随机访问输入，恢复原读取位置后交给 FFmpeg。没有再次打开一个音乐源，也不会在后台资料同步时下载音频，但解析标签可能增加当前 HTTP 播放流的有界 Range 读取。

切歌、停止和来源替换清除旧快照；等待播放打开的歌词任务支持取消，迟到结果核对来源、曲目和任务身份。非随机访问输入跳过标签读取。所有读取在后台执行；每次解析有 3 秒合作式期限、32 MiB 累计读取、16 MiB 单块和 20,000 对象上限，歌词文本最多 512,000 字节、10,000 行。同步文件系统调用仍可能受操作系统/共享目录延迟影响，不能声称期限强制中断所有同步打开操作。

## 格式范围

| 文件/容器 | 已接入的歌词字段 |
| --- | --- |
| MP3、ID3v2.2/2.3/2.4 | ULT/USLT、SLT/SYLT、TXX/TXXX 的歌词键；Latin-1、UTF-8、UTF-16 与大小端/BOM；扩展头、常见非同步化和分组前缀 |
| FLAC | Vorbis comment 的 LYRICS、UNSYNCEDLYRICS、SYNCEDLYRICS 等歌词键 |
| Ogg Vorbis、Opus | 首个识别音频流的 comment/OpusTags，含跨页评论包 |
| M4A/MP4 | moov/udta/meta/ilst 下的 ©lyr/data，UTF-8、UTF-16BE；支持 64 位 atom 大小 |
| WAV、AIFF/AIFC | ID3 chunk 中的上述歌词帧 |
| APE 标签 | APEv1/v2 尾部的 UTF-8 文本歌词字段，兼容后接 ID3v1 |
| ASF/WMA | 扩展内容、metadata、metadata library 中的 Unicode WM/Lyrics |

不伪造 MPEG 帧计数 SYLT 的时间；压缩/加密 ID3、二进制 WM/Lyrics_Synchronised、RF64、任意多路 Ogg/chained stream 和逐字歌词不在此覆盖范围。Ogg 核对包连续性和长度，未增加 CRC 校验。截断、异常长度或超限标签按无可用歌词处理，不沿标签中的 URL 打开文件或网络。

格式依据为 [ID3v2.3](https://id3.org/id3v2.3.0)、[Vorbis comment](https://xiph.org/vorbis/doc/v-comment.html)、[FLAC 格式](https://www.xiph.org/flac/format.html)、[TagLib Sharp 的 Apple 标签实现](https://github.com/mono/taglib-sharp/blob/main/src/TaglibSharp/Mpeg4/AppleTag.cs) 和 [Microsoft ASF 元数据](https://learn.microsoft.com/en-us/windows/win32/wmformat/metadata)。未引入新运行时依赖或复制其实现。

## 验证

核心 Debug 完整 119/119；Release 内嵌专项 5/5。包括 15 组容器标签、编码/同步帧、损坏与取消、5 GiB 稀疏 MP4 跳过音频区且读取少于 4 KiB，以及 LRC 优先与异常回退；回退前后音频 SHA-256 保持。

`scripts/create-embedded-lyric-fixtures.py` 使用独立 FFmpeg 写入器生成 MP3、FLAC、M4A、Ogg、Opus、WMA 六格式各一份定时/纯文本有效音频，共 12 文件，无 LRC。独立生产器回读 12/12 通过；真实原生播放器、共用播放流标签、高亮/定位、返回、停止及迟到结果隔离共 65/65，通过报告为 `.review-audit/embedded-verified-app/embedded-lyrics-window-verification.json`，时间 `2026-10-06T18:59:06.4741677+08:00`、stage=complete、error 为空。SYLT/WAV/AIFF/APE 等格式目前有构造容器专项，未把它们列为独立有效音频播放验收。

最终完整窗口 610/610，通过报告 `.review-audit/embedded-verified-app/collection-window-verification.json`，时间 `2026-10-06T19:00:54.7498706+08:00`、stage=complete、error 为空。此前一次歌词页播放器排列断言失败，补充诊断后的完整复测也 610/610、未重现；最终检查等待两个实际渲染帧替代固定 50/100 ms 延迟，保留原尺寸门槛，并在失败时记录控件尺寸。不把这次未重现的断言当作已定位的生产布局故障。

实际鼠标验证：静音双击定时 FLAC、封面进入应用内歌词页，三行歌词与第三句高亮可见；点击第一句后时间回到 00:04、第二句高亮，Escape 返回保持播放和列表。切换 Ogg 后出现两行纯文本和无时间轴提示；停止清除歌词，正常关闭独立进程。11 项观察记录 `.review-audit/embedded-preview-real-verification.json`，没有声称听音。

实际 Release 恢复既有飞牛会话，选择一个接口歌词缓存为空的歌曲，播放时钟 00:11→01:19、封面进入和队列右侧覆盖/Escape 正常；这首仍显示无可用歌词，未建立其文件含内嵌歌词的事实，因此不作为真实 NAS 内嵌成功证据。停止、恢复原静音状态后正常关闭，八配置/会话文件哈希不变，9 项观察记录 `.review-audit/embedded-nas-real-verification.json`。真实 NAS 含已知内嵌标签的正例仍保留扩展验收。

Debug/Release 构建零警告零错误；隐私专项 8/8，源码扫描通过。最终独立 Release 在 `.review-audit/embedded-verified-release-app/`；上述实际 NAS 操作使用生产源码相同的 `embedded-shared-stream-release-app`，之后仅调整 Debug 验证等待/诊断。本次不改写 preview.9 固定验证包。
