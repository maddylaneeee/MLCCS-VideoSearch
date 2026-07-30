# MLCCS Video Search 0.3.0 Feedback 5 验收记录

生成日期：2026-07-29（America/Vancouver）

## 本轮功能

- 搜索页可按已配置资源文件夹精确过滤，查询与筛选状态随页面会话保留。
- 资源库网格/列表按可见容器流式生成预览，滚动到后续视频时继续加载；图片解码宽度限制为 360 像素。
- 索引采用定位采样、多视频并行、OpenCV/NumPy CPU 预处理、CUDA 自适应批处理与 OOM 自动降批。
- 解码并发按 CPU、可用内存与本地/网络存储类型动态缩放；本机本地盘选择 10 路，`M:` 网络盘选择 6 路。
- 文件名、语音文字和 OCR 搜索延迟加载 CLIP，只有视觉/综合搜索才占用视觉模型显存。
- 主程序、Agent、Updater 与在线安装器统一使用 `C:\Users\mattl\Downloads\已生成图像 1.png` 生成的多尺寸图标。

## 自动化与运行时结果

- Windows 全量构建门：通过，0 警告、0 错误。
- C# Core：10/10 通过。
- Python Worker/协议/Schema：10/10 通过。
- Python 语法编译：通过。
- UI 与 Agent：真实发布目录启动并保持运行，Windows 应用日志无本轮崩溃记录。
- Agent IPC：读取到 4 个资源文件夹；以 `M:\documents\dianying` 过滤返回 5 个结果，全部严格属于所选文件夹。
- 搜索 Worker：非视觉冷启动查询成功，工作集约 81 MiB。
- 六个 10 分钟视频的同粒度 CUDA 基准：9.478 fps → 26.392 fps（2.78 倍）。
- 真实 `M:` 网络资源库持续索引：RTX 2050、批大小 64、网络盘 6 路，阶段观测约 12.51 fps。
- 持续长跑后阶段吞吐达到 20.28 fps；修正版重启续跑时已恢复到 72.7%，无需重建既有帧记录。
- 网络视频采样对照：150 个随机定位采样点 16.672 秒；连续解码抽样 44.785 秒，定位采样快 2.686 倍。
- 单个损坏媒体不再令整个批次最终显示 Failed；失败文件会单独保留错误并让批次以完成状态结束。

## 交付边界

- UI 外观、交互手感、缩放、高对比度、Narrator 与键盘主观验收由用户继续反馈。
- 72 小时耐久测试按用户要求预留，本轮不执行。
- 生产更新私钥未生成、未读取、未进入源码或交付；稳定更新通道仍需 UI 验收后使用外部保管的生产密钥。
- 已发现一个源媒体损坏：`M:\documents\dianying\laodou\大江大河\大江大河第14集.mp4`，FFmpeg/PyAV 报告 H.264 access unit 缺少图像；其余索引继续。

## 本地交付

- 可运行目录：`C:\Users\mattl\Downloads\1213`
- 可继续开发源码：`C:\Users\mattl\Downloads\1213\_development\source-current`
- 主源码：`D:\MLCCSapps\MLCCS-VideoSearch-FromScratch`

## 在线安装器

- 本地安装器：`C:\Users\mattl\Downloads\MLCCS-VideoSearch-Online-Setup-0.3.0-feedback5.exe`
- 线上安装器：`https://lixinchen.ca/docs/mlccs-video-search/0.3.0-feedback5/MLCCS-VideoSearch-Online-Setup.exe`
- 在线清单：`https://lixinchen.ca/docs/mlccs-video-search/0.3.0-feedback5/installer-manifest.json`
- 安装器：143,943,908 字节；SHA-256 `8350e9e39062f88ffda1f46def307300575f84c76eea2284829f311ed5e89962`
- 清单：11,478,734 字节；SHA-256 `0acecbaae15a38e8ea3a09fa7d6436755ac5543a741d0d6e056b921f305c2fcd`
- 必需下载：应用核心 186,752,530 字节、私有运行时 3,559,201,093 字节、基础视觉模型 1,365,789,903 字节。
- 可选 OCR：18,631,826 字节；语音模型由应用按需下载。
- MLCCS 远端逐文件 SHA-256、HTTPS HEAD、清单下载哈希与 Range 206 验证均通过。
- 旧清单已移入服务器 `docs\mlccs-video-search\rollback`，未保留在版本入口。
- 针对测试机“点击后无窗口”反馈，安装器取消单文件压缩，并在 WinForms 初始化前写入启动日志；顶层异常会通过原生 Windows 对话框显示。
- 安装器启动日志：`%LOCALAPPDATA%\MLCCS\VideoSearch\Installer\startup.log`；若本地应用数据不可写，则回退到 `%TEMP%\MLCCS-VideoSearch-Installer\startup.log`。
- 下载包按 2–64 MiB 自适应 Range 分段，网络异常最多连续重试 100 次并以 60 秒封顶退避；若中间网络节点剥离 Range，则自动退回连续下载。
- 未完成下载保存在 `%LOCALAPPDATA%\MLCCS\VideoSearch\Installer\downloads\0.3.0-feedback5`，关闭安装器后仍可继续；“取消”会删除本次所选组件的完整包和 `.partial` 文件。
- 解压和 SHA-256 校验在后台线程执行并持续汇报文件进度，暂停与取消在下载、解压和校验阶段均有效。
- 安装根目录生成 `uninstaller.exe`；Windows 当前用户卸载注册包含 `UninstallString`、`QuietUninstallString`、安装日期、大小和真实安装目录。
- 受控弱网验收通过：强制断流恢复、跨进程式断点续传、暂停期间文件零增长、非 Range 回退和最终 SHA-256 均通过。
