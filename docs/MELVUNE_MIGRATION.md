# Melvune 整体命名与独立配置

2026-10-17按用户最新决定，当前项目采用完整的新身份，不继续保留旧版运行时兼容。之前仅修改显示名称、保留旧namespace和配置的迁移方案已由本阶段替代；历史验收记录描述的是当时的版本。

## 当前名称

- 解决方案native/Melvune.sln；源码工程、C# namespace、XAML x:Class、程序集与资源均使用Melvune。
- 应用Melvune.App.exe，独立消费者Melvune.InfoClient.exe，便携启动器Melvune.Portable。
- 新ZIP启动入口Start-Melvune.cmd，当前候选0.2.0-rc.8。
- 应用与托盘、桌面歌词使用Melvune；飞牛音乐作为音乐服务名称继续保留，provider标识使用fnos-music。

## 独立数据与协议

普通ZIP默认使用 %LOCALAPPDATA%/Melvune.Native；显式--data-directory绝对路径优先。便携版只使用旁边Melvune-portable/Data。不会自动搜索、合并、复制或改写旧版个人配置，因此新版需要重新配置音乐来源和登录。不要把旧配置手动覆盖到新目录；来源账号与凭据摘要域也已统一为melvune。

信息接口发现记录位于当前配置的playback-information子目录，管道名称melvune-info-v1-加随机GUID。独立消费者按相同新身份发现，不承诺与改名前消费者互通。SMTC继续使用Windows系统接口。

便携格式为MELVUNE-PAYLOAD-1，新启动器与校验器仅接受新产品、格式、版本和完整摘要/来源绑定；不提供旧EXE或旧Data目录回退。

历史发布包、Git历史、旧本机回退副本和用户原有个人数据保留原样，不因源码改名重写。旧完整安装器和一次性发布工具停止作为当前入口；后续安装升级及发布器单独实施。

## 开发与构建

NAS为唯一开发主副本，旧本机副本只用于回退。断线时停止写入，不在两处同步覆盖。shell明确指定NAS工作目录；Git所有权信任仅限当前仓库和该进程。

scripts/native.ps1支持bootstrap、build、test和run；新的中间输出为obj/Melvune，避免旧生成XAML或程序集残留混入当前构建。合成规模夹具默认放在本机临时目录，源码与构建输出保留NAS。

构建脚本及MP3补丁的品牌属性同步更新后，原生解码桥接库会按固定工具链重新编译，生成新的实际provenance；不会只给旧二进制换标签。

NAS路径下解码器工具链默认使用本机临时目录Melvune-build-cache，减少SMB小文件往返；已锁定下载仍逐项校验，构建完成后将DLL、manifest与MSBuild属性写回NAS。可通过绝对路径环境变量MELVUNE_DECODER_CACHE指定其他可重建缓存位置。该目录不存用户音乐或凭据。
