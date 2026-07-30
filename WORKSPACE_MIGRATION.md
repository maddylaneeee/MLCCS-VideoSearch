# MLCCS VideoSearch 工作目录迁移说明

打包日期：2026-07-29（America/Vancouver）

## 包含内容

- `src/`：Core、Agent、WinUI、Updater 的完整源码。
- `installer/`：在线安装器源码。
- `worker/mlccs_worker/`：Python worker 源码。
- `worker/manifests/`、`requirements*.txt`、`pyproject.toml`：模型与依赖锁。
- `scripts/`、`tools/`、`tests/`、`schemas/`：构建脚本、签名工具源码、验收测试和协议。
- `website/`：诊断插件源码、测试、回滚说明和现有小型部署包。
- `release/`：版本信息与未签名更新样例。
- `assets/`：应用图标源资产。
- 根目录的架构、构建、索引、搜索、UI、发布、验收和交接文档。
- `evidence/`：小体积 Windows 验收记录、截图、原始统计及网站回滚源码。
- `publication/`：当前在线安装器发布记录；不包含多 GB 在线安装载荷。
- `SOURCE-MANIFEST.sha256`：包内文件的相对路径、大小和 SHA-256。

## 有意排除

- `artifacts/` 中的历史 portable、在线安装载荷、重复 stage、诊断发布目录和旧源码 ZIP。
- `worker/python/`：约 7 GB 私有 CPython/CUDA/AI 运行时，可由锁文件和 Windows 构建脚本重建。
- 所有 `bin/`、`obj/`、`.vs/`、`TestResults/`、`__pycache__/`、`.pytest_cache/`。
- 已生成的 `.dll`、`.exe`、`.pdb`、`.pyc` 和运行日志。
- 模型文件、索引数据库、预览缓存、用户设置与测试媒体。
- 生产签名私钥、凭据、令牌或证书私钥。生产私钥本来就不应进入项目或迁移包。

## 恢复工作目录

1. 解压到新的工作目录。
2. 首先阅读 `READMEFIRST.md`、`BUILD_WINDOWS.md`、`ITERATION_HANDOFF.md` 和 `FEEDBACK5_ACCEPTANCE.md`。
3. 运行 `scripts\Bootstrap-Windows.ps1` 安装或检查工具。
4. 运行 `scripts\Build-PrivateRuntime.ps1` 重建私有运行时。
5. 运行 `scripts\Build-Windows.ps1` 执行总体编译门。
6. 使用 `SOURCE-MANIFEST.sha256` 验证迁移后文件；清单自身的哈希由外层 ZIP 交付记录提供。

72 小时耐久测试仍按用户要求预留，未在本轮执行。
