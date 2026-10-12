# 音频输出可靠性批次验收

日期：2026-10-12。本轮按 [活动开发清单](NEXT_DEVELOPMENT_CHECKLIST.md) 完成设备监视、断开回退、刷新/选择竞态修复与共用提示；继续使用 rc.2 独立 ZIP，保留旧包。

## 行为

- 原生设备监听等待初始完整枚举，之后处理增删更新与默认输出变更；主线程 300 毫秒去抖，通知立即使旧刷新失效。收起到托盘仍继续监听，真正退出停止并撤销静态事件。
- 完整枚举确认显式设备不存在时尝试回退系统默认；默认设备路由由 Windows 处理，不修改系统设置。刷新与回退不重开歌曲，不清空队列，不强制续播。
- 提交列表时抑制选择事件，同 ID 不写入播放器；用户选择及更新请求使用版本隔离。切换失败恢复最近确认的实际选择，读取异常时明确显示无法确认，不误报成功。
- 设置与播放栏设备菜单共用状态。自动监视启动失败可手动刷新重试；若监视器运行中意外停止，下次打开菜单/手动刷新重新启动。持续通知期间等待安静后刷新，不循环重试异常。

平台依据：[DeviceWatcher 生命周期](https://learn.microsoft.com/en-us/uwp/api/windows.devices.enumeration.devicewatcher?view=winrt-26100)、[默认输出变更事件](https://learn.microsoft.com/en-us/uwp/api/windows.media.devices.mediadevice.defaultaudiorenderdevicechanged?view=winrt-26100)、[MediaPlayer 输出选择](https://learn.microsoft.com/en-us/uwp/api/windows.media.playback.mediaplayer.audiodevice?view=winrt-26100)。

## 集中检查

- Debug 构建：0 警告、0 错误。只读审查发现异常处理再次读取设备可能逃逸，已改用安全读取并加入连续读取失败/写成功读取失败检查。
- 首次窗口回归在旧 `shell_resume_reuses_player` 断言失败：原生状态已 Playing，但异步快照通知尚未更新。测试等待两个状态完成后再断言，未改变生产播放行为。原失败报告保留在 `.review-audit/audio-output-20261012/first-window-regression.json`。
- 最终窗口集中回归 152/152 通过，包含原 120 项以及 32 项输出设备检查：请求竞态、连续读取失败、写成功读取失败、断开/失败回退、无设备、重新连接不抢回、20 次通知合并一次、在途结果失效、暂停/队列/时钟保持、原生枚举与切换、收起期间监视及真实关闭路径资源清理。报告 `.review-audit/audio-output-20261012/consolidated-final/desktop-verification.json`。
- 合成静音窗口实际双击歌曲、暂停、打开设备菜单、选择原生扬声器、点击刷新通过；暂停位置保持在约 23.36 秒，所选端点保持，浮层状态提示正确，关闭后独立测试进程退出。
- 独立静音真实播放专项 8/8 通过：播放时间推进、暂停/恢复、定位、原生设备枚举、显式输出选择、回系统默认后继续推进及停止。报告 `.review-audit/audio-output-20261012/native-playback-result.json`。
- 隐私扫描 332 个文件通过；用户已有 bug/logo 删除和 img 新文件不纳入提交。

## 边界

设备消失、回退失败、连续读取异常、并发枚举与事件风暴使用受控夹具；关闭检查调用真实清理路径并释放迟到的夹具枚举，不冒充实际取消 WinRT 在途枚举。真实原生枚举/切换使用当前可用设备并静音。本轮不改变系统设备、断开用户硬件或操作驱动；USB/蓝牙拔插、全部端点断开、异常驱动、实际听音和无声隙恢复仍在用户验收单中。

## 交付

- 推荐：[FnMusic-0.2.0-rc.2-win-x64-audio-output-20261012.zip](../dist/FnMusic-0.2.0-rc.2-win-x64-audio-output-20261012.zip)。解压后运行 `FnMusic.App.exe` 或 `Start-FnMusic.cmd`。
- 源码修订：`fcf20f030825187e90122805fd456cef1318be24`；仍为 `0.2.0-rc.2`，使用独立批次目录。
- ZIP SHA-256：`94138F8F7E91CA6642CA8C47DB73D5E8E0E38ECDDDBAFBCCCC3D23E9834EF0C5`，附 [校验文件](../dist/FnMusic-0.2.0-rc.2-win-x64-audio-output-20261012.SHA256SUMS.txt)。593 个文件哈希匹配，总计 594 个文件（含清单）；许可、对应源码、独立示例消费者及使用/验收说明随包附带。
- 最终 Release 包以独立空配置实际启动，打开输出浮层、检查自动监视提示、点击刷新且保留默认输出、默认关闭退出均通过；测试进程已结束。记录 `.review-audit/audio-output-20261012/package-smoke-result.json`。
- T01–T06 已完成。旧产物及无关用户修改保持；不上传、推送、打标签或重新封装完整安装器。
