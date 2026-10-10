# Windows 系统媒体控制（rc.2）

`SystemMediaBridge` 使用 `SystemMediaTransportControlsInterop.GetForWindow` 获取主窗口 HWND 的系统媒体控制会话，播放器仍为唯一的 `NativeMusicPlayer.Player`。无包桌面程序没有 UWP 的 CoreWindow，不能依赖 `MediaPlayer.SystemMediaTransportControls` 入口。`CommandManager.IsEnabled=false` 防止 Windows 的自动控制和应用命令重复执行。Windows 按钮、媒体键和系统定位请求通过窗口 `DispatcherQueue` 进入主窗口原有播放命令；上一首、下一首及播放模式仍由应用队列决定。

桥接使用 `PlaybackSnapshotHub` 的实际状态、位置、时长和按钮能力，不读取界面状态文字，也不发布拖动预览位置。系统时间轴约每五秒刷新，切歌、播放状态/时长/定位能力变化及位置跳变会立即刷新。系统定位值必须有限，并被限制在当前时长内；已排队的旧曲目代次请求会丢弃。

元数据只包含标题、歌手和专辑。缩略图接收主窗口提供的本地图片字节，单次上限 8 MiB，转换为 `InMemoryRandomAccessStream` 后交给系统；无 NAS URL、认证头或路径。流保留到替换封面、切歌、停止或退出。旧代次和同曲旧封面请求不能覆盖新封面。停止清空系统显示，退出解除事件并关闭会话；主窗口必须先销毁桥接，再销毁播放器。

集成 API：

```csharp
new SystemMediaBridge(player, windowHandle, dispatcher, hub, executePlaybackCommand);
await bridge.UpdateArtworkAsync(trackGeneration, imageBytes);
bridge.Dispose();
```

`LastError` 仅提供固定故障代码，不能包含异常正文或来源数据。系统展示失败不应中断播放。

集中 Debug 回归入口为 `await SystemMediaBridgeVerification.RunAsync(DispatcherQueue, windowHandle)`，检查真实 Windows API 的状态、按钮、元数据、封面代次隔离、大小限制、停止和退出清理。窗口句柄来自 `WinRT.Interop.WindowNative.GetWindowHandle(this)`。该回归使用主窗口会话和无音频的临时播放器，应在最终本应用 SMTC 会话的人工按钮验收前执行。

实际系统交互还需对本应用验证：播放/暂停媒体键、上一首/下一首遵循队列、系统定位与实际播放同步、换歌后的元数据与封面、停止/退出清理。该调试回归不能替代真实系统按钮或媒体键测试；未执行的项目必须保留为未验证。

Windows API 行为参考：[桌面 HWND 互操作入口](https://learn.microsoft.com/en-us/windows/win32/api/systemmediatransportcontrolsinterop/nf-systemmediatransportcontrolsinterop-isystemmediatransportcontrolsinterop-getforwindow)、[微软 SMTC 手动控制文档](https://learn.microsoft.com/en-us/windows/apps/develop/media-playback/system-media-transport-controls)、[系统定位事件](https://learn.microsoft.com/en-us/uwp/api/windows.media.systemmediatransportcontrols.playbackpositionchangerequested)、[从本地流创建缩略图引用](https://learn.microsoft.com/en-us/uwp/api/windows.storage.streams.randomaccessstreamreference.createfromstream)。
