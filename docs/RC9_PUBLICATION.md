# rc.9 Windows 安装版公开交付

完成日期：2026-10-18（Asia/Shanghai）。[公开 Release](https://github.com/JuuWaii/Melvune/releases/tag/v0.2.0-rc.9) 为未签名预发布版，Release ID `416369169`。

## 来源与执行结果

- 应用与封装源码：`44be9520e9e5ce522050617e6c89cbe314b0dcd0`，标签 `v0.2.0-rc.9`。
- [最终构建、安装验收与发布](https://github.com/JuuWaii/Melvune/actions/runs/39116856904) 的 package / installation / publish 三个作业全部成功。
- 核心121、Node24、便携完整性24、发布14、清理7、安装器定义1及安装参数2项检查通过，HTTP/JSON与队列专项通过；历史隐私审查2360个文件对象，阻断项0。
- 完整载荷603文件、MSI605文件一致；便携CRC/尾部/摘要与来源、EXE内嵌MSI、当前用户安装范围、目录选择和三个EXE的七档图标均通过。
- 实际执行 MSI 自选目录、安装 EXE 默认及自选目录三组静默安装/卸载；自选路径含中文、空格和方括号。每组安装文件摘要、开始菜单、安装目录记录和卸载后配置保留通过。
- 11个公开附件的远端大小及SHA-256与云端产物一致。四个二进制公开HEAD下载入口HTTP200；公开release.json及SHA256SUMS已GET读取、完整摘要匹配，来源与构建地址正确，校验清单10行覆盖其他全部附件。
- rc.8的7个附件名称、大小与摘要保持，rc.1的9个附件ID保持；两个已发布历史标签来源未改变。

## 首次失败与修正

[首次构建39046668968](https://github.com/JuuWaii/Melvune/actions/runs/39046668968) 的源码为 `0e047de635b5df73094304919fe265dda4b92af6`，四种包构建和内容校验通过，安装验收等待超时，未公开Release。修正了含空格安装属性的引号处理，增加失败日志与参数回归，并将安装验收拆成独立作业。确认rc.9尚未发布后，以明确旧标签对象的lease更新候选标签；旧源码提交和失败运行保留在历史中，rc.8及rc.1发布未改动。最终安装场景全部通过，才公开rc.9。

## 开发与定期清理规则

日常按需要执行 `./scripts/preview.ps1 -Run`，只生成可运行Debug预览，不生成ZIP、便携或安装器；完整打包仅最终发布时执行，普通分支推送不触发。正式ZIP脚本已改名为package-release.ps1，避免与开发预览混淆。安装验收可在同一工作流内复用原artifact单独重跑。

每日10:00的清理任务标识为melvune，运行clean-previews.py --apply，保留最近3份及7天内预览；只识别dist/previews中的有效标记与已完成产物，播放器运行时跳过。没有可清理目录时保持安静。历史发布包、验证证据、旧归档、未知/未完成目录、个人配置和用户资料不在清理范围。本轮没有删除历史预览或发行包。

## 公开附件

| 附件 | 字节数 | SHA-256 |
| --- | ---: | --- |
| AI_DISCLOSURE.md | 962 | `33f659b69ce65fa6c72374dcfdaf8c703d0230d28822f907d599884dc7105a80` |
| Melvune-0.2.0-rc.9-win-x64-portable.exe | 277661781 | `204596b9d0a0d7d6a8f7e6f1a73e35d13519fdb83c396efffa8ff8a527d751ee` |
| Melvune-0.2.0-rc.9-win-x64-setup.exe | 209792847 | `153c76f2456ef70fca0735d019e63abe309e59fcd5dbb6eb0d119f7b383fb0ee` |
| Melvune-0.2.0-rc.9-win-x64-setup.msi | 208633793 | `a5d705801bb855192fbe7e56b875ff6d5f82daf2ba21220f5aa60de93bc82666` |
| Melvune-0.2.0-rc.9-win-x64.zip | 239890399 | `1d679e84468b95da8d0f3fc66f739622457d4f36aebcf0f97689c6b7fc8d20e5` |
| release.json | 1915 | `5ee6189c5dd18d300b41778124af4d447caf771cbd89a9662dd1aa7bc5ed4152` |
| RELEASE_NOTES.md | 2032 | `59354f686730fe6eea88f1edd53d02a967868960cd4811940226ad83bffc20ef` |
| SHA256SUMS.txt | 906 | `771172502f784d21f0d91e3478e61fe93974331a2ed74fb7d031278c8d27bcae` |
| USER_GUIDE.md | 14160 | `3a84f754f3bbdfaa63f6bde87da1573aed6c9d671200f3cb912d98586293a2d2` |
| WIX_LICENSE.txt | 3369 | `4004370957cfc16afa52582f0cbd56c98a2f00846d6dadc51c7c7ab0500c0000` |
| WIX_NOTICE.md | 1064 | `d54fc94bf01fb423cbf586525648dba7a5e270f7316ee81e9b9e13bb1a92873a` |

## 验收边界

云端进行了全部二进制摘要与包内容检查，本机未重复完整下载四个大文件。静默安装不替代图形安装页面、SmartScreen、跨版本升级/修复、全部权限和系统/磁盘组合的人工验证。应用播放功能承接rc.8，实体媒体键、硬件和真实服务器未因安装器补齐而视为重新通过，详见RC9_USER_VALIDATION.md。
