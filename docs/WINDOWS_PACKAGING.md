# Melvune Windows候选封装

当前交付为Windows x64的ZIP与便携EXE，最低Windows 10 2004（19041）。完整MSI/EXE安装器尚未实施本轮新身份升级，不属于rc.8交付范围。

```powershell
./scripts/package-preview.ps1 -Version 0.2.0-rc.8 -Name Melvune-0.2.0-rc.8-win-x64
./scripts/package-windows.ps1 -Payload dist/Melvune-0.2.0-rc.8-win-x64 -Name Melvune-0.2.0-rc.8-win-x64-portable -PortableOnly
python scripts/verify-portable-package.py --packages dist/Melvune-0.2.0-rc.8-win-x64-portable --payload dist/Melvune-0.2.0-rc.8-win-x64 --report .review-audit/portable-verification.json
```

ZIP与便携必须从同一已提交源码检查点连续生成。脚本拒绝覆盖已有输出、来源不一致、错误版本/产品、缺失材料或非完整摘要；不要在两次封装中间提交不同源码。

ZIP包含Melvune.App.exe、WinUI资源、运行时、解码器、完整许可/对应源码、使用指南与独立信息消费者。便携EXE附加同一ZIP和MELVUNE-PAYLOAD-1尾部，先校验SHA-256、路径和产品元数据，再解包启动。只在Melvune-portable/Data存储配置；普通ZIP默认使用Melvune.Native。两者不自动合并。

校验覆盖尾部、ZIP CRC、每个文件摘要、来源、图标及新产品标识。旧发布工具与原始安装器留在Git历史和忽略的审查归档，不能用来发布或安装当前Melvune候选。当前 GitHub 工作流在标签推送时构建并校验附件，独立发布作业核对远端摘要后公开预发布版；手动运行仅构建，详见 [发布流程](RELEASING.md)。

正式发布前需要人工验证实际听音、服务部署、系统媒体卡片、设备热插拔、DPI及安装升级等边界；已验证与未验证分别记录于本轮验收和用户验证单。
