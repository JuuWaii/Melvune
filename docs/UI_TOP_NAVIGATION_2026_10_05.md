# 顶部导航与品牌

按照用户网易云截图的位置参考，将返回、音乐搜索、提交搜索和清空放进 WinUI TitleBar 的内容区域。左侧栏从“音乐浏览”开始，移除重复图标和软件名；标题栏使用 28 号音乐图标和 20 号名称，保留系统窗口按钮、齿轮设置和黑白主题。TitleBar 原生控件继续管理可交互区域和拖动区域，不替换系统窗口呈现方式。

搜索当前音乐页面时保留原来源/分组/库范围；从首页、设置或歌单页搜索时转到所有音乐并使用输入的查询，不先加载无查询页。歌单管理自身的名称筛选仍保留。歌词、队列和全屏管理期间禁用顶部搜索，返回原音乐页面后恢复；来源不支持搜索时继续按能力禁用。

顶部返回优先关闭队列、全屏管理或歌词；歌单详情返回其管理页，管理页返回原浏览或设置上下文，专辑/歌手详情返回列表。搜索结果先清空查询，普通浏览或设置返回首页。这是对应页面的返回行为，不新增任意页面历史栈。保存时沿用已有阻止离开逻辑，命名/删除等模态弹窗打开时顶部搜索与返回不会导航。普通歌单/音乐库管理的页内返回移至顶部，队列与覆盖管理页面自身仍保留关闭按钮。

宽度自适应为品牌、搜索与系统按钮保留空间，搜索/清空用图标并提供名称和提示。回归检查使用真实加载的 WinUI 控件，记录 1360、980、820 宽度下的位置、尺寸、按钮不重叠和标题栏内垂直范围；覆盖浅色/深色、顶部按钮调用、首页/设置搜索、来源范围、歌单返回、保存保护、模态保护、队列/歌词/全屏返回及播放源/队列保持。最终结果见 NATIVE_BACKLOG.md。

最终报告 `.review-audit/top-navigation-review-app/collection-window-verification.json`，时间 `2026-10-05T16:59:12.4104056+08:00`，574/574、stage=complete、error 为空；Debug/Release 零警告零错误，隐私回归 8/8。首次主题断言在通知落定前执行而失败；补充 80 毫秒等待与浅/深色诊断后，Root、TitleBar、品牌、搜索和返回的 ActualTheme 均与请求一致，生产主题逻辑保持。最新独立 Release 位于 `.review-audit/top-navigation-release-app`，保留完整目录运行。

实际窗口截图已观察新布局，但鼠标工具两次激活均返回 `failed to activate captured window`，未执行点击；已正常关闭自建的合成数据窗口。原生按钮 Invoke 检查不代表实际标题栏鼠标输入或不同 DPI 全部通过。本轮不写入真实 NAS，也不更新已有 preview.8 固定包。

## 验证包准备时发现的模态导航问题

真实 ContentDialog 打开后调用标题栏齿轮，旧代码仍会切换背后的主页面；返回和搜索已保护，但共用主导航入口缺少相同检查。先增加原生齿轮 Invoke 回归，旧代码在 `.review-audit/modal-before-app/ui-motion-verification.json` 的 `modal_dialog_blocks_titlebar_settings_navigation` 处失败。修复后，`NavigateMainContentAsync` 在 `themedDialogOpen` 时拒绝离开，避免隐藏或释放弹窗下的页面。

完整窗口报告 `.review-audit/modal-fixed-app/collection-window-verification.json`，时间 `2026-10-05T17:22:40.9260406+08:00`，575/575、stage=complete、error 为空。新增检查在真实弹窗打开时调用原生齿轮按钮并验证首页保持；歌单命名弹窗检查也同时调用返回、搜索和齿轮，确认原管理页没有被释放。Debug/Release 构建零警告零错误。该修复将进入独立 preview.9 verified 包；初始 preview.9 包保留，不覆盖。实际鼠标输入与不同 DPI 的边界仍保留。
