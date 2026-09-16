# MVP Roadmap

## Milestone 0 — 工程初始化

- [x] .NET 10 WPF solution 骨架
- [x] Core / Shell / Storage / Rules 分层
- [x] GitHub CI 模板
- [x] Hatchable 原型归档进仓库
- [ ] GitHub 新仓库创建并首次 push
- [ ] 选择最终许可证策略

## Milestone 1 — 桌面面板

- [ ] 无边框桌面面板
- [ ] 拖动 / Resize
- [ ] 锁定 / 折叠
- [ ] 位置、尺寸、透明度保存
- [ ] Explorer 重启后的挂载恢复

## Milestone 2 — 分类

- [ ] 新建 / 重命名 / 删除
- [ ] 排序
- [ ] 折叠状态
- [x] SQLite Schema v1 / Migration / 本地持久化后端
- [x] 分类服务层与安全删除语义

## Milestone 3 — 文件引用

- [x] Core 层 FileDrop 引用接收逻辑（待 WPF Drop 事件接线）
- [x] 文件、文件夹、快捷方式引用识别
- [ ] Shell 图标
- [ ] 双击打开
- [ ] 所在位置
- [x] 按需缺失文件状态刷新（待 watcher 接线）

## Milestone 4 — 文件安全

- [ ] IFileOperation
- [ ] 同名冲突策略
- [x] 本地操作日志 Schema 与引用操作记录
- [ ] 最近 20 次撤销


## Backend Baseline — 已完成

- [x] `%LOCALAPPDATA%\DeskZone` 数据目录
- [x] SQLite WAL / Foreign Keys / busy timeout / quick check
- [x] `schema_migrations` + v1 migration
- [x] `panel_state` / `categories` / `items` / `operation_log`
- [x] Reference 模式不移动真实文件
- [x] 非空分类安全删除
- [x] 系统“未分类”后备分类
- [x] SQLite Backup API 本地备份与保留策略
- [x] 应用启动前本地后端初始化
