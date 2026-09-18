# DeskZone Wiki

> 桌序 DeskZone：让桌面上的文件、文件夹和快捷方式保持有序。

本文档是 DeskZone 项目的总览 Wiki，面向使用者、开发者和后续维护者。内容以当前仓库实现为准；尚未完成的能力会明确标注。

## 目录

- [项目定位](#项目定位)
- [当前功能](#当前功能)
- [用户界面与操作](#用户界面与操作)
- [项目结构](#项目结构)
- [运行机制](#运行机制)
- [本地数据](#本地数据)
- [安装、升级与数据保留](#安装升级与数据保留)
- [开发与构建](#开发与构建)
- [安全边界](#安全边界)
- [常见问题](#常见问题)
- [当前限制与路线图](#当前限制与路线图)

## 项目定位

DeskZone 是一个 Windows 优先、纯本地运行的桌面整理组件。它不是普通意义上的应用窗口，而是可以挂载到 Windows 桌面层的整理面板：

- 平时作为桌面组件显示在桌面上。
- 普通应用窗口打开时，普通应用可以覆盖它。
- 使用 `Win+D` 返回桌面后，组件继续显示。
- 用户可以创建分类，把文件、文件夹和快捷方式作为引用放入分类。
- 默认不移动、不删除用户真实文件。

核心原则：本地优先、文件安全、轻量常驻、可恢复。

## 当前功能

### 桌面组件

- 无边框、可移动、可调整大小的 WPF 面板。
- 通过 Windows `WorkerW` / 桌面 Shell 宿主挂载到桌面层。
- 不依赖全局置顶来模拟桌面常驻。
- 支持锁定位置与大小。
- 支持标题栏折叠：折叠后只保留 Logo 所在的标题栏。
- 支持透明显示和白色正常显示两种外观。
- 透明模式下保留半透明分类背景和浅色文字；正常模式下使用白色面板。
- 右下角保留调整大小的交互区域，但不显示额外的斜线装饰。

### 分类

- 新建分类。
- 拖入文件夹创建分类并导入其中的项目引用。
- 分类重命名：双击名称进入编辑，按 `Enter` 或失去焦点保存，按 `Esc` 取消。
- 单击分类标题区域切换展开/折叠。
- 分类支持拖动调整顺序的交互基础。
- 分类标题和分类内容使用圆角布局。
- 系统分类“最近打开”固定在第一栏，不允许删除。

### 文件对象

- 支持文件、文件夹和 Windows 快捷方式（`.lnk`）引用。
- 双击通过 Windows Shell 打开对象。
- 文件对象只显示图标和名称，不显示独立的小卡片底色或描边。
- 对象缺失时可以按需刷新状态。
- 当前引用所在目录发生变化时，DeskZone 会通过聚合式文件监听自动刷新缺失状态；不会因此移动或删除真实文件。
- 同一引用拖入其他分类时采用移动归属语义，避免重复入口。

### 最近打开

“最近打开”是固定系统分类，最多显示最近从 DeskZone 打开的 10 个对象，包括文件、图片、文件夹和快捷方式等。删除该分类不会被允许；移除其中的入口不会删除真实文件。

### 设置

设置入口提供以下配置：

- 锁定或解锁面板位置与大小。
- 关闭时保留到通知区域。
- 开机自动启动：登录 Windows 后自动运行 DeskZone，并作为桌面组件显示。
- 切换透明显示 / 白色正常显示。
- 打开本地数据目录。
- 导入和导出 DeskZone 数据包（分类、快捷方式引用、布局、最近打开记录和应用设置）。导入前会自动备份当前数据。
- 恢复面板尺寸。

## 用户界面与操作

| 操作 | 行为 |
| --- | --- |
| 拖动标题栏 | 移动桌面组件 |
| 点击分类标题 | 展开或折叠分类 |
| 双击分类名称 | 编辑分类名称 |
| `Enter` | 保存分类名称 |
| `Esc` | 取消分类名称编辑 |
| 拖入文件或文件夹 | 添加引用，或按文件夹内容创建分类 |
| 双击文件对象 | 使用 Windows Shell 打开 |
| 点击透明度图标 | 在透明与白色显示之间切换 |
| 点击折叠按钮 | 只保留标题栏 |
| 拖动窗口边缘 | 调整面板大小；锁定后不可调整 |

## 项目结构

```text
DeskZone/
├─ src/
│  ├─ DeskZone.App/          # WPF 主窗口、ViewModel、交互和资源
│  ├─ DeskZone.Core/         # 模型、服务接口和核心业务约束
│  ├─ DeskZone.Shell/        # Windows Shell、WorkerW、桌面宿主和系统打开能力
│  ├─ DeskZone.Storage/      # SQLite、JSON 设置、迁移和备份
│  └─ DeskZone.Rules/        # 自动分类规则预留模块
├─ tests/                    # 测试与验收说明
├─ docs/                     # 项目文档与本 Wiki
├─ installer/                # 安装工程预留目录
└─ prototype/hatchable/      # 交互原型，不是正式运行时
```

### 分层职责

- `DeskZone.App`：只负责 WPF 展示、窗口生命周期、用户输入和 ViewModel 编排。
- `DeskZone.Core`：定义分类、文件对象、面板布局和服务接口，不直接依赖具体 UI。
- `DeskZone.Shell`：处理 Windows 窗口宿主、Explorer / Shell 行为和系统打开。
- `DeskZone.Storage`：处理 SQLite、JSON 设置、数据库迁移、本地备份和路径。
- `DeskZone.Rules`：为后续自动分类能力保留边界。

## 运行机制

### 启动流程

1. 应用创建 `%LOCALAPPDATA%\DeskZone` 数据目录。
2. 初始化 SQLite 连接和 WAL 配置。
3. 执行数据库迁移与快速完整性检查。
4. 加载面板布局、分类、文件引用和外观设置。
5. 显示 WPF 主窗口。
6. 将窗口挂载到 Windows 桌面 Shell 宿主，并进行轻量健康检查。

初始化失败时应用不会执行文件整理操作。

### 桌面宿主

`DeskZone.Shell.WindowsDesktopHostService` 负责：

1. 查找包含 `SHELLDLL_DefView` 的桌面窗口。
2. 定位可用的 `WorkerW` / 桌面宿主。
3. 将 DeskZone 窗口改为真正的 `WS_CHILD` 桌面子窗口并挂载到 WorkerW。
4. 将窗口放在桌面层，低于普通应用窗口。
5. 在 Explorer 重启、`Win+D` 或宿主失效时保持或重新建立挂载。

该机制的目标是让 DeskZone 像桌面组件一样存在，而不是成为覆盖所有窗口的全局 TopMost 窗口。

### 数据流

```text
WPF Desktop Panel
        │
        ▼
DeskZone.App / ViewModels
        │
        ├── DeskZone.Core      分类、文件引用、布局和操作语义
        ├── DeskZone.Storage   SQLite、设置、备份
        └── DeskZone.Shell     Windows 桌面宿主与 Shell 操作
```

## 本地数据

默认数据目录：

```text
%LOCALAPPDATA%\DeskZone\
├─ deskzone.db              # SQLite 主数据库
├─ settings.json            # 外观和应用设置
├─ backups\                 # 本地一致性备份
├─ logs\                    # 预留日志目录
└─ cache\                   # 图标及其他缓存预留目录
```

数据库当前包含以下主要表：

- `schema_migrations`：迁移记录。
- `panel_state`：面板位置、尺寸、显示和锁定状态。
- `categories`：分类与系统分类标记。
- `items`：文件引用、所属分类和缺失状态。
- `operation_log`：引用添加、移动和移除等操作记录。

### 文件安全语义

- 添加文件对象默认只保存引用路径。
- 从分类移除引用不会删除真实文件。
- 删除非空分类不会静默丢失内容。
- 数据库批量修改使用事务。
- 数据库结构升级前优先执行本地备份。
- 数据不会上传到远程服务器，也没有账号系统和遥测依赖。

## 安装、升级与数据保留

DeskZone 的程序文件和用户数据分开保存。仓库现在提供可执行的 PowerShell 发布、安装、升级、验证和卸载脚本。升级安装只替换程序文件，不删除用户数据目录；普通卸载默认也保留数据，重新安装后会继续读取原有分类、引用、设置和备份。

详细的安装器、数据库迁移、备份和验收规则见 [`INSTALLATION-AND-UPDATES.md`](INSTALLATION-AND-UPDATES.md)。
安装脚本的使用方法见 [`../installer/README.md`](../installer/README.md)。

## 开发与构建

### 环境要求

- Windows 10 或 Windows 11。
- .NET 10 SDK。
- Visual Studio（“.NET 桌面开发”工作负载）或支持 .NET 10 的 Rider。

### 常用命令

在仓库根目录执行：

```powershell
dotnet restore DeskZone.sln
dotnet build DeskZone.sln -c Debug
dotnet run --project src/DeskZone.App/DeskZone.App.csproj
```

发布前至少应完成：

```powershell
dotnet build DeskZone.sln -c Release
```

### 开发约定

- 不使用 `git reset --hard`、`git checkout --` 或清理命令覆盖其他人的修改。
- 不把用户数据库、备份、私钥或生产配置提交到仓库。
- UI 不直接操作 SQLite 或删除真实文件；通过 Core / Storage / Shell 接口完成。
- 修改桌面宿主行为后，需要分别验证普通窗口覆盖、`Win+D` 返回桌面和 Explorer 重启恢复。
- 修改外观后，需要同时检查透明模式和白色正常模式。

### Commit 命名

```text
feat: 新功能
fix: 缺陷修复
refactor: 重构
docs: 文档
test: 测试
build: 构建或依赖
chore: 杂项
```

## 安全边界

DeskZone 当前是“引用式整理”工具，不默认承担真实文件管理器的高风险操作。以下能力需要单独设计、确认和回滚机制后才适合接入默认流程：

- 真实移动文件到 DeskZone 管理目录。
- 覆盖、改名和同名冲突处理。
- 删除到回收站。
- 批量撤销。
- 文件系统成功而数据库失败时的恢复队列。

## 常见问题

### 按 `Win+D` 后组件消失

确认应用运行的是最新构建，并检查桌面宿主是否仍然存在。Explorer 重启后，DeskZone 应通过健康检查重新挂载。如果仍未恢复，可重启 DeskZone 或 Windows Explorer。

### 组件覆盖了其他应用

桌面组件不应使用全局置顶。检查是否运行了旧版本进程；关闭旧进程后重新启动最新构建。桌面层模式的预期行为是：普通窗口覆盖 DeskZone，返回桌面后 DeskZone 再显示。

### 数据或分类不见了

不要直接删除数据库文件。先打开设置中的本地数据目录，保留 `deskzone.db`、`settings.json` 和 `backups`，再进行诊断或恢复。

### 文件图标或名称异常

文件对象保存的是路径引用。先确认目标路径仍存在；路径失效时对象不会自动移动真实文件。

## 当前限制与路线图

当前仍待完善的方向：

1. Windows 10 / 11、多显示器和不同 DPI 下的桌面宿主兼容性验收。
2. Shell 缩略图缓存、Windows 原生“打开所在位置”和全局显示 / 隐藏快捷键。
3. 安全的真实文件收纳、冲突处理和撤销队列。
4. Windows 10 / 11、多显示器、DPI 与 Explorer 重启人工验收。
5. 更完整的自动化测试和安装包流程。

相关文档：

- [`ARCHITECTURE.md`](ARCHITECTURE.md)：架构和桌面宿主说明。
- [`BACKEND.md`](BACKEND.md)：本地后端、SQLite 和安全语义。
- [`DEVELOPMENT.md`](DEVELOPMENT.md)：开发约定。
- [`ROADMAP.md`](ROADMAP.md)：里程碑和待办事项。
- [`../README.md`](../README.md)：项目简介和快速开始。
