# 最终依赖、模型与许可清单摘要

## 运行时

- 私有 CPython 3.12 x64；不依赖系统 Python。
- 依赖锁共 144 个制品：CPython、get-pip、142 个 wheel。
- 已安装 wheel 的逐包版本/许可字段：`raw/python-license-inventory.csv`。
- `pip check`：无断裂依赖。
- CUDA 栈：torch 2.7.1+cu128、torchvision 0.22.1+cu128；GPU 实测 CUDA 12.8。

## 模型锁

- visual：6 文件，6,255,217,292 字节。
- text：31 文件，1,904,682,210 字节。
- speech：26 文件，6,955,372,352 字节。
- OCR：16 文件，195,658,535 字节。
- 本次安装并打入便携包：OpenCLIP standard 3 文件；精确哈希见 `raw/installed-model-hashes.txt`。
- 其他模型仅存在于锁文件，未声称已下载或验收。

## 许可阻断

- Python/wheel 元数据清单已生成，但 `SEE-PACKAGE-METADATA` 项尚未全部人工归一化。
- FFmpeg/ffprobe 与 libVLC 独立二进制及其 LGPL/GPL 构建说明未纳入；PyAV 自带库不能替代计划要求的独立工具/播放器许可交付。
- 因此当前制品为验收构建，不是可稳定公开分发的许可完结版本。
