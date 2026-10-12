# rc.2 集中验收与本地交付

源码回归：2026-10-10；最终封装和安装验收：2026-10-12。应用版本 `0.2.0-rc.2`，内部安装版本 `0.1.1002`。
本次只交付本地 Windows x64 候选产物，不上传或覆盖公开 rc.1。

## 源码批次

- Debug / Release 构建：0 警告、0 错误。
- Core：119 项通过，含快照身份/文本边界及多时间戳歌词膨胀限制。
- 新集中窗口回归：29 项通过。实际音频来自隔离目录的 WAV；实际播放器、暂停、定位、切歌、plain/empty/timed 歌词、迟到提交、错误代次命令、飞牛 ResetLibrary 后本地播放保持、歌词隐藏重开和服务启停已覆盖。
- SMTC 真实 HWND API：元数据、封面、播放状态、能力、时间轴设置、停止/释放及迟到封面隔离通过。无包桌面需要 `GetForWindow`；清空后类型为 Unknown，不能再读取 MusicProperties。
- 信息接口：9 组通过。包含独立进程消费者读取/订阅、只读与版本边界、UTF-8/超量请求、有界分页、4 个慢客户端及超时恢复、多实例记录归属、启停和 Network SID Deny ACL。不是同进程内部读取代替接口验收。
- Node 测试：24/24；Python 发布测试：9/9；源码隐私扫描通过。

本机详细结果在忽略目录 `.review-audit/rc2/`，不随包分发。

## 实际交互边界

computer-use 已读取实际主窗口截图和可访问树；尝试关闭窗口时报告 `failed to activate captured window`，按技能重新读取后重试又报告 `window bounds changed; call get_window_state before using this window`。未绕过工具限制进行鼠标自动化。

因此鼠标拖动、系统媒体卡片/实体媒体键、更多显示器/DPI变化以及安装器目录选择的完整图形流程仍**未验证**。窗口回归通过 API 操作验证状态和生命周期，不冒充这些人工交互。用户应按 [一页验证单](RC2_USER_VALIDATION.md) 检查。

本轮未重新打开私人 NAS 账号或传递测试凭据，不扩大旧账号/编码/规模矩阵。最终包的真实飞牛播放尚待用户验证；保留 rc.1 已有服务证据，不将它计作 rc.2 最终包通过。程序/安装器未签名。

## 产物与最终包

源码及封装提交：`283c9f0c93b5ced15b8e51b645f64c6eee425fb7`。唯一推荐便携目录为
`dist/FnMusic-0.2.0-rc.2-win-x64/`，运行 `FnMusic.App.exe` 或 `Start-FnMusic.cmd`；
对应 ZIP 为 `dist/FnMusic-0.2.0-rc.2-win-x64.zip`。其他入口在
`dist/FnMusic-0.2.0-rc.2-win-x64-windows/`，选择便携 EXE、setup EXE 或 MSI。
新包不覆盖 rc.1；所有入口未签名。安装包与便携目录含独立自包含消费者，不需要另装 .NET。

| 文件 | 字节 | SHA-256 |
| --- | ---: | --- |
| FnMusic-0.2.0-rc.2-win-x64.zip | 239742474 | 17922589D4E286985B1583891D1E417B97ACBC37087C43E272C59EA6489972B6 |
| portable.exe | 277511808 | 3DB7947E1F7C75112413BDA71DCC1B390C889CD15FBB987A798E9EFABBC3D68E |
| setup.exe | 209717485 | 8D29174D31069880E3720D3A1562B01C540424260D3963F1016DA0B925DC9D9B |
| setup.msi | 208554579 | 060273B6B30AFF40A505180BD7E535988E4263D4C02C29DE04A3161FF1E8230B |

2026-10-12 的实际通过项：

- ZIP/目录 594 文件完整校验；便携 EXE 实际仅解包退出码 0，同样 594 文件一致。
- MSI 596 文件逐项与应用及安装器许可一致；setup EXE 内嵌 MSI 与独立 MSI 一致。
- 全部发行外部 SHA-256 一致；应用/便携 EXE/setup EXE 图标七种尺寸逐字节一致。
- 安装器 per-user，无执行序列自定义动作/服务/机器级注册表组件；目录选择结构检查通过。
- 在包含中文、空格和方括号的隔离目录中，实际安装 rc.1 `0.1.1001` 后升级至 rc.2 `0.1.1002`，保留自选目录、移除旧产品，596 文件一致。
- MSI 卸载及 setup EXE 安装/卸载全部实际完成；安装目录及登记移除，当前用户个人配置的整体哈希不变。没有清理旧私人配置或历史残留。
- 最终 ZIP 的应用实际启动，桌面歌词窗口可见；包内独立 EXE 消费者实际读取停止状态和递增序列订阅。

本机证据为 `.review-audit/rc2/windows-verification.json`、`installation-verification.json` 和对应安装日志。

**N10 仍未完成，不将上述证据扩大为最终包播放通过。** 更新后的 computer-use 再次尝试歌词隐藏按钮，首次报告 `coordinate input geometry is unavailable`；重新读取截图/窗口后报告 `point ... is outside window bounds`。因此没有完成实际鼠标输入。最终包目前停在未播放状态，独立 Windows 媒体探针也没有找到播放中的测试歌曲；需用户开始隔离测试库的 timed 歌曲后继续独立媒体命令、切歌/暂停/定位/停止验证。最终包真实飞牛播放、实际歌词拖动及系统按钮仍需有效运行证据。

交付入口和验证步骤已就绪；清单保持 N10 待验收，持续任务不宣称全部完成。
