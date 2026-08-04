# MLCCS Video Search

MLCCS Video Search `v1.0.0` 是一款仅在本机运行的 Windows 视频语义搜索应用。它可以按文件名、视频画面、语音和画面文字查找视频，并从命中时间点直接播放。

[English README](README_EN.md)

## 系统要求

- Windows 10 1809（build 17763）或更高版本，或 Windows 11，x64。
- NVIDIA GPU，至少 4 GB 显存。
- NVIDIA 驱动必须能让随应用提供的 `PyTorch 2.7.1+cu128` 报告 CUDA 可用。
- 不需要另行安装 CUDA Toolkit；兼容的 NVIDIA 驱动是必要条件。

安装器允许在硬件暂不合格时完成安装，便于先更新驱动。首次运行会显示检测到的 Windows、GPU、显存、驱动和 CUDA 状态；未达标时会硬性阻止索引和搜索，但资源库浏览、设置、日志和更新仍可使用。v1.0.0 不支持 CPU、AMD、Intel GPU 或少于 4 GB 显存的搜索/索引回退。

## 下载与安装

正式版在线安装器：

[下载 MLCCS Video Search v1.0.0](https://lixinchen.ca/docs/mlccs-video-search/1.0.0/MLCCS-VideoSearch-Online-Setup-1.0.0.exe)

首次安装的必选内容包含应用核心、私有 Python/PyTorch CUDA 运行时、Qdrant Server v1.18.3、OpenCLIP Standard 和 BGE Small，共 `5,458,820,128` 字节（约 `5.084 GiB`）。安装器从签名 Manifest 读取并显示精确下载量，支持断点续传；Whisper 按需下载，OCR 可在安装器或应用内选装，v1.0.0 的可选 OCR 包为 `18,631,826` 字节（约 `17.77 MiB`）。

安装器按接近裸机的 Windows 10/11 环境设计，不要求预装 winget、.NET、Windows App Runtime、Python、CUDA Toolkit、浏览器或第三方包管理器。开始下载大型组件前，它会复检 Windows 版本/架构、Visual C++ x64 运行库、Media Foundation、DirectX/WinUI 所需系统 DLL 以及网络/加密基础组件；缺失项只从 Microsoft 官方入口或 Windows Update 下载，验证 Microsoft Authenticode，并在安装或 DISM/SFC 修复后再次检查。Windows N/KN 会补装 Media Feature Pack；需要重启时会保留下载并明确停止。应用所需的 .NET 10、Windows App SDK、Python/PyTorch CUDA 和 Qdrant 均随签名程序载荷提供。NVIDIA 驱动仍须按实际 GPU/OEM 安装，安装器不会猜测并下载不匹配的驱动。

本项目不使用 Windows Authenticode。Windows SmartScreen 可能显示“未知发布者”。请只从上述正式地址或 GitHub Release 下载，并在 PowerShell 中核对 SHA-256：

```powershell
Get-FileHash .\MLCCS-VideoSearch-Online-Setup-1.0.0.exe -Algorithm SHA256
```

将结果与 Release 中的 `SHA256SUMS` 对照后再运行安装器。

## 已实现功能

- 约 2 FPS 的低分辨率场景分析，阈值 27，场景窗口限制在 2–8 秒；高运动窗口保存额外代表帧。
- OpenCLIP Standard 视觉向量、BGE Small 中文文本向量和私有 Qdrant Server；SQLite 是路径、原文、FTS5 与 Outbox 的权威存储。
- 文件名、语音原文、OCR 原文、拼音/音近 FTS；视觉、语音和 OCR 语义召回通过 RRF 融合，并保留来源、时间点和分数贡献。
- 按资源库、格式、时长、修改日期、索引状态筛选，并按相关度、最近修改或文件名排序。
- 列表/网格浏览、命中时间预览、播放器跳转、复制路径和打开所在文件夹。
- 搜索模型空闲 5/10/30 分钟后终止整个 Worker；无索引或搜索任务时同时停止私有 Qdrant，下一次使用自动恢复。
- 签名稳定通道检查、用户确认更新、断点下载、逐文件校验、`current/previous` 原子交换、健康检查和失败回滚。
- 单个损坏或不支持的视频只标记文件级失败，不会使整个资源库任务失败。

## 数据与网络边界

媒体文件、路径、查询、转写、OCR 原文、缩略图和向量保留在本机。Qdrant 只监听动态选择的 `127.0.0.1` 端口，其 Payload 只包含不透明 ID、时间和模型/算法版本。应用不提供诊断上传、自动遥测或“帮助改进”网络入口。

应用仅在以下明确行为中联网：下载已锁定组件/可选模型，以及启用自动检查或点击“立即检查更新”。关闭自动检查后不会主动访问更新通道。本地日志会脱敏，并可从设置页打开。

## 更新、重置与卸载

Feedback 版配置、索引和模型缓存不与 v1.0.0 复用。交互安装会先解释并要求确认；静默安装默认拒绝旧状态，只有显式重置参数才能继续。重置和卸载永远不会删除原始视频。

应用核心位于 `current`，上一个可回滚核心位于 `previous`；运行时、Qdrant 和模型按 SHA-256 放在不可变组件目录。更新不会静默安装或重启。卸载会删除程序目录和快捷方式，用户数据目录中的设置、索引和按需模型默认保留。

## 开发与验证

Windows 构建入口为：

```powershell
.\scripts\Build-Windows.ps1 -Configuration Release
```

发布门禁要求 Windows 零警告构建、全部自动测试通过、固定不少于 30 个查询的数据集达到 `Recall@10 ≥ 0.80` 和 `MRR ≥ 0.65`，再完成一次 NVIDIA/CUDA 端到端验收与一次纯人工 UI 验收。项目不进行 72 小时耐久验收。

参见 [贡献指南](CONTRIBUTING.md)、[安全策略](SECURITY.md)、[支持说明](SUPPORT.md) 和 [第三方声明](THIRD_PARTY_NOTICES.md)。
