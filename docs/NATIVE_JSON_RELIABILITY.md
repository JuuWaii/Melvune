# JSON 接口的完整请求期限

2026-10-05。曲库、搜索、歌单及飞牛设置共用的 JSON 请求现在从发送到正文读完使用同一 15 秒期限。原来 `ResponseHeadersRead` 收到响应头后就结束 HttpClient 的超时保护，部分正文停滞可无限等待；依据 [微软文档](https://learn.microsoft.com/en-us/dotnet/api/system.net.http.httpcompletionoption)。音频的 Range 输入仍使用原有独立策略。

客户端关闭主动取消正文读取；调用者取消仍以取消返回，超时以可重试的连接失败返回，不清除登录。保留响应上限、重定向拒绝和旧会话失败隔离。没有自动重发请求，尤其写入超时后应先刷新核对服务器状态，再决定是否重试。

共用 HttpClient 仍保留原有 15 秒响应头超时，确保非 JSON 封面请求继续受到既有保护；JSON 另外使用覆盖正文的总期限，两者均不延长期限。

真实回环 TCP 回归先在旧代码复现：服务器声明完整长度、只发送一个 JSON 字节，18 秒看门狗超时。修复后四项检查通过：正文 15 秒超时且连接关闭；8 秒响应头延迟与正文共用总期限；调用者取消保留替换会话；重复关闭客户端及时结束停滞读取。超时后的显式新请求成功，服务器核对请求次数与合成会话头。仅使用本机临时端口和合成数据，不访问 NAS。

聚焦命令：`dotnet run --project native/tests/Melvune.Tests/FnMusic.Tests.csproj -c Debug -- --verify-json-requests`。完整检查另需首个参数指定隔离数据目录；`--ffmpeg` 额外包含六项生成音频检查。这些网络故障注入不代替真实 NAS 断网、登录和连续界面交互验收。此前 preview.8 固定包不包含本次修复。

完整 Debug 102 项、Release 101 项（含六项 FFmpeg 生成音频），应用 Debug/Release 零警告零错误；窗口报告 `.review-audit/json-deadline-app/collection-window-verification.json`，时间 `2026-10-05T15:50:30.565574+08:00`，479/479、stage=complete。隐私回归 8/8 与 198 文件扫描通过。第一次完整命令漏传隔离目录，在保护检查处中止；补齐参数后完整两种配置均通过。
