# Windows 缺陷与修复记录

| ID | 缺陷 | 修复/状态 |
|---|---|---|
| W-001 | Bootstrap 未检查 winget 退出码却输出完成 | 记录为脚本缺陷；随后直接验证 .NET、VS Build Tools、Windows SDK。 |
| W-002 | 私有运行时不支持缓存/`bundled:`，get-pip 错用 `--no-index` | 已修复并完成运行时组装。 |
| W-003 | macOS 锁遗漏 Windows 条件依赖 | 补齐并锁定 colorama、greenlet、tzdata、jsonschema 等 7 个闭包包。 |
| W-004 | PyTorch 锁定的是 CPU-only wheel | 已替换为带精确大小/SHA-256 的 torch/torchvision cu128 wheel；CUDA 实测可用。 |
| W-005 | requests 与 chardet 7.4.3 产生不兼容警告 | chardet 固定为 5.2.0，更新哈希、URL与许可。 |
| W-006 | WinUI XAML Button Icon 无效 | 已修复。 |
| W-007 | SQLite 测试连接池阻止临时库删除 | 已清池并修复测试。 |
| W-008 | WinAppSDK publish 漏应用 PRI/XBF，UI 启动后无法导航 | 发布脚本显式复制应用 PRI、MainWindow.xbf 与 Pages XBF；保留启动崩溃日志。 |
| W-009 | AppInstance 单实例路径出现无窗口进程 | 移除不可靠的重定向分支；窗口实测启动。 |
| W-010 | Agent 在便携拓扑中查找错误的 UI/Python 路径 | 改为从 release root 查找 `ui` 与 `worker`。 |
| W-011 | embedded Python 不导入同级 worker 包 | 增加相对 `sys.prefix` 的 `.pth`，路径随便携目录移动。 |
| W-012 | Updater 健康检查路径错误，UI 不支持 `--health-check` | 路径改为 `current/ui`，UI 增加无窗口健康检查退出。 |
| W-013 | UI 索引进度为静态模拟文字；Agent 队列为空操作 | 删除模拟进度；增加真实 PyAV 解码、OpenCLIP CUDA 推理、缩略图与 SQLite 向量提交。 |
| W-014 | 单路解码使 GPU 等待 | 增加按 CPU 自动调整的多路解码、有界队列、合批 GPU 推理和单写者 SQLite。 |
| W-015 | 恢复后 UI 累计数回零 | 状态改为读取数据库模型总行数；吞吐只计算本次新增量。 |
| W-016 | UI 读取状态时 Windows `os.replace` 偶发拒绝访问 | 原子状态替换增加有界重试，不降级为半写 JSON。 |
| W-017 | 便携打包 Copy-Item/Compress-Archive 极慢 | 私有运行时使用 robocopy 多线程复制，归档优先 7-Zip。 |
| W-018 | 便携包未包含锁定视觉模型 | 发布脚本校验并包含标准视觉模型；Agent可在本地模型和便携模型间安全回退。 |

仍未修复的产品缺口见 `KNOWN_LIMITATIONS.md`。
