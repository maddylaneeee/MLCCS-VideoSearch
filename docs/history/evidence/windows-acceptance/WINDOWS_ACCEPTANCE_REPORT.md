# MLCCS Video Search Windows 集中验收报告

- 版本：0.1.0（Windows 修复构建）
- 主机：Windows build 26200，AMD Ryzen 5 7535HS（6C/12T），约 24 GB RAM，NVIDIA GeForce RTX 2050 4 GB
- 测试媒体库：`M:\documents\dianying\yongzheng`，44 个 MP4，24,800,737,599 字节
- 72 小时耐久测试：按用户要求预留，本次未执行，不计为通过

## 总结

结论为 **有限验收，禁止稳定发布**。Windows 原生编译、单元测试、私有 CUDA 运行时、真实视频解码、OpenCLIP CUDA 推理、SQLite 向量提交、实时 UI 状态和诊断插件离线测试均已有证据。完整产品验收仍被搜索/播放器、语音/OCR、Qdrant、首次下载体验、无开发依赖干净机、辅助功能、生产诊断部署和生产签名更新等缺口阻断。

先前界面的索引数字是静态演示内容，未对应实际工作。Windows 修复版已删除模拟进度：实时状态由 Worker 原子写入，UI 每秒读取；数据库、进程/GPU 遥测和 UI 三者可互相核对。

## 已通过或已有实证

- 交接 ZIP 大小 192,139 字节，SHA-256 `428e85276363f3abd37e56e75a4776b5dca67e95b0bed1e50ce77de780ac71f3`；manifest SHA-256 `82f946a90c78ef393e4139ab8e6686ba41b6465628fe3754c96307292b711890`。
- 最终编译门禁曾通过：WinUI/Core/Agent/Updater/SigningTool 0 错误；C# 10/10；Python 7/7。真实索引改造后的最终门禁日志随最终制品更新。
- 私有 CPython 依赖 `pip check` 无断裂；核心导入成功。
- PyTorch 已由 CPU-only 轮替换为锁定的 `2.7.1+cu128`；CUDA 12.8、RTX 2050、4 GB VRAM 检测成功。
- 锁定 OpenCLIP 标准模型 3 个文件按大小和 SHA-256 验证；主权重 1,464,635,263 字节。
- 真实索引使用高优先级进程、12 个 CPU 线程、CUDA 推理和 SQLite WAL；支持暂停、取消、进程互斥、文件级时间戳检查点及恢复。
- 全库真实索引完成：121,072.68 秒媒体、44 个文件、30,281 个唯一 512 维 float16 向量和 30,281 张缩略图；0 个错误长度/重复键。
- 单路基线约 13.5 帧/秒；4 路并行解码最终约 16.1 帧/秒，保留有界生产者—消费者实现。并行度和批大小按 CPU/VRAM 自动计算，可由本机配置覆盖，不写死到发布包。
- UI“索引任务”页显示实际文件、累计向量数、进度、GPU/CUDA、CPU 线程、批大小、解码器数和真实吞吐；截图见 `screenshots/real-index-live-ui.jpg`。
- 网站诊断插件离线语法检查及 8/8 定向测试通过。
- 最终 x64 便携 ZIP 为 Zip64 归档，5,335,373,290 字节、67,495 个文件；完整 `7z test` 通过，SHA-256 `0aa2dc7dfd0aceca21ccc0f2ea93b7a1c8f55a32ad30911cfb102a5363deae56`。
- 最终便携目录的 UI `--health-check` 返回 0，真实窗口成功启动并显示已提交 30,281 个视觉向量；包内私有 Python 实测 CUDA 可用，三个标准 OpenCLIP 模型文件再次通过大小与哈希核验。

## WINDOWS_ACCEPTANCE 分节结果

| 节 | 状态 | 结果 |
|---|---|---|
| A 干净机便携启动 | 部分通过 | 最终便携目录已在本机执行健康检查、CUDA 导入、模型哈希和真实窗口启动，且内置已验证标准视觉模型；未在 Windows Sandbox/新账户完整执行无开发依赖首启、下载中断、哈希错误和磁盘不足用例。 |
| B 能力门控 | 部分通过 | CUDA/VRAM 探测和自适应视觉批大小有实证；无 CUDA 主机、Whisper 模型建议/OOM 回退、CPU OCR未做运行验收。 |
| C 初始索引与恢复 | 部分通过 | 约 39 分钟真实索引阶段完成全库，包含暂停/继续（暂停期间数据库行数稳定）、3 次受控取消/检查点恢复和并行解码；托盘完整生命周期、Windows 重启/掉盘仍未通过。72 小时按用户要求预留。 |
| D 索引正确性 | 未通过 | 已产出按 4 秒采样的真实视觉向量与缩略图；原要求的场景阈值、2–8 秒窗口、Whisper、OCR、Qdrant 和全套增删改移动/重复/损坏用例未完成。 |
| E 搜索与交互 | 未通过 | UI 搜索/播放器仍未连接真实向量查询，Recall@10、MRR、音近提升、p50/p95 均无有效结果。 |
| F UI/辅助功能 | 部分通过 | 真实窗口启动和主要页面截图已保存；100/150/200%、窄屏、最大化、高对比度、键盘、Narrator、全套提示窗口未集中通过。 |
| G 诊断与隐私 | 部分通过 | 插件离线 8/8；因应用总验收未通过，遵循强制顺序未部署生产站点，故真实 Defender/存储/回滚未做。 |
| H 更新 | 未通过 | 签名/防篡改单元覆盖存在；无外置生产私钥，未执行测试频道完整更新、回滚和稳定 `latest.json` 发布。 |
| I 发布证据 | 部分通过 | 编译/运行/截图/硬件/模型/依赖证据已收集；生产签名、完整许可清单和稳定发布记录仍是阻断项。 |

## 关键证据

- `raw/cuda-runtime.txt`
- `raw/real-index-database-proof.txt`
- `raw/real-index-final-proof.json`
- `raw/pause-resume-proof.txt`
- `raw/real-index-hardware-samples.csv`
- `raw/real-index-batch32-samples.csv`
- `raw/installed-model-hashes.txt`
- `raw/machine.txt`
- `raw/test-media-inventory.txt`
- `raw/final-portable-runtime-validation.txt`
- `screenshots/real-index-live-ui.jpg`
- `screenshots/final-portable-window.jpg`
- `../logs/real-index-live.log`、`real-index-resumed*.log`、`real-index-parallel.log`

所有“未通过”均保持未通过，不以静态页面、模拟数字或单元测试替代真实验收。
