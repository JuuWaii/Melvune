# 封面接口的正文期限与关闭保护

2026-10-05。界面封面加载原已有独立 15 秒期限；本次补齐 NasApiClient 封面接口自身的保护，不将接口缺口描述为当前界面必定无限等待。`ResponseHeadersRead` 收到响应头后，HttpClient.Timeout 不再覆盖正文；客户端关闭也需要主动取消仍在读取的正文。

封面请求从发送到正文读取完成共用 15 秒期限，并链接调用者和客户端生命周期。调用者取消仍返回取消，超时或客户端关闭返回连接不可用；关闭后的新请求以 ObjectDisposedException 拒绝。超时及正文断连保留会话，不自动重试，可由调用方显式发起新请求。维持同源会话、拒绝重定向、尺寸枚举、图片媒体类型、4 MiB 正文限制及旧会话 401 隔离。

旧代码在真实回环 TCP 服务器声明 PNG 完整长度、只发送一个字节后，超过 18 秒看门狗仍未结束；记录 `.review-audit/cover-lifetime-before.log`。测试自行取消并清理连接，没有访问 NAS。修复后验证正文 15 秒超时并关闭连接、8 秒响应头延迟仍计入总期限、调用者取消保留替换会话、重复关闭客户端立即结束正文，以及正文意外截断转为连接失败。服务器核对调用次数及合成会话头，显式重试取回完整 PNG 字节，不把字节读取检查描述为新增图片显示验收。

聚焦命令：`dotnet run --project native/tests/Melvune.Tests/FnMusic.Tests.csproj -c Debug -- --verify-cover-requests`。五项封面 TCP 检查也纳入完整核心回归，和 JSON 四项使用同一服务器夹具；JSON 的原有期限与关闭检查继续保留。完整检查需要首个参数指定独立数据目录，`--ffmpeg` 增加六项生成音频检查。

封面原生窗口专项报告 `.review-audit/cover-lifetime-app/artwork-window-verification.json`，时间 `2026-10-05T17:07:52.9024484+08:00`，21/21、stage=complete、error 为空；核对生产解码、像素尺寸、缓存、大封面与关闭取消。没有因本次接口修复重跑已通过的 574 项整套导航布局检查，其证据仍属于顶部导航阶段。完整核心、构建及当前运行目录见 NATIVE_BACKLOG.md。

完整 Debug 107 项、Release 106 项均通过，包含五项封面 TCP、四项 JSON TCP 和六项生成音频；日志为 `.review-audit/cover-core-debug.log` 与 `.review-audit/cover-core-release.log`。应用 Debug/Release 零警告零错误，隐私回归 8/8 和 210 文件扫描通过。最新独立 Release 为 `.review-audit/cover-lifetime-release-app`，需要保留整个目录运行。

此前 preview.8 固定包保持原快照，更多实际 NAS 网络故障、标题栏鼠标、DPI 与完整重点交付关卡仍待验收。
