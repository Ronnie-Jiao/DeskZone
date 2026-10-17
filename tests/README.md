# Tests

## 分类标题拖放回归测试

`DeskZone.App.Smoke` 使用真实主窗口模板、WPF 命中测试与路由事件，以及隔离的临时 SQLite 数据库验证：

- 只读标题以移动效果接收批处理文件和快捷方式，将原件移入分类收纳目录并自动展开。
- 文件移动完成后才向资源管理器返回结果；无法移动的项目保留在原位置。
- 名称、箭头、数量和菜单按钮均能接收文件拖入。
- 分类名称正在编辑时不被文件数据替换，重复拖入不产生重复引用。
- 已有引用转分类后实体文件移入目标分类收纳目录，两个分类的内容和数量刷新。
- 应用内项目拖动保留移动分类归属的行为。
- 系统分类拒绝文件拖入。
- 卡片以固定列宽从左到右填充，每行排满后再换行，末行保持左对齐。
- 启动时清理可访问磁盘上的缺失项目；外部删除事件清理引用、托管项目和最近打开记录，真实已存在项目不受影响。
- 启动时及运行中每分钟复查快捷方式目标；仅当 `.lnk` 仍存在、目标可明确解析为本地/可访问磁盘路径且目标确实不存在时，清理 DeskZone 引用和最近记录，不删除快捷方式文件。

从源码根目录运行（Windows、.NET 10 SDK）：

```powershell
dotnet run --project tests/DeskZone.App.Smoke/DeskZone.App.Smoke.csproj -c Debug -p:OutputPath=obj/header-drop-smoke/
```

只运行启动清理和运行中快捷方式目标清理回归：

```powershell
dotnet run --project tests/DeskZone.App.Smoke/DeskZone.App.Smoke.csproj -c Debug -p:OutputPath=obj/header-drop-smoke/ -- --shortcut-cleanup-only
```

独立输出路径避免锁住当前 Debug 预览；临时文件与测试数据库在完成后清理，不使用真实用户数据库。此测试验证应用命中和事件处理，不等于 Explorer/WorkerW 上的真实鼠标拖放验收，后者仍须人工实测。

## 后续测试

其他功能优先覆盖：

- 分类创建 / 重命名 / 删除。
- 布局序列化与恢复。
- 文件引用缺失状态。
- Shell 路径与中文路径处理。
- 文件操作冲突与回滚。

当前回归程序不依赖额外的测试框架包。
