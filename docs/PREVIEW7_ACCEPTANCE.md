# preview.7 构建与验收状态

2026-10-03。固定目录 `dist/FnMusic-0.2.0-preview.7-win-x64/`，源码提交 `ef0ad0a13bc8cfdf8b2f9eb8831a288d721d788e`，ProductVersion 为 `0.2.0-preview.7+ef0ad0a13bc8cfdf8b2f9eb8831a288d721d788e`。旧固定 preview.6 保留。本版已经构建；实际鼠标/播放验收尚未完成，不等于全部重点完成或正式发行。

## 已确认

- 锁定依赖还原和 Release Publish 通过，无编译警告/错误；551 个清单文件及清单本身共 552 文件。
- `scripts/check-preview.ps1` 逐文件核对 SHA-256，拒绝清单路径逃逸、重复项、额外文件与开发/个人配置；程序版本、XAML 资源和解码依赖齐全。启动前及启动后检查均通过，目录未被运行修改。
- 补丁 DLL、补丁、工具链锁及构建脚本与 `native-decoder-build.json` 哈希一致；DLL 为 MP3 预滚版本 2.1.0.1，SHA-256 为 `3DBE6C7083A78C8129BD994C5499385098645235A5E0B23B5F0E6E7C1585B803`。包内包含桥接许可、FFmpeg 依赖许可与补丁构建材料；完整源码分发与安装升级审查仍保留。
- 仅保留 System32 的子进程 PATH 后启动，进程存在、主窗口标题正确且保持响应。实际进程模块中的 coreclr.dll 与 Microsoft.UI.Xaml.dll 位于本包目录。这只证明启动/框架加载，不代表已确认解码模块加载或播放。
- 校验器用五个隔离小目录验证拒绝路径逃逸、重复条目、内容变化、未列出的额外文件及 connection.json；全部按预期失败，不修改固定包。
- 当前代码基础检查 Debug 85 项、Release 84 项通过，差异是仅 Debug 的文件诊断；业务窗口 181 项、四布局定位内容 80 项已有对应源码阶段报告。

## 尚未确认

窗口自动化两次尝试均返回 `failed to activate captured window`，之后停止本轮窗口输入，没有将其解释为应用崩溃或交互通过。新验证进程保留，供用户查看。包内实际播放、封面/歌词、暂停/跳转/停止、明暗切换及正常退出需继续验证；旧 preview.6 的运行记录不能代替。

账号真实切换/重启仍需要用户手动运行 `scripts/verify-native-accounts.ps1` 的独立配置验证，密码隐藏输入；此前携带凭据的自动启动被审查拒绝，不换工具重试。该验证即使通过也不代替所有登录界面鼠标验收。

NAS 新的重关联任务在约 1 秒后消失，最新增强探针未捕获完成计数；歌曲 GUID、完整音频哈希及歌单引用保持已分别验证，边界见 NATIVE_NAS_SETTINGS.md。服务端临时资源清理通过，但六份 NAS 测试副本的删除被自动审批策略阻止，副本仍保留。新 UI 整合、更多系统/网络/格式矩阵及正式安装发行继续按开发清单推进。

本地记录：`.review-audit/preview7-process.json`、`.review-audit/preview7-integrity-guards.json`。这些运行记录、浏览器资料、个人配置和原始测试媒体未进入固定包或版本库。
