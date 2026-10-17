# GitHub 改名方案：FnMusic → Melvune

NAS迁移和本地改名已完成，正在执行远端改名。当前origin为 `git@github.com:JuuWaii/fnmusic_pc.git`，目标为 `git@github.com:JuuWaii/Melvune.git`。

## 2026-10-17执行状态

用户已授权按本方案继续执行。已核对管理员身份和原仓库ID `1351488925`：默认分支master，2个分支均无保护，规则集为空，3个Git标签，1个公开Release含9个附件，Issues和PR均为空；未启用GitHub Pages。目标名称查询未找到同名仓库，实际名称可用性以改名表单为准。

当前等待内置浏览器登录GitHub。当前项目链接与构建引用已准备；旧发布和清理工作流均取消push触发，仅保留手动执行，避免改名提交触发远端发布或删除。历史发布记录中的旧仓库名称和附件名称保留。发布检查9项、便携检查16项、源码隐私和1978个历史文件对象审查通过，阻断项为0。远端改名、origin更新、源码推送和云端构建仍待执行。

## 执行顺序

1. 以仓库管理员账号检查 `JuuWaii/Melvune` 名称是否可用，记录仓库ID、默认分支、分支保护、标签、Release及附件清单。不要新建一个空仓库替代现有仓库。
2. 在原仓库 Settings → General → Repository name 输入 `Melvune`，点击 Rename。也可在已登录GitHub CLI的环境运行 `gh repo rename Melvune --repo JuuWaii/fnmusic_pc --yes`。此命令为备选方案，当前使用GitHub网页执行。
3. 在NAS主仓库执行 `git remote set-url origin git@github.com:JuuWaii/Melvune.git`，再核对 `git remote -v` 和 `git ls-remote origin HEAD`。其余工作副本也更新origin；远端未改名前不要提前改断现有连接。
4. 将仓库描述改为“Melvune · 合听 — 多来源桌面音乐播放器，支持本地音乐、NAS、Navidrome、Emby与Subsonic兼容源”。更新README当前项目链接、徽章、关于页和外部引用；旧Release附件名称及历史标签保持不变。
5. 修改 `.github/workflows/remove-superseded-releases.yml` 中的 `github.repository` 白名单为 `JuuWaii/Melvune`。该工作流会删除远端Release，已取消push触发，改名不运行清理。历史 `publish-verified-candidate.yml` 固定rc.1资产，保留其版本边界，不用于发布新品牌候选包。
6. 本地 `windows-packages.yml` 已将新构建产物改为Melvune前缀；发布前选定新版本号并同步标签触发条件、版本声明、文档及校验器。不要覆盖已交付的FnMusic rc.7附件，不重打历史标签。
7. 完成本地变更审阅后，单独推送改名提交。先运行只构建、不发布的工作流；核对新EXE、ZIP、SHA-256、来源、旧配置/便携Data兼容和GitHub下载。正式发布单独进行。

## 重定向与验收

GitHub说明：改名后原仓库的网页链接及clone/fetch/push地址会重定向，Issues、Wiki、Stars等信息继续保留；建议更新本地origin。GitHub Pages项目站点地址不在此重定向保证内，仓库托管Action的 `uses: owner/repository@ref` 调用也不会自动重定向。执行前检查是否使用这两项，不假定所有网址都自动迁移。

不要在同一账号下重新创建名为 `fnmusic_pc` 的仓库，否则旧地址重定向会失效。验收应确认仓库ID、默认分支、Issues/PR、标签和既有Release附件没有变化，并检查旧地址跳转和新地址下载。

官方依据：[GitHub仓库改名文档](https://docs.github.com/en/repositories/creating-and-managing-repositories/renaming-a-repository)。
