<p align="center">
  <img src="assets/branding/fnmusic.png" alt="FnMusic 图标" width="96" />
</p>

# FnMusic · 飞牛音乐 Windows 客户端

一个使用 C#、WinUI 3 和 FFmpeg 构建的非官方桌面音乐播放器，支持飞牛音乐与本地音乐。黑白界面，统一音乐库、歌单、播放队列和应用内歌词。

**本地候选版：0.2.0-rc.2；已公开发布：0.2.0-rc.1** · Windows 10 2004（19041）及以上 · x64 · [MIT 许可](LICENSE)

rc.2 新增 Windows 媒体控制、桌面歌词和可选本机只读信息接口，尚未上传 GitHub。入口和验证边界见 [rc.2 验收记录](docs/RC2_ACCEPTANCE.md)、[桌面联动指南](docs/DESKTOP_INTEGRATION.md) 与 [本轮清单](docs/NEXT_DEVELOPMENT_CHECKLIST.md)。

本轮推荐本地 ZIP：`dist/FnMusic-0.2.0-rc.2-win-x64-final.zip`，解压后运行 `Start-FnMusic.cmd`。便携 EXE、安装 EXE 和 MSI 位于 `dist/FnMusic-0.2.0-rc.2-win-x64-windows-final/`。最终包已完成实际本地/飞牛播放、桌面歌词交互、独立系统媒体控制与信息接口、关闭重启及隔离安装升级验收；哈希见验收记录。

本项目与飞牛官方无关联或背书。当前为候选版，发行文件尚未进行代码签名。

## 下载与安装

前往 [0.2.0-rc.1 Release](https://github.com/JuuWaii/fnmusic_pc/releases/tag/v0.2.0-rc.1) 下载。运行库与解码依赖随包提供，无需另装 .NET 或 FFmpeg。

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
- **两类歌单**：跨音乐库的本地歌单，以及当前飞牛账号的专属歌单管理。
- **播放与队列**：播放/暂停/停止、进度拖动、音量、输出设备、双击切歌和四种播放模式；队列从右侧打开。
- **封面与歌词**：飞牛封面、支持的内嵌封面，以及飞牛歌词、同名 LRC 和支持的内嵌歌词。
- **界面与主题**：黑白界面、浅色/深色主题和跟随系统，统一顶部导航与右键菜单。
- **飞牛管理入口**：管理员可管理支持的素材库与设置，查看后台任务；任务消失或身份变化时保留未确认状态。

## 开始使用

首次启动不需要账号，可先导入本地音乐。在“音乐库与素材库”中新建本地库，选择文件夹；单目录连同子目录最多扫描 10,000 首。索引保存后，重启会先显示缓存，再检查变化。缓存不会复制音频文件。

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

第三方音乐源、FN Connect/OAuth、托盘、全局快捷键和自动更新留待后续版本。rc.2 的信息接口需第三方按协议接入，不承诺自动兼容。实体媒体键、系统媒体卡片完整图形操作，以及全部编码、DPI、触摸、设备热插拔和长期压力组合尚未覆盖。

遇到问题可提交 [Issue](https://github.com/JuuWaii/fnmusic_pc/issues)，注明版本、复现步骤、预期与实际结果。附截图或日志前，请隐去密码、令牌、服务地址和私人路径；集中验证项目见 [rc.2 用户验证单](docs/RC2_USER_VALIDATION.md)（公开 rc.1 使用[原验证单](docs/RC1_USER_VALIDATION.md)）。

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
./scripts/package-preview.ps1 -Name FnMusic-0.2.0-rc.2-win-x64
./scripts/package-windows.ps1 -Name FnMusic-0.2.0-rc.2-win-x64-windows
```

脚本拒绝覆盖同名输出；已有产物时使用新名称，并用 `-Payload` 指向对应应用目录。辅助检查无需安装 npm 依赖：

```powershell
node --test tests/*.test.cjs
node scripts/check-privacy.js
```

[GitHub Actions](https://github.com/JuuWaii/fnmusic_pc/actions/workflows/windows-packages.yml) 支持手动构建；推送 `v0.2.0-rc.*` 标签时，在隐私、测试、包内容与图标校验通过后发布候选版 Release。

## 许可与项目资料

FnMusic 使用 [MIT 许可](LICENSE)。FFmpeg 使用 LGPL 2.1 或更高版本，完整对应源码、补丁和构建材料随包位于 `licenses/corresponding-source`；其他运行库与安装器许可也随包提供。

[第三方声明](THIRD_PARTY_NOTICES.md) · [源码构建与替换](docs/BUILDING_SOURCES.md) · [解码器说明](docs/NATIVE_DECODER_DEPENDENCIES.md) · [Windows 封装与验证](docs/WINDOWS_PACKAGING.md)

[当前开发清单](docs/NEXT_DEVELOPMENT_CHECKLIST.md) 是开发进度的统一入口；[rc.1 发布历史](docs/RELEASE_CHECKLIST.md) 和 [路线图](docs/PRODUCT_ROADMAP.md) 保留交付记录及后续计划。旧 Electron 客户端已归档，见 [归档说明](docs/ELECTRON_ARCHIVE.md)。
