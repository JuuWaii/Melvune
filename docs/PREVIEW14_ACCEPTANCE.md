# preview.14 范围读取优化交付

最终固定包：`dist/FnMusic-0.2.0-preview.14-win-x64-verified-final/`，产品版本 `0.2.0-preview.14+efca13eed86ed404e753bfbe5198e7212a4d7c87`。功能源码 `785232c`，后续 `efca13e` 仅同步打包指南。保留整个目录，双击 `Start-FnMusic.cmd` 启动；旧preview.13不覆盖。

强ETag音频流保留最多2MiB近期已验证范围块，减少OGG反复定位的重复响应，不改变准确定位或解码器配置。当前序列OGG整轮字节减少47.9%、请求21降至11；弱/无标签仍只缓存一块，MP3大范围读取继续跟踪，见 [实现及对照](NATIVE_RANGE_CACHE.md)。包含此前本地标签、启动索引、播放器/侧栏及六项手动反馈修复；本地有效音频万首规模验收见 [记录](NATIVE_LOCAL_SCALE.md)。

- 核心124/124、完整原生窗口620/620、五格式强版本内容定位20/20、MP3/OGG全程Range120轮/367条件通过。缓存命中/淘汰、跨块/尾块、取消/版本拒绝保留和弱标签兼容有失败基线及新增回归；Debug/Release零警告零错误，隐私8/8通过。有限范围和生成样本不冒充真实网络、触摸、长期稳定性或听音。
- 真实NAS只读九项通过，使用自有目录中的既有加密会话副本，没有输入新凭据或切换账户；原八个配置/会话文件哈希保持，副本清理成功。报告在 `range-cache-nas-app/playback-verification.json` 及 `range-cache-nas-acceptance.json`。
- 最终包还原/发布成功，清单551文件加清单共552，发布后及实际运行后完整核验通过：`.review-audit/preview14-final-integrity-before.log`、`preview14-final-integrity-after.log`。固定解码器SHA-256仍为 `3DBE6C7083A78C8129BD994C5499385098645235A5E0B23B5F0E6E7C1585B803`。
- 最终包以仅含System32的子进程PATH启动，实际鼠标进入所有音乐546首（飞牛273/本地273），静音双击NAS歌曲，实际走时至12秒；七个播放模块均来自包内。停止/归零、恢复原静音状态并正常关闭自建进程后，原八文件保持。`.review-audit/preview14-final-runtime.json` 时间 `2026-10-08T21:13:30.7053020+08:00`。

首次preview.14固定包 `FnMusic-0.2.0-preview.14-win-x64-verified` 的实际NAS鼠标拖动前进至约184秒、退回约34秒，Home回曲首、PageUp增量定位并继续推进至25秒，通过后停止/恢复静音/正常关闭；记录 `preview14-runtime.json`。一次停止后的截图报`no monitor found for window`，重新选择同一自建窗口后确认停止已经生效，没有重复提交输入或将观察失败当作进程退出。此首次包说明页标题仍为preview.13，保留文件不覆写；纠正说明后生成并实际验证上述`verified-final`，不要将首包的拖动/键盘观察冒充最终包重新执行了同一矩阵。

本阶段降低当前强版本样本的重复读取，未宣称NAS提供强ETag或量化真实NAS速度。真实断连/失效会话、连续快速查询、触摸/更多拖动、更多DPI与实际OS主题通知、设置权限界面及重关联完成计数继续保留；所有重点目标仍按路线图有效，未以本次交付标为全部完成。
