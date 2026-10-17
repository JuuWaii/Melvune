# Windows 预览与发行封装

## 日常开发

只在需要运行修改后的应用时执行 `./scripts/preview.ps1 -Run`，生成独立 Debug 可运行目录。它不压缩 ZIP、不准备发行源码材料、不构建便携入口或安装器。增量复用 SDK、解码器与依赖缓存，预览运行使用独立开发配置，位于不会随预览清理的 `.review-audit/development-profile`。

目录为 `dist/previews/<UTC时间>-<随机ID>`。`python scripts/clean-previews.py` 默认列出清理候选，`--apply` 执行；只删除有效专用标记、完成生成且超过7天的旧预览，保留最近3份及所有7天内产物。检测到 Melvune 播放器运行时不清理。未知、未完成、时间不符或包含链接的目录不删除；不处理历史 dist、验证证据或个人资料。每日10点的任务只运行该清理工具。

## 最终发布

完成一批修改和必要检查后，提交并推送发布标签。普通分支推送不自动构建完整包；当前 rc.9 标签的 Actions 工作流统一构建四种入口。手动工作流仅用于明确需要的最终候选打包。

```powershell
./scripts/package-release.ps1 -Version 0.2.0-rc.9 -Name Melvune-0.2.0-rc.9-win-x64
./scripts/package-windows.ps1 -Payload dist/Melvune-0.2.0-rc.9-win-x64 -Name Melvune-0.2.0-rc.9-win-x64-portable
```

第二条默认生成便携 EXE，以及旁边 `-installers` 目录中的安装 EXE/MSI；明确只需便携时使用 `-PortableOnly`。所有产物来自同一已提交源码，封装期间不插入提交，不覆盖已有输出。

安装器使用固定 WiX 4.0.6，独立 Melvune MSI/Burn UpgradeCode 和当前用户 HKCU 记录。默认安装至 `%LOCALAPPDATA%\Programs\Melvune`，EXE Options/Browse 与 MSI 中文页面均可选择可写目录。安装路径保存供维护/卸载恢复，卸载保留独立的 Melvune.Native 配置。不会自动升级或移除旧品牌应用。内部 MSI 版本0.2.1009，应用版本0.2.0-rc.9。

发行检查覆盖完整 ZIP 和便携载荷、MSI 逐文件摘要、EXE 内嵌 MSI、目录选择与用户范围、三个 EXE 的七档图标。云端实际执行 MSI 自选目录、EXE 默认和自选目录的静默安装/卸载，检查开始菜单、安装文件及配置保留。图形安装、SmartScreen、跨版本升级与全部系统组合仍需用户验证。当前全部入口未签名，代码签名与自动更新后置。

许可证、FFmpeg 对应源码和 AI 声明随应用载荷提供，WiX 许可额外安装于 `licenses/installer`。公开 Release 的 SHA256SUMS 与 release.json 为该次云端构建的交付基准，详见 [发布流程](RELEASING.md)。
