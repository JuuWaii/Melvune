# rc.1 Windows 安装与便携 EXE

本轮在已交付的 rc.1 ZIP 上增加三种 Windows x64 产物。应用仍为
`0.2.0-rc.1`，不是稳定版；原 ZIP 和 preview.14 不覆盖。系统要求为
Windows 10 2004 / build 19041 或更高版本、x64。

推荐输出目录：`dist/FnMusic-0.2.0-rc.1-win-x64-windows/`。

| 文件 | 使用方式 |
| --- | --- |
| FnMusic-0.2.0-rc.1-win-x64-portable.exe | 单文件便携入口；首次启动解压到 EXE 旁边的 FnMusic-portable，随后启动播放器 |
| FnMusic-0.2.0-rc.1-win-x64-setup.exe | 常规图形安装入口，内嵌完整 MSI，不需要在线下载应用依赖 |
| FnMusic-0.2.0-rc.1-win-x64-setup.msi | Windows Installer 安装包；和 EXE 安装器部署相同内容，选择一种安装入口即可 |
| SHA256SUMS.txt / release.json | 文件哈希、原 ZIP 哈希、应用源码与封装源码提交、工具版本和签名状态 |
| WIX_LICENSE.txt / WIX_NOTICE.md | 安装器许可与来源说明；也安装在应用的 licenses/installer 下 |

便携版需要 EXE 所在目录可写，首次启动需约 700 MB 可用空间（EXE、
临时 ZIP、解压目录同时存在）。应用文件位于 `FnMusic-portable/App-<哈希前缀>/`，
个人配置位于 `FnMusic-portable/Data/`。跨机器移动时一起复制 EXE 与该目录；
DPAPI 加密会话绑定 Windows 用户/机器，换机器后需要重新登录。不要把
含 Data 的使用中便携目录作为公开发布包。更新启动器复用同一 Data；
退出播放器后可以清理旧 App 缓存，保留 Data。运行修改过的解码 DLL 不受阻止。
启动器的自包含 .NET 运行库另由 .NET 解压到用户临时目录。

安装版按当前用户安装至 `%LOCALAPPDATA%/Programs/FnMusic`，建立开始菜单
快捷方式并在系统应用列表注册。配置仍在 `%LOCALAPPDATA%/FnMusic.Native`，
安装器未声明该目录为安装组件，卸载设计为保留它。安装、卸载的动态实测边界
见下文；从 EXE 安装的用户应从系统应用列表对应项卸载，避免混用 MSI 维护入口。

MSI 只能使用数值版本，并且升级比较不使用第四段。rc.1 的内部安装版本固定为
`0.1.999`（EXE 为 `0.1.999.0`），留出未来稳定 `0.2.0` 的升级顺序。
安装产品名和播放器显示仍为 `0.2.0-rc.1`。后续版本须更新产品代码及数字版本，
保持两个 UpgradeCode 不变，并单独验收升级。

三个产物均未进行 Authenticode 签名；本轮没有使用或上传签名私钥。

## 本机构建

Python 3.11+、Git 和 Node 用于构建材料及隐私检查；SDK、NuGet 依赖和固定
WiX Toolset 4.0.6 存于项目工具目录。首次构建需要网络。

```powershell
./scripts/native.ps1 bootstrap
./scripts/package-preview.ps1 -Name FnMusic-0.2.0-rc.1-win-x64
./scripts/package-windows.ps1 -Name FnMusic-0.2.0-rc.1-win-x64-windows
```

如果原 ZIP 已存在并通过校验，跳过第二步。两个脚本都拒绝覆盖同名输出。
封装脚本检查应用目录、ZIP 外部校验和以及锁定依赖；便携启动器校验嵌入
ZIP 的 SHA-256、拒绝越界/符号链接条目，使用暂存目录完成后再切换。
`--extract-only=<空的绝对路径>` 只解包，供验证使用，不启动播放器。

## 验证记录与限制

本轮便携启动器编译成功，实际只解包测试通过原应用的 588 文件校验。
最终包进一步验证嵌入 ZIP 的 CRC、全部文件哈希、MSI 的 590 个文件
（原包 588 + 安装器许可 2）、EXE 内嵌 MSI 与独立 MSI 的一致性，及全部
发布文件的外部 SHA-256。MSI/EXE 声明为 per-user；MSI 没有自定义安装动作、
服务或机器级注册表写入。Windows 系统版本检查使用注册表构建号，避免
Windows Installer 的兼容属性误判新系统。

首个 MSI 安装实测返回 1603，原因是上述系统版本条件，已修复。
后续安装/卸载实测和便携入口的图形启动验证被自动审批审核拒绝，未提供
具体原因；没有通过其他方式重试被拒绝的动作。最终记录不把静态校验
称为安装/卸载通过。原 rc.1 应用的真实启动/播放结果仍见
[原包交付记录](RC1_ACCEPTANCE.md)，封装不会改变应用文件。

## GitHub 构建

`.github/workflows/windows-packages.yml` 提供手动 Windows 构建，固定 Actions
提交、SDK、NuGet 锁和 WiX 版本；隐私检查及核心/脚本测试后构建 ZIP/EXE/MSI，
只上传发行产物为工作流 artifact，保留 14 天，不自动创建 Release。
当前工作流还未推送或在 GitHub 执行，不能视为云端构建已通过。

推送前检查见 [GitHub 隐私审查](GITHUB_PRIVACY_REVIEW.md)。
