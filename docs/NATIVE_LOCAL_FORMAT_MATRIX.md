# 本地格式与标签扩展验收

2026-10-08。使用生产 LocalMusicSource、LocalMusicMetadataReader、持久索引快照恢复及 NativeMusicPlayer，23 个编码样本、162 项条件通过；每首实际静音播放推进，暂停定位至 7/2/10/0/5 秒并分别恢复推进，共 115 次前后跳转，然后停止、释放并核对音频哈希。

## 修复

Windows 属性接口可能返回 WAV 时长而缺失其 RIFF INFO 标签，原逻辑直接返回，漏掉真实标题/歌手/专辑。公开 APE 样本同样漏掉全部标签，旧代码报告 `ape_16_44100_stereo_public_sample_tags=false`。现在按字段补齐缺失的标题、歌手、专辑和时长，保留 Windows 已读到的值，沿用禁止嵌套文件/网络协议的受限流探测、3 秒取消期限及解码器清理；标签遍历和值长度有界。

旧索引仍立即恢复和可播放；后台对旧元数据读取版本重读一次，原子更新来源快照和磁盘索引，再恢复文件时间/大小未变时的元数据复用。取消升级保留原索引；不会删掉缓存迫使启动等待。核心检查验证旧 JSON 没有版本字段、取消、不变身份和升级后的零重读；原生两进程 13 项验证旧缓存渲染/实际播放、后台增删改、当前播放与选中保持、取消及目录重关联迟到隔离。

## 样本与结果

| 格式 | 样本变化 | 标签结果 |
| --- | --- | --- |
| WAV | 44.1 kHz 单声道 PCM16；96 kHz 双声道 PCM24；48 kHz 双声道 float32、六声道 PCM24 | 四种均读取标题/歌手/专辑 |
| FLAC | 44.1 kHz 双声道16位；96 kHz双声道24位；48 kHz六声道24位 | 三种均读取三字段 |
| MP3 | 44.1 kHz/320k CBR、48 kHz/q0 VBR双声道，22.05 kHz/64k单声道 | 三种均读取三字段 |
| OGG、Opus | OGG q0单声道/q9双声道；Opus32k单声道/160k双声道 | 四种均读取三字段 |
| M4A | AAC双声道，ALAC16位/44.1 kHz与24位/96 kHz双声道 | 三种均读取三字段 |
| AAC ADTS、WMA | AAC128k/48 kHz，WMA2 128k/44.1 kHz双声道 | ADTS样本未写入三字段；WMA读取三字段 |
| AIFF、AIF | 默认24位/96 kHz双声道、16位/44.1 kHz单声道；另有ID3/24位/48 kHz双声道 | 默认编码仅写入标题，标题读取通过；ID3样本三字段均读取 |
| APE | 16位/44.1 kHz双声道，60.48秒公开音频 | 标题/歌手/专辑与样本标签一致 |

编码后通过原生解码器读取实际 codec、采样率、声道和位深，报告与设定逐项核对采样率/声道；并非只记录编码命令。APE 来自 [FFmpeg 官方样本目录](https://samples.ffmpeg.org/A-codecs/lossless/)，其 [官方校验清单](https://samples.ffmpeg.org/A-codecs/lossless/md5sum) 的 MD5 `ab078cadd6367ab132124cbc0ecb8005` 与下载一致；固定 SHA-256 `6A7B79A6D530E9847C18119D627BD43C8D27DCEFB3EC7EC979B9B6306E34AC15`。仅在自有验证目录使用，不纳入 Git、发布包或重新分发。生成副本清理成功，原始样本哈希保持。

## 可复现检查与范围

Debug：`FnMusic.App.exe --verify-local-format-matrix --ape-fixture "<已校验的本地 APE 路径>"`。生成器与检查入口不进入 Release；报告只有公开/生成样本字段及异常类型，不记录私人路径。原先十格式及大内嵌封面检查也重跑，10/10 与7项损坏/取消/封面条件通过。

最终矩阵报告 `.review-audit/local-format-final-app/local-format-matrix-verification.json` 时间 `2026-10-08T15:12:19.5966062+08:00`；23项 stage=complete、异常为空，162条件均为true、cleanup=true。旧 APE 标签失败报告在 `.review-audit/local-format-tags-baseline-app/local-format-matrix-verification.json`，对应生产修复之前的新断言；不隐藏失败或将其当成功基线。核心完整 121/121（增加旧索引迁移条件但保留原测试分组计数），Debug/Release 均零警告零错误。完整原生窗口620/620通过，报告 `.review-audit/local-format-window-final-app/collection-window-verification.json`；原生旧索引种子3项与独立恢复10项报告在 `local-format-final-app`。隔离窗口的首次复制缺少运行资源而终止，补齐完整资源后新进程完成，不将首进程算作成功。

这些检查证明当前样本的正式本地扫描、标签、索引恢复、原生播放时钟、前后定位和释放；不证明采样级内容定位、多声道硬件输出/听音、任意编码或损坏媒体、真实大目录耗时和断网恢复均已完成。MP3快速模式继续不默认启用，OGG大读取量和真实持续交互/内存曲线仍单独验收。
