# 网站、诊断与更新发布记录

## 网站诊断插件

- `website/videosearch-diagnostics-upgrade.zip` 保持为唯一部署升级包。
- Windows 离线验证：Python 语法通过，定向测试 8/8 通过。
- 生产部署：**未执行**。原因是强制顺序要求应用验收先通过，而本报告仍有 A、D、E、H 等阻断项。
- 因未部署，未修改生产 routes/plugin，未触发 Defender、未创建真实诊断存储，也无需生产回滚。回滚说明和可交付包仍随证据返回。

## 签名更新

- 源码仅含验收公钥；未发现或索取生产私钥。
- 生产签名包、测试频道更新、版本化托管、Range 验证、回滚、稳定 `latest.json`：**未执行**。
- 稳定 `latest.json` 未发布，避免把未通过验收的构建暴露为稳定版本。
- `release/update-sample/` 仅为验收样例，绝不冒充生产签名发布。
