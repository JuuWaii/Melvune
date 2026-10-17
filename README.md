<p align="center">
  <img src="assets/branding/melvune.png" alt="Melvune 图标" width="96" />
</p>

# Melvune · 合听

使用 C#、WinUI 3 和 FFmpeg 构建的多来源Windows桌面音乐播放器。统一浏览与播放本地音乐、飞牛音乐、Navidrome、Emby、Subsonic兼容源及匿名HTTP/JSON音乐源，采用黑白界面并跟随Windows主题。

**当前候选：0.2.0-rc.8，源码回归完成，本地封装中。** Windows 10 2004（19041）及以上，x64。当前进度以 [开发清单](docs/NEXT_DEVELOPMENT_CHECKLIST.md) 为唯一入口，结果记录于 [rc.8验收](docs/RC8_ACCEPTANCE.md)。

本轮将目录、namespace、工程、资源、构建与数据/协议身份整体统一为Melvune，不保留改名前版本的兼容回退。普通版使用 `%LOCALAPPDATA%/Melvune.Native`，便携版使用旁边 `Melvune-portable/Data`；新版需要重新添加音乐源和登录。旧个人数据和历史发布包保留原样。详见 [命名与数据说明](docs/MELVUNE_MIGRATION.md)。

## 使用与交付

当前候选提供ZIP与便携EXE。ZIP解压并保持完整目录，运行 `Start-Melvune.cmd` 或 `Melvune.App.exe`；便携EXE放在可写目录双击，首次校验并解包后启动。随包提供运行时和解码依赖，无需另装.NET或FFmpeg。候选未签名，完整安装器和自动更新后续实施。

本地rc.8文件和摘要待集中验收完成后列入清单。已公开的旧版本仍在 [历史Releases](https://github.com/JuuWaii/Melvune/releases)，其功能与配置身份不等同于当前源码。当前GitHub工作流仅构建并上传Actions产物，不自动创建Release。

首次启动可直接在“音乐管理 → 音乐库”添加本地目录。连接服务时填写HTTP/HTTPS地址并登录；会话由当前Windows用户的DPAPI保护，不保存明文密码。双击歌曲播放，右键可收藏、安排下一曲或加入歌单；点击左下角封面进入应用内歌词。

## 功能

- 多音乐库、分组与筛选；持久索引先恢复可播放目录，再在后台增量扫描。
- 本地跨库歌单与飞牛同步歌单，支持侧栏合并或按来源分类。
- Navidrome、Emby及Subsonic兼容连接独立管理，第三方服务端写入和转码后续添加。
- 匿名同源HTTP/JSON来源，支持受控分页、Range音频、封面和UTF-8歌词；不支持自定义脚本、账号认证或跨域CDN。
- 播放队列、顺序/随机/循环模式、下一曲安排；正常退出保存歌曲、进度、队列和最近历史，重启保持暂停。
- 封面、内嵌/外部歌词、应用内歌词页与右侧队列抽屉，浅色/深色/跟随系统主题。
- SMTC、透明桌面歌词与锁定穿透、托盘、可选全局快捷键、睡眠定时和输出设备监视。
- 可选本机只读播放信息/歌词接口，使用当前Windows用户命名管道；默认关闭，第三方需按协议适配。
- 飞牛管理员支持的素材库与非UI设置入口，按真实服务权限显示。

## 配置和限制

ZIP与便携配置互相独立。显式数据目录使用绝对路径：

```powershell
./Melvune.App.exe "--data-directory=D:\MelvuneProfile"
```

新版本不自动读取或转换改名前的账号、凭据、来源和信息接口记录，也不覆盖旧配置。更换Windows用户或机器后，加密会话需要重新登录。

飞牛网页修改歌单需刷新后读取，不承诺实时同步和并发编辑事务；素材库任务缺少完成计数时保持“未确认”。第三方来源当前只读，Subsonic兼容源按专辑目录读取，最多10000首。实体媒体键、完整系统媒体卡片操作、全部DPI/编码/设备热插拔和长期压力组合仍需用户验证。

本项目与飞牛官方无关联或背书。详细操作见 [使用指南](docs/USER_GUIDE.md)，真实服务与系统交互待验证项见 [用户验证单](docs/RC8_USER_VALIDATION.md)。

## 从源码构建

需要Windows x64、Git、Python 3.11+和Node.js，脚本在.tools中准备固定SDK与解码工具链。

```powershell
./scripts/native.ps1 bootstrap
./scripts/native.ps1 build
./scripts/native.ps1 test
./scripts/native.ps1 run
```

解决方案为 `native/Melvune.sln`，当前源码项目均位于Melvune命名目录；新的中间生成物位于obj/Melvune以隔离旧缓存。

```powershell
./scripts/package-preview.ps1 -Version 0.2.0-rc.8 -Name Melvune-0.2.0-rc.8-win-x64
./scripts/package-windows.ps1 -Payload dist/Melvune-0.2.0-rc.8-win-x64 -Name Melvune-0.2.0-rc.8-win-x64-portable -PortableOnly
```

两次封装必须来自同一已提交源码检查点；不覆盖已有产物。便携尾部、ZIP、逐文件摘要、产品、版本与源码来源均有一致性校验。

## 许可和资料

本项目使用 [MIT许可](LICENSE)。FFmpeg使用LGPL 2.1或更高版本，完整对应源码、补丁、构建与许可证材料随包提供。历史版权和原始第三方材料保留原文。

[第三方声明](THIRD_PARTY_NOTICES.md) · [源码与库替换](docs/BUILDING_SOURCES.md) · [Windows封装](docs/WINDOWS_PACKAGING.md) · [信息接口](docs/PLAYBACK_INFORMATION_PROTOCOL.md) · [路线图](docs/PRODUCT_ROADMAP.md)

问题反馈：[GitHub Issues](https://github.com/JuuWaii/Melvune/issues)。请注明版本及复现步骤，提交截图或日志前隐去凭据、服务地址和私人路径。
