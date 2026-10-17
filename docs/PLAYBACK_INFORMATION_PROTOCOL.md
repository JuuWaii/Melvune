# Melvune 本机播放信息协议 v1

rc.2 提供可选、默认关闭的只读音乐/歌词信息接口。使用设置入口启用后，本机当前用户的第三方软件可按本协议接入；第三方软件需要实现消费者，不表示任意软件自动兼容。本接口没有播放控制或账号配置读取命令。

## 发现与权限

应用数据目录由 `AppDataDirectory.Resolve()` 决定，默认 `%LOCALAPPDATA%\Melvune.Native`；应用启动参数 `--data-directory=<绝对目录>` 指定隔离目录时发现记录也位于该目录。接口不会扫描其他应用的配置。

每个启用实例在 `playback-information/<随机实例GUID无连字符>.json` 原子写入一条记录。该文件严格只有三个字段：

```json
{"protocolVersion":1,"instanceId":"随机实例GUID","pipeName":"melvune-info-v1-随机GUID无连字符"}
```

管道为 `\\.\pipe\<pipeName>`，仅连接本机 `.`。服务器使用 `PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly`，独立消费者也使用 `CurrentUserOnly` 校验服务器；服务器另在当前用户 ACL 上明确拒绝 Network SID 的全部访问，阻止相同账号经网络登录读取。Windows 对当前用户和提升权限级别同时校验，因此常规消费者应与 Melvune 处于相同权限级别。见 [Microsoft PipeOptions 文档](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipeoptions?view=net-10.0) 和 [管道 ACL API](https://learn.microsoft.com/en-us/dotnet/api/system.io.pipes.pipesaclextensions.setaccesscontrol?view=net-10.0)。

启用时使用新的随机管道名；正常禁用或退出只删除本次激活仍拥有的记录，其他实例保留。强制结束、掉电可能留下发现文件：消费者必须以成功连接为准，跳过过期记录，不删除其他实例记录。示例最多检查 128 条记录，每条不超过 4096 字节。

## 帧与请求

传输使用 UTF-8 JSON，每条 JSON 后一个 LF (`\n`)，没有 BOM。字符串内部换行必须由 JSON 转义。请求不含 LF 时最多 4096 字节，响应最多 65536 字节；超量请求或未完成帧关闭连接。非法 UTF-8 或 JSON 返回通用 `invalidRequest` 后关闭；详细异常和原请求不会回传。

```json
{"version":1,"command":"snapshot","id":"request-1"}
{"version":1,"command":"lyrics","id":"request-2","offset":0,"limit":32}
{"version":1,"command":"subscribe","id":"request-3"}
```

`version` 必须为整数 1；`command` 仅支持上述三项。`id` 可省略或为最多 64 个 UTF-16 字符的字符串，原样作为请求关联标识回显。未知字段被忽略，不参与数据查询。每连接最多处理 256 个请求、每个一秒窗口最多 20 个请求；读取下条请求超时 15 秒。超过频率返回 `rateLimited` 并关闭连接。

所有响应使用同一封套：

```json
{"version":1,"id":"request-1","ok":true,"type":"snapshot","data":{},"error":null}
{"version":1,"id":"request-1","ok":false,"type":"error","data":null,"error":"unknownCommand"}
```

错误码只有 `invalidRequest`、`unsupportedVersion`、`unknownCommand`、`invalidPage`、`trackChanged`、`rateLimited`。不支持的播放/定位等控制命令返回 `unknownCommand`。连接断开或超时也可能没有错误响应，消费者需自行重连。

## 当前快照

`snapshot` 返回应用共用的实际播放快照，`subscribe` 也发送相同类型：

| data 字段 | 含义 |
| --- | --- |
| protocolVersion / instanceId / sequence / timestamp | 协议版本、随机应用实例 ID、递增快照序号、UTC ISO-8601 生成时间 |
| trackGeneration | 当前曲目匿名 GUID，停止清理时可为 null；仅在当前实例中用于切歌/迟到结果隔离 |
| state | `stopped`、`loading`、`playing`、`paused`、`buffering` |
| positionSeconds / durationSeconds | 实际播放器位置和时长；非负有限秒数，未知时为 0；拖动预览不是已提交定位 |
| title / artist / album | 当前歌曲公开元数据，每字段最多 2048 个 UTF-16 字符 |
| lyricsKind | `loading`、`timed`、`plain`、`empty`、`unavailable` |
| currentLineIndex / currentLine | 当前歌词行索引和文本；无实际当前行时索引为 -1，文本为空 |
| canPlay / canPause / canPrevious / canNext / canSeek | 当前播放能力说明，供显示使用；本接口没有控制命令 |

快照不包含服务器地址、完整文件路径、账号、凭据、源内部 ID、TrackReference 或私人封面 URL。公开歌曲标题等内容来自歌曲元数据，不做语义审查；协议不额外传输来源信息。匿名曲目代次在重启后不可作持久歌曲索引。

## 歌词分页

`lyrics` 返回当前曲目解析结果，读取只使用已发布的公共歌词；晚到且代次不匹配的旧歌词返回当前曲目的空页。

```json
{"version":1,"id":"request-2","ok":true,"type":"lyrics","data":{"trackGeneration":"匿名GUID","kind":"timed","offset":0,"totalLines":2,"nextOffset":null,"isLimited":false,"lines":[{"seconds":0,"text":"第一行"},{"seconds":2.5,"text":"第二行"}]},"error":null}
```

`offset` 为非负行索引，超过总行数时返回末尾空页；`limit` 为 1–64，默认 32。页按实际 JSON 序列化后的字节大小缩小，中文/控制字符转义可能使单页少于请求行数。消费者必须使用返回的 `nextOffset`，而不是自己加 `limit`；`nextOffset: null` 表示最后一页。

公共结果最多 2000 行，每行文本最多 2048 个 UTF-16 字符；`isLimited: true` 明确表示原歌词行数或文本曾被截断。`totalLines` 是保留的行数。`plain` 行的时间为 0，不应当作时间轴；`empty`/`loading`/`unavailable` 可无行。

后续页面建议携带首份结果的 `trackGeneration`：

```json
{"version":1,"command":"lyrics","offset":32,"limit":32,"trackGeneration":"匿名GUID"}
```

播放已经切到其他代次时返回 `trackChanged`，消费者应重新读取快照和第一页。读取歌词与随后一次快照之间仍可能切歌，消费者应持续按代次比对，不能假定多个请求构成事务。

## 订阅、背压与生命周期

`subscribe` 占用本连接，先发当前快照，再发变化；最长约每 5 秒发一次当前快照心跳。订阅后不继续读取请求，结束订阅请关闭连接，再以新连接查询歌词或其他快照。多个订阅可以同时存在。

每实例最多 4 个连接，包含空闲请求客户端和订阅客户端。没有空闲管道时新客户端需超时重试。每个订阅队列只保留 1 条最新快照，快速更新时允许跳过中间序号；播放事件仅写入有界队列，不执行管道序列化、等待、磁盘或客户端 I/O。单次响应写入超过 3 秒即断开慢客户端，并归还连接槽；快照心跳会检测已经断开的空闲订阅。

停止播放会产生停止状态；禁用接口会取消监听/订阅、关闭连接、移除当前实例记录。重新启用后需要重新发现并连接新管道。主窗口退出结束服务，歌词窗口关闭不等同于接口或播放停止。

## 独立消费者和集中回归

可直接构建/运行 [Melvune.InfoClient 示例](../native/examples/Melvune.InfoClient/README.md)，它没有 Core 或播放器引用，通过跨进程管道获取真实数据。分发目录附带已构建的消费者时可直接运行 `Melvune.InfoClient.exe list|snapshot|lyrics|subscribe`；隔离数据目录用 `--data-directory <目录>`，多实例用 `--instance <GUID>`。

专用集中回归项目 `native/tests/Melvune.Information.Tests` 包含独立消费者进程读取/订阅、错误/只读边界、非 ASCII 大歌词分页、纯文本/空歌词、代次隔离、异常/慢客户端、4 客户端上限、禁用/重启/多实例发现清理。它是独立测试项目，不修改现有测试入口，也不以同进程 hub 读取代替消费者验证。执行方式：

```powershell
dotnet run --project native/tests/Melvune.Information.Tests -c Release
```

源码测试通过和最终候选包实际接口验收应分别记录。本协议只承诺当前用户边界；没有将另一个 Windows 账号的实际连接测试、其他厂商消费者兼容性或强制退出发现清理写作通过。
