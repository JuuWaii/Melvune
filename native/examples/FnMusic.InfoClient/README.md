# Melvune 只读信息消费者

这是独立 .NET 10 Windows 控制台程序，没有播放器项目依赖，通过当前用户 NamedPipe 读取数据。协议见 [PLAYBACK_INFORMATION_PROTOCOL.md](../../../docs/PLAYBACK_INFORMATION_PROTOCOL.md)。先在 Melvune 设置中启用“本机音乐/歌词信息接口”；此功能默认关闭。新构建名为Melvune.InfoClient.exe，源码目录与旧协议标识保留以兼容已有接入。

在仓库根目录构建和运行：

```powershell
dotnet build native/examples/FnMusic.InfoClient/FnMusic.InfoClient.csproj -c Release
dotnet run --project native/examples/FnMusic.InfoClient -c Release --no-build -- list
dotnet run --project native/examples/FnMusic.InfoClient -c Release --no-build -- snapshot
dotnet run --project native/examples/FnMusic.InfoClient -c Release --no-build -- lyrics --offset 0 --limit 32
dotnet run --project native/examples/FnMusic.InfoClient -c Release --no-build -- subscribe --count 5
```

`--data-directory <绝对目录>` 指向 Melvune 的数据目录（不是 `playback-information` 子目录），适合应用以 `--data-directory=<绝对目录>` 启动的隔离验收。默认目录仍为 `%LOCALAPPDATA%\FnMusic.Native`。多实例时用 `--instance <GUID>` 选择 `list` 输出中的实例；默认探测最多 128 条发现记录，选择可连接实例，跳过崩溃留下的记录。

歌词页的 `nextOffset` 为下一页偏移；为避免切歌后混入另一首歌词，后续页追加 `--track-generation <上一页的匿名GUID>`，不匹配时返回 `trackChanged`。每页可能因 JSON 字节边界返回少于请求行数。`subscribe` 输出初始状态及后续最新状态，默认读取 5 条，可指定 1–100 条；最长约每 5 秒有一次心跳。每行是一个 JSON 响应，适合其他工具逐行消费。

返回码：0 成功；1 参数或通信失败；2 接口没有可连接实例；3 协议错误响应。消费者只支持读取，不提供播放控制。
