<p align="center">
  <img src="assets/MLCCS.VideoSearch.png" width="160" alt="MLCCS Video Search logo">
</p>

<h1 align="center">MLCCS Video Search</h1>

<p align="center">
  本地优先的 Windows 视频搜索工具：用文件名、画面、语音或画面文字，找到视频中的对应片段。
</p>

<p align="center">
  <a href="#下载与安装">下载</a> ·
  <a href="#它能做什么">功能</a> ·
  <a href="#系统要求">系统要求</a> ·
  <a href="#数据与网络边界">隐私</a> ·
  <a href="README_EN.md">English</a>
</p>

## 一句话了解

当你记得的是“画面里有什么”“谁说过一句什么话”，却想不起文件名时，MLCCS Video Search 会在你的本机视频库中建立可搜索索引，并把结果直接定位到命中的时间点。

| 你得到什么 | 说明 |
| --- | --- |
| 多种线索都能搜 | 文件名、画面内容、语音转写、OCR 画面文字，以及中文拼音/音近检索。 |
| 找到的不只是文件 | 结果会说明命中来源和时间点；点击即可从对应位置播放。 |
| 数据留在电脑里 | 视频、路径、查询、转写、OCR、缩略图和向量默认仅保存在本机。 |
| 适合自己的视频库 | 可以按资源库、格式、时长、修改日期和索引状态筛选结果。 |

## 它能做什么

- **按内容寻找视频。** 可以搜索“海边日落”“会议里提到预算”或画面中出现的文字，不必只依赖文件名。
- **快速回到关键片段。** 在列表或网格中浏览结果，预览命中时间点，并让播放器直接跳转。
- **保留搜索依据。** 结果区分文件名、画面、语音和 OCR 等来源，便于判断为什么会命中。
- **管理大型资料库。** 按相关度、最近修改或文件名排序；损坏或不支持的单个视频只会标记为文件级失败，不会中断整个任务。
- **按需占用资源。** 空闲后，搜索模型与私有 Qdrant 服务会自动停止；下一次索引或搜索时自动恢复。
- **安全地更新。** 更新需要用户确认，并包含断点下载、逐文件校验、健康检查和失败回滚。

## 工作方式

1. **添加资源库**：选择包含视频的文件夹。
2. **建立索引**：应用分析视频画面，并按需处理语音和 OCR 文字。
3. **输入你记得的线索**：可以是名称、画面描述、说过的话或屏幕上的文字。
4. **跳到命中位置**：查看匹配理由与时间点，然后直接播放、复制路径或在资源管理器中打开文件所在位置。

## 下载与安装

[下载 MLCCS Video Search v1.0.0](https://lixinchen.ca/docs/mlccs-video-search/1.0.0/MLCCS-VideoSearch-Online-Setup-1.0.0.exe)

首次安装需要下载应用核心、私有 Python/PyTorch CUDA 运行时、Qdrant Server、OpenCLIP Standard 和 BGE Small，共 `5,458,820,128` 字节（约 `5.084 GiB`）。安装器会显示精确下载量，并支持断点续传。Whisper 按需下载；OCR 为可选项，v1.0.0 OCR 包约 `17.77 MiB`。

安装器面向接近裸机的 Windows 10/11 环境设计，不要求预先安装 winget、.NET、Windows App Runtime、Python、CUDA Toolkit、浏览器或第三方包管理器。开始大型下载前，它会检查必需的 Windows 组件；可由 Microsoft 官方来源补齐的依赖会先验证签名再安装。N/KN 系统会安装 Media Feature Pack；如需重启，安装器会保留已下载内容并明确提示。

> **下载安全提示**：项目暂未使用 Windows Authenticode 签名，SmartScreen 可能显示“未知发布者”。请只从上方正式链接下载，并在运行前核对 SHA-256。

```powershell
Get-FileHash .\MLCCS-VideoSearch-Online-Setup-1.0.0.exe -Algorithm SHA256
```

将结果与发布的 [SHA256SUMS](https://lixinchen.ca/docs/mlccs-video-search/1.0.0/SHA256SUMS.txt) 对照。

## 系统要求

- Windows 10 1809（build 17763）或更高版本，或 Windows 11，x64。
- NVIDIA GPU，建议 4 GB 及以上显存；应用检测下限为驱动报告的 `3.75 GiB`。
- 能让随应用提供的 `PyTorch 2.7.1+cu128` 报告 CUDA 可用的 NVIDIA 驱动。
- 不需要单独安装 CUDA Toolkit；兼容的 NVIDIA 驱动是必要条件。

硬件暂不符合条件时仍可完成安装，方便先更新驱动。首次启动会显示 Windows、GPU、显存、驱动和 CUDA 检测结果。未达到要求时，索引和搜索会被阻止；资源库浏览、设置、日志和更新仍可使用。

v1.0.0 当前不提供 CPU、AMD GPU、Intel GPU 或低于 `3.75 GiB` 驱动报告显存的搜索与索引回退方案。

## 数据与网络边界

| 保留在本机 | 仅在你明确触发时联网 |
| --- | --- |
| 媒体文件、路径、查询、转写、OCR 原文、缩略图和向量 | 下载已锁定组件或可选模型；启用自动检查更新，或点击“立即检查更新” |

私有 Qdrant Server 仅监听动态选择的 `127.0.0.1` 端口；其 Payload 只保存不透明 ID、时间和模型/算法版本。v1.0.0 不提供诊断上传、自动遥测或“帮助改进”网络入口。关闭自动检查后，应用不会主动访问更新通道；本地日志会脱敏，可从设置页打开。

## 更新、重置与卸载

- 更新不会静默安装或重启。应用会保留可回滚的上一版本，并在切换后执行健康检查。
- 重置与卸载不会删除你的原始视频。
- 用户数据目录中的设置、索引和按需模型默认保留；交互安装会在重置旧数据前要求确认。

## 技术概览

面向希望了解实现细节的读者：应用以约 2 FPS 进行低分辨率场景分析，并为高运动片段保留额外代表帧。OpenCLIP Standard 生成视觉向量，BGE Small 生成中文文本向量；SQLite 负责路径、原文、FTS5 与向量 Outbox，私有 Qdrant 用于向量检索。文件名、语音、OCR、拼音/音近 FTS 与语义召回会通过 RRF 融合，并保留每个来源的时间点和分数贡献。

## 开发与文档

Windows 构建入口：

```powershell
.\scripts\Build-Windows.ps1 -Configuration Release
```

发布门禁要求 Windows 零警告构建、全部自动测试通过、固定不少于 30 个查询的数据集达到 `Recall@10 ≥ 0.80` 和 `MRR ≥ 0.65`，并完成 NVIDIA/CUDA 端到端验收与人工 UI 验收。

更多资料：[搜索说明](SEARCH.md) · [索引说明](INDEXING.md) · [架构](ARCHITECTURE.md) · [贡献指南](CONTRIBUTING.md) · [安全策略](SECURITY.md) · [支持说明](SUPPORT.md) · [第三方声明](THIRD_PARTY_NOTICES.md)
