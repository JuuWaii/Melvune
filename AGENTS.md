# Melvune 工作约定

- 此仓库为迁移后的NAS主工作副本，应用名称为Melvune（合听）。旧fnmusic_pc本机目录仅用于回退，不在两个副本间并行开发。
- 当前任务以 docs/NEXT_DEVELOPMENT_CHECKLIST.md 为准；批量实现后集中验证，修复后只重跑受影响检查。
- 保留用户未提交的bug/logo删除、img资料、历史发布包与验收证据；不要将个人配置、凭据或NAS私有路径提交到Git。
- 2026-10-17用户明确不考虑旧版兼容：当前目录、namespace、工程、资源、数据目录、凭据域、来源标识和信息/便携协议统一新命名，不保留旧品牌fallback。历史包与真实个人数据不改写，新版使用独立Melvune.Native配置；飞牛音乐来源显示名保留。
- 新应用构建输出为Melvune.App.exe，解决方案为native/Melvune.sln；禁止覆盖正在运行的输出，项目构建串行执行。
- 未经相应授权不要推送、上传、打标签或发布远端。GitHub改名执行顺序见docs/GITHUB_RENAME_PLAN.md。
