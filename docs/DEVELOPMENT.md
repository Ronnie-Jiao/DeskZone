# 开发约定

## 分支建议

- `main`：可构建、可演示的稳定主线。
- `feature/<name>`：功能开发。
- `fix/<name>`：缺陷修复。
- `docs/<name>`：文档变更。

## Commit 建议

使用简洁前缀：

- `feat:` 新功能
- `fix:` 修复
- `refactor:` 重构
- `docs:` 文档
- `test:` 测试
- `build:` 构建/依赖
- `chore:` 杂项

## Definition of Done

- 正常路径可用。
- 错误路径有提示或可恢复行为。
- 重启后状态一致。
- 无明显空闲 CPU 回归。
- 基础 DPI 场景正常。
- 无未处理异常。
- 至少有一条自动化或手工验收记录。
