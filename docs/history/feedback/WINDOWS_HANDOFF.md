# MLCCS VideoSearch Windows 交接记录

版本：`0.3.0-feedback5`  
更新日期：2026-07-29（America/Vancouver）

## 当前状态

- Windows 主解决方案、在线安装器与安装器弱网验收项目均可从源码编译。
- 主程序、Agent、Updater、Python worker、网站诊断插件和在线安装器源码均包含在工作目录。
- 搜索、资源库预览、资源目录过滤、自动索引、存储管理、后台 Agent/worker 协调和自适应 CUDA 索引已实现。
- 在线安装器支持持久断点续传、自适应 Range 分段、长时间重试、暂停/继续、取消删除缓存、后台解压/校验及 `uninstaller.exe`。
- Windows UI 的最终外观、缩放、高对比度、Narrator、键盘体验以及完整从零安装/卸载仍由用户继续主观验收。
- 72 小时耐久测试按用户要求预留，本轮不执行。
- 生产更新私钥未生成或读取，也未进入源码、日志或迁移包。

## 恢复顺序

1. 阅读 `READMEFIRST.md`、`WORKSPACE_MIGRATION.md`、`ITERATION_HANDOFF.md` 和 `FEEDBACK5_ACCEPTANCE.md`。
2. 运行 `scripts\Bootstrap-Windows.ps1`。
3. 根据锁文件运行 `scripts\Build-PrivateRuntime.ps1` 重建私有 Python/FFmpeg/libVLC/Qdrant/CUDA 运行时。
4. 运行 `scripts\Build-Windows.ps1`。
5. 运行 `dotnet run --project tests\MLCCS.VideoSearch.Installer.Acceptance\MLCCS.VideoSearch.Installer.Acceptance.csproj -c Release` 验证安装器弱网下载。
6. 按 `WINDOWS_ACCEPTANCE.md` 完成集中验收。

## 关键入口

- 解决方案：`MLCCS.VideoSearch.sln`
- UI：`src\MLCCS.VideoSearch.UI`
- Agent：`src\MLCCS.VideoSearch.Agent`
- Core：`src\MLCCS.VideoSearch.Core`
- Updater：`src\MLCCS.VideoSearch.Updater`
- Worker：`worker\mlccs_worker`
- 在线安装器：`installer\MLCCS.VideoSearch.OnlineInstaller`
- 安装器弱网测试：`tests\MLCCS.VideoSearch.Installer.Acceptance`
- Windows 总体构建：`scripts\Build-Windows.ps1`
- 私有运行时构建：`scripts\Build-PrivateRuntime.ps1`

## 当前在线安装器

- URL：`https://lixinchen.ca/docs/mlccs-video-search/0.3.0-feedback5/MLCCS-VideoSearch-Online-Setup.exe`
- 大小：`143943908` 字节
- SHA-256：`8350e9e39062f88ffda1f46def307300575f84c76eea2284829f311ed5e89962`
- 在线清单：`https://lixinchen.ca/docs/mlccs-video-search/0.3.0-feedback5/installer-manifest.json`

## 已知边界

- 源码迁移包不包含约 7 GB 私有运行时、模型、历史发布载荷、用户索引、预览缓存或测试媒体。
- 这些内容由 `worker\manifests`、依赖锁、构建脚本和在线安装清单重建。
- 当前验收记录中存在一个损坏源媒体：
  `M:\documents\dianying\laodou\大江大河\大江大河第14集.mp4`。
  单个损坏媒体不会再使整个索引批次显示 Failed。
- 稳定更新通道发布前仍必须使用外部保存的生产私钥替换验收公钥并重新执行更新验收。

迁移包内 `SOURCE-MANIFEST.sha256` 覆盖除清单自身以外的全部文件。外层 ZIP 的大小和 SHA-256 应由交付消息单独保存。
