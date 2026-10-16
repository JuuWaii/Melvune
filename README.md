<p align="center">
  <img src="assets/branding/fnmusic.png" alt="FnMusic 图标" width="96" />
</p>

# FnMusic · 飞牛音乐 Windows 客户端

一个使用 C#、WinUI 3 和 FFmpeg 构建的非官方桌面音乐播放器，支持飞牛音乐、本地音乐，以及 Navidrome、Emby、Subsonic 兼容和匿名 HTTP/JSON 只读音乐源。黑白界面，统一音乐库、歌单、播放队列和应用内歌词。

**当前本地交付：0.2.0-rc.6；已公开发布：0.2.0-rc.1** · Windows 10 2004（19041）及以上 · x64 · [MIT 许可](LICENSE)

rc.7 已完成黑白歌单背景标志、可切换分类/合并、侧栏底部固定管理入口，以及最多四路后台标签读取；集中回归已通过，正在生成交付包。开发状态见 [本轮清单](docs/NEXT_DEVELOPMENT_CHECKLIST.md)，证据见 [rc.7 验收](docs/RC7_ACCEPTANCE.md)。rc.6旧包保留，完整安装器和远端发布另行安排。

本地下载：[rc.6 ZIP](dist/FnMusic-0.2.0-rc.6-win-x64.zip)，或 [rc.6 便携 EXE](dist/FnMusic-0.2.0-rc.6-win-x64-portable/FnMusic-0.2.0-rc.6-win-x64-portable.exe)。摘要和实际运行记录见 [rc.6 验收](docs/RC6_ACCEPTANCE.md)。旧包保留用于回退，真实服务/听音等边界见 [用户验证单](docs/RC6_USER_VALIDATION.md)。

本项目与飞牛官方无关联或背书。当前为候选版，发行文件尚未进行代码签名。

## 下载与安装

本地交付入口及状态见 [开发清单](docs/NEXT_DEVELOPMENT_CHECKLIST.md)：ZIP 解压后运行 `Start-FnMusic.cmd`；便携 EXE 放在可写目录双击启动。运行库与解码依赖随包提供，无需另装 .NET 或 FFmpeg。ZIP 与便携 EXE 选择一种即可。

