# Navidrome 首版验收记录

日期：2026-10-13。范围为只读音乐源，可创建多连接、Windows DPAPI 加密保存、目录浏览与搜索、原始音频播放和定位、封面和结构化歌词、跨库本地歌单。服务端收藏/歌单写入、Emby、自定义源、独立专辑歌手页及完整安装器留到后续。

## 已检查证据

- App Debug 构建零警告、零错误；核心既有集中回归 120/120。
- `FnMusic.Tests --verify-navidrome` 五组专项通过：完整分页与跨源同 ID、认证/重定向/取消及错误脱敏、Range 播放与定位/封面/歌词 offset、原子保存回滚/身份约束/并发保存/删除、真实 DPAPI 加密与新仓库实例恢复。
- 独立 WinUI 窗口专项 20/20，报告 `.review-audit/navidrome-20261013/navidrome-window-final/navidrome-window-verification.json`：动态新建和编辑入口、密码留空重用、来源筛选、专辑搜索、真实静音原生播放和定位、XAML 封面与歌词、飞牛 reset 保留播放、普通目录错误保留当前流、混合本地歌单顺序、新实例来源恢复、删除确认/取消及离线引用。
- 桌面联动与音频输出集中窗口回归 152/152，报告 `.review-audit/navidrome-20261013/desktop-integration/desktop-verification.json`，桌面歌词、主题、SMTC、托盘、快捷键、定时与设备恢复仍通过。
- 首次窗口新建启用断言因 TextChanged/PasswordChanged 尚未完成失败，延迟观察后通过；未修改生产启用规则。审查发现普通 `Rejected` 目录错误不应撤销账号，已改为只在 `Unauthorized` 撤销；本地后台索引刷新现在遵守 Navidrome 筛选范围，均有专项覆盖。

## 验收边界

本轮未提供真实 Navidrome 服务。协议使用受控 HttpMessageHandler 响应，原生 WinUI/FFmpeg 播放、定位、封面解码及 DPAPI 为实际运行；这不证明真实服务器、TLS/反向代理、下载/流权限及 Range 行为全部兼容。用户验收入口见 [验证单](RC2_USER_VALIDATION.md)。歌词接口不存在时返回空结果，交给解码器尝试内嵌歌词，不按同名歌曲模糊匹配。

实现参考 [Navidrome Subsonic 兼容说明](https://www.navidrome.org/docs/developers/subsonic-api/)、[OpenSubsonic search3](https://opensubsonic.netlify.app/docs/endpoints/search3/) 与官方 Navidrome `server/subsonic` 源码。

## 交付

推荐独立 [Windows x64 ZIP](../dist/FnMusic-0.2.0-rc.2-win-x64-navidrome-20261013.zip)，及 [SHA-256](../dist/FnMusic-0.2.0-rc.2-win-x64-navidrome-20261013.SHA256SUMS.txt)。程序版本仍为 rc.2，完整安装器尚未重新打包。最终包构建和启动核验结果将在打包后补入此处。
