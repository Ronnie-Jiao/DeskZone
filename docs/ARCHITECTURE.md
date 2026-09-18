# DeskZone 架构

## 分层

- `DeskZone.App`：WPF UI、窗口生命周期、用户交互。
- `DeskZone.Core`：与 UI/Windows 实现解耦的模型和业务接口。
- `DeskZone.Shell`：Windows Shell、Explorer、COM、PInvoke、拖放与系统集成。
- `DeskZone.Storage`：SQLite、JSON 设置、备份与迁移。
- `DeskZone.Rules`：P1/P2 自动分类规则。

## MVP 数据流

```text
WPF Desktop Panel
      │
      ▼
Application/Core
 ├─ Category
 ├─ DesktopItem
 └─ Layout
      │
      ├────────► Storage (SQLite + JSON settings + local backup)
      │
      └────────► Shell (Explorer / Windows API)
```

## 关键约束

1. 默认只保存文件引用，不静默移动真实文件。
2. 本地数据目录位于 `%LOCALAPPDATA%\DeskZone`。
3. UI 层不直接执行危险文件操作；统一通过 Core 接口进入 Shell / Storage。
4. 文件真实移动、删除与撤销必须有操作日志与失败回滚。
5. 无变化时不做周期性全盘扫描。


## 当前后端实现

- `CategoryService`：分类 CRUD、折叠、排序、安全删除。
- `DesktopItemService`：Reference 模式添加、移动、移除、缺失状态检查。
- `LayoutService`：Monitor + DPI + DIP 布局持久化。
- `SqliteStorageInitializer`：Schema Migration、WAL、完整性检查。
- `SqliteWorkspaceStore`：参数化 SQL 与批量事务。
- `SqliteBackupService`：SQLite 一致性备份 + settings 本地备份。
- `LocalBackend`：本地服务组合入口。

真实文件收纳仍属于 Milestone 4，未在当前版本默认启用。


## Windows Desktop Host

`DeskZone.Shell.WindowsDesktopHostService` 负责把 WPF 主窗口挂载到 Windows 桌面层：

1. 向 `Progman` 发送 WorkerW 初始化消息。
2. 查找承载 `SHELLDLL_DefView` 的窗口与其后方 WorkerW。
3. 将 DeskZone HWND 改为 `WS_CHILD`，真正挂载到桌面宿主。
4. 不使用 `TopMost` 模拟桌面常驻；真正的桌面子窗口不会被 `Win+D` 的顶层窗口隐藏流程一起收起。
5. UI 层每 500 毫秒做一次轻量宿主健康检查；正常挂载时只检查 HWND / parent，只有 WorkerW 失效时才重新枚举 Shell 窗口。启动参数不会切换到普通窗口模式，旧版 `--preview` 参数也会按桌面组件处理。

该实现为 v1，需要在 Windows 10 / 11、多显示器、Explorer 重启和不同 DPI 模式下继续做人工兼容性验收。
