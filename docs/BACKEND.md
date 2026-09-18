# DeskZone 本地后端实现

DeskZone 不设计远程服务端。“后端”指客户端内部的 **Core + Storage + Shell** 服务层：分类、文件引用、布局、SQLite、设置、备份和 Windows Shell 能力均在本机运行。

## 当前已实现

### Core

- `CategoryService`
  - 分类创建、重命名、折叠、排序。
  - 安全删除：非空分类默认拒绝；可迁移到其他分类或系统“未分类”。
  - UI 不直接操作 SQLite。
- `DesktopItemService`
  - 文件 / 文件夹 / `.lnk` 引用加入分类。
  - 同一个引用再次拖入其他分类时执行“移动归属”，而不是制造重复入口。
  - 分类之间批量移动。
  - 从分类移除引用时不会删除真实文件。
  - 按需刷新缺失文件状态，不做后台全盘轮询。
- `LayoutService`
  - 面板 Monitor、DPI、DIP 坐标、尺寸、透明度、折叠与锁定状态。
- `OperationLog`
  - 为引用添加、分类间移动、移除入口等操作记录本地日志数据，为后续 Undo 做基础。

### Storage

- SQLite：`Microsoft.Data.Sqlite 10.0.12`
- 数据库：`%LOCALAPPDATA%\DeskZone\deskzone.db`
- 设置：`%LOCALAPPDATA%\DeskZone\settings.json`
- 备份：`%LOCALAPPDATA%\DeskZone\backups\`
- 缓存：`%LOCALAPPDATA%\DeskZone\cache\`
- 日志：`%LOCALAPPDATA%\DeskZone\logs\`
- Managed Storage：`%USERPROFILE%\Documents\DeskZone\Storage\`

数据库初始化启用：

- Foreign Keys
- WAL
- `busy_timeout = 5000`
- `synchronous = NORMAL`
- `PRAGMA quick_check`

### Schema v1

- `schema_migrations`
- `panel_state`
- `categories`
- `items`
- `operation_log`

迁移 ID：`001_initial_local_backend`

## 安全语义

1. MVP 默认只建立文件引用，不改变真实路径。
2. 删除普通引用只删除 DeskZone 入口，不删除真实文件。
3. Managed 文件不能走普通“移除引用”路径。
4. 非空分类不能静默删除。
5. 分类名称不参与 Managed Storage 的物理目录 ID。
6. SQLite 批量修改使用事务。
7. 本地数据不上传、不遥测、不做账号系统。

## 备份

`SqliteBackupService` 使用 SQLite Backup API 生成一致性数据库副本，并复制 `settings.json`。默认保留最近 8 份。

### 文件变化监听

`FileSystemChangeMonitor` 根据当前引用路径的父目录建立聚合式 `FileSystemWatcher`。分类或引用发生变化时会重建监听目录；收到文件系统事件后由 WPF 层防抖刷新引用的缺失状态。监听只更新本地引用状态，不移动、删除或上传真实文件。

后续 Schema v2+ 迁移前会先触发本地备份。

## 尚未实现（按 Roadmap 顺序）

### Milestone 3 后续

- 缩略图缓存。
- 文件重新定位。
- Windows 原生右键菜单。

### Milestone 4

- `IFileOperation` 真实收纳。
- 同名文件“保留两者并自动重命名”。
- 回收站删除。
- 最近 20 次 Undo。
- 文件系统成功但 DB 失败时的恢复队列。

在这些安全能力完成之前，不把真实文件移动逻辑接到 UI 默认流程。

## 应用启动

`DeskZone.App` 启动时会先：

1. 创建本地数据目录。
2. 打开 / 创建 SQLite。
3. 应用 Schema Migration。
4. 执行数据库快速完整性检查。
5. 初始化成功后再显示主窗口。

初始化失败时应用停止，不执行任何文件整理操作。