以下为已公开的 [0.2.0-rc.1 Release](https://github.com/JuuWaii/fnmusic_pc/releases/tag/v0.2.0-rc.1)，功能早于本轮 rc.6，不包含其新增能力：

| 版本 | 下载 | 使用方式 |
| --- | --- | --- |
| EXE 安装包 | [setup.exe](https://github.com/JuuWaii/fnmusic_pc/releases/download/v0.2.0-rc.1/FnMusic-0.2.0-rc.1-win-x64-setup.exe) | 常规安装入口；通过 **Options → Browse** 选择目录 |
| MSI 安装包 | [setup.msi](https://github.com/JuuWaii/fnmusic_pc/releases/download/v0.2.0-rc.1/FnMusic-0.2.0-rc.1-win-x64-setup.msi) | Windows Installer，提供中文目录选择页 |
| 便携 EXE | [portable.exe](https://github.com/JuuWaii/fnmusic_pc/releases/download/v0.2.0-rc.1/FnMusic-0.2.0-rc.1-win-x64-portable.exe) | 放在可写目录启动，首次运行在旁边解包 |
| 完整 ZIP | [win-x64.zip](https://github.com/JuuWaii/fnmusic_pc/releases/download/v0.2.0-rc.1/FnMusic-0.2.0-rc.1-win-x64.zip) | 解压并保留完整目录，运行 `Start-FnMusic.cmd` 或 `FnMusic.App.exe` |

EXE 与 MSI 安装包选择一种即可，默认按当前用户安装到 `%LOCALAPPDATA%\Programs\FnMusic`，建立开始菜单快捷方式。自选目录会保存，供维修和卸载恢复；卸载保留独立的个人配置。

便携 EXE 会创建 `FnMusic-portable` 目录，应用文件与个人数据分别保存在其中。首次解包请预留约 700 MB 空间。移动时一起复制 EXE 和该目录；换机器或 Windows 用户后，加密会话需要重新登录。更新便携 EXE 前退出播放器，保留已有 `FnMusic-portable\Data`。

Release 同时提供 SHA-256 校验文件与构建来源信息。请下载 Release 附件中的完整 ZIP；GitHub 自动生成的 **Source code** 是源码，不含可直接运行的应用。

## 功能

- **飞牛与本地音乐**：连接飞牛音乐服务、登录与加密会话恢复，浏览音乐、收藏、搜索、专辑和歌手。
- **音乐库管理**：多个本地库、来源/分组筛选、启动索引恢复、后台扫描与取消，离线目录保留记录。
- **第三方只读来源**：Navidrome、Emby 与 Subsonic 兼容连接分别管理，支持目录、搜索、原始音频与封面；歌词依服务接口或音频内嵌信息提供。服务端收藏/歌单写入及转码后续添加。
- **自定义 HTTP/JSON 来源（rc.4）**：声明式字段方案与受控分页接入匿名同源目录，支持原始 Range 音频、封面、UTF-8 LRC/纯文本歌词和跨库本地歌单；配置示例见 [来源说明](docs/HTTP_JSON_SOURCES.md)。
- **统一歌单管理**：本地跨库歌单和当前飞牛账号同步歌单集中管理；侧栏默认合并全部歌单，可在音乐管理或外观设置中切换飞牛/其他分类。
- **播放与队列**：播放/暂停/停止、进度拖动、音量、输出设备、双击切歌和四种播放模式；队列从右侧打开。
- **完整队列恢复（rc.5）**：正常退出保存队列顺序、当前歌曲及位置、模式、下一曲安排和最近历史；重启保持暂停，离线引用保留。详细规则见 [队列恢复](docs/QUEUE_SESSION.md)。
- **封面与歌词**：飞牛封面、支持的内嵌封面，以及飞牛歌词、同名 LRC 和支持的内嵌歌词。
- **界面与主题**：黑白界面、浅色/深色主题和跟随系统，统一顶部导航与右键菜单。
- **飞牛管理入口**：管理员可管理支持的素材库与设置，查看后台任务；任务消失或身份变化时保留未确认状态。
- **Windows 桌面联动**：SMTC 开关、透明桌面歌词及锁定穿透、托盘解锁、可选全局快捷键与睡眠定时；输出设备自动监视，可选本机只读音乐/歌词信息接口。

## 开始使用

首次启动不需要账号，可先导入本地音乐。在“音乐管理 → 音乐库”中新建本地库，选择文件夹；单目录连同子目录最多扫描 10,000 首。索引保存后，重启会先显示缓存，再检查变化。缓存不会复制音频文件。

连接飞牛时，打开“设置 → 连接与账号”，填写音乐服务的最终 HTTP/HTTPS 地址（可带 `/music/`），连接后登录。会话由当前 Windows 用户的 DPAPI 加密保存，不保存明文密码。

双击歌曲播放，右键可收藏、下一首或加入歌单；点击左下角封面进入应用内歌词。详细操作见 [使用指南](docs/USER_GUIDE.md)。

## 配置与升级

安装版和普通 ZIP 版默认配置位于 `%LOCALAPPDATA%\FnMusic.Native`；便携 EXE 使用旁边的 `FnMusic-portable\Data`。不同入口的配置不会自动互相迁移。

升级前退出旧客户端并备份配置目录，避免同时运行两版写入同一配置。本地歌单使用 v2 格式，preview.5 及更早版本不能读取；回退时同时恢复备份。

需要独立配置时，可直接运行应用并传入绝对目录：

```powershell
./FnMusic.App.exe "--data-directory=D:\FNMusicProfile"
```

## 已知限制与反馈

MP3 远距离首次定位可能读取较多数据；素材库重关联缺少完成计数时仍显示“未确认”。飞牛歌单的网页修改需要显式刷新，不承诺实时同步或并发编辑事务。

第三方来源目前只读。Subsonic 兼容源按专辑目录读取，最多 10,000 首，不保证未归入专辑的曲目；原始播放需要服务支持 HTTP Range。真实部署兼容性、实体媒体键、系统媒体卡片完整图形操作，以及全部编码、DPI、触摸、设备热插拔和长期压力组合尚未覆盖。

自定义 HTTP/JSON 首版最多 10000 首，字段方案仅访问对象成员；媒体必须同源且无查询参数，不跟随重定向，不支持账号认证、Cookie、自定义请求头、脚本转换和跨域 CDN。认证式自定义源、第三方服务端写入、转码、完整队列恢复、自定义快捷键、FN Connect/OAuth、自动更新和本轮完整安装器留待后续。只读信息接口需第三方按协议接入，不承诺自动兼容。

遇到问题可提交 [Issue](https://github.com/JuuWaii/fnmusic_pc/issues)，注明版本、复现步骤、预期与实际结果。附截图或日志前，请隐去密码、令牌、服务地址和私人路径；集中验证项目见 [rc.5 用户验证单](docs/RC5_USER_VALIDATION.md)（公开 rc.1 使用[原验证单](docs/RC1_USER_VALIDATION.md)）。

## 从源码构建

需要 Windows x64、Git、Python 3.11+ 和 Node.js。脚本在项目的 `.tools` 目录准备固定版本的 .NET SDK 与原生解码工具链；首次构建需要网络。

```powershell
./scripts/native.ps1 bootstrap
./scripts/native.ps1 build
./scripts/native.ps1 test
./scripts/native.ps1 run
```

构建发行包：

```powershell
./scripts/package-preview.ps1 -Version 0.2.0-rc.5 -Name FnMusic-0.2.0-rc.5-win-x64
./scripts/package-windows.ps1 -Payload 'dist/FnMusic-0.2.0-rc.5-win-x64' -Name FnMusic-0.2.0-rc.5-win-x64-portable -PortableOnly
```

脚本拒绝覆盖同名输出；已有产物时使用新名称，并用 `-Payload` 指向对应应用目录。辅助检查无需安装 npm 依赖：

```powershell
node --test tests/*.test.cjs
node scripts/check-privacy.js
```

[GitHub Actions](https://github.com/JuuWaii/fnmusic_pc/actions/workflows/windows-packages.yml) 的候选构建流程生成 ZIP 与便携 EXE，并检查版本、隐私、摘要和图标；只上传构建产物，不自动创建 Release。本轮没有运行远端流程；公开发布另行安排，历史标签保留当时的发布配置。

## 许可与项目资料

FnMusic 使用 [MIT 许可](LICENSE)。FFmpeg 使用 LGPL 2.1 或更高版本，完整对应源码、补丁和构建材料随包位于 `licenses/corresponding-source`；其他运行库与安装器许可也随包提供。

[第三方声明](THIRD_PARTY_NOTICES.md) · [源码构建与替换](docs/BUILDING_SOURCES.md) · [解码器说明](docs/NATIVE_DECODER_DEPENDENCIES.md) · [Windows 封装与验证](docs/WINDOWS_PACKAGING.md)

[当前开发清单](docs/NEXT_DEVELOPMENT_CHECKLIST.md) 是开发进度的统一入口；[rc.1 发布历史](docs/RELEASE_CHECKLIST.md) 和 [路线图](docs/PRODUCT_ROADMAP.md) 保留交付记录及后续计划。旧 Electron 客户端已归档，见 [归档说明](docs/ELECTRON_ARCHIVE.md)。
