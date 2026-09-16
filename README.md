# 桌序 DeskZone

> 桌面有序，工作更轻松。

DeskZone（桌序）是一个 Windows 优先、纯本地、轻量常驻的桌面整理客户端。它以一个固定在桌面层、可自由移动的整理面板为核心，让用户创建分类、重命名分类，并将文件、文件夹与快捷方式拖入不同分类中。

## 项目原则

- **纯本地**：用户数据仅保存在本机，不依赖云端服务。
- **永久免费**：核心功能不收费，不植入广告。
- **轻量常驻**：避免后台轮询，优先使用 Windows 系统事件。
- **文件安全优先**：MVP 默认采用“引用/视觉整理”模式，不擅自移动真实文件。
- **Windows 优先**：首版目标为 Windows 10 / Windows 11。

## 技术栈

- C# / .NET 10 LTS
- WPF
- Windows Shell / COM / PInvoke
- SQLite / Microsoft.Data.Sqlite（已接入本地后端）
- CommunityToolkit.Mvvm（后续接入）

## 仓库结构

```text
DeskZone/
├─ src/
│  ├─ DeskZone.App/          # WPF UI / 启动入口
│  ├─ DeskZone.Core/         # 核心模型与业务接口
│  ├─ DeskZone.Shell/        # Windows Shell 集成
│  ├─ DeskZone.Storage/      # 本地存储、设置与备份
│  └─ DeskZone.Rules/        # 自动分类规则（P1/P2）
├─ tests/                    # 测试工程占位
├─ docs/                     # 架构、开发与路线文档
├─ installer/                # MSIX / WiX 安装工程占位
├─ prototype/hatchable/      # 当前 Hatchable Web 交互原型
└─ .github/                  # CI、Issue / PR 模板
```

## 本地开发

### 前置条件

- Windows 10/11
- Visual Studio 2022/2026（安装“.NET 桌面开发”工作负载）或支持 .NET 10 的 Rider
- .NET 10 SDK

### 构建

```powershell
dotnet restore DeskZone.sln
dotnet build DeskZone.sln -c Debug
```

### 运行

```powershell
dotnet run --project src/DeskZone.App/DeskZone.App.csproj
```

## 当前阶段

当前仓库已完成 **MVP 工程骨架 + 第一版本地后端**。当前本地后端已经具备分类、文件引用、面板状态、SQLite Migration、操作记录与本地备份能力。下一阶段优先完成：

1. WPF 常驻桌面面板。
2. 面板移动、Resize、锁定与折叠。
3. 分类创建、重命名、删除与状态持久化。
4. 文件/文件夹/快捷方式引用拖入。
5. 双击打开与基础 Windows Shell 行为。
6. 将已完成的本地后端服务接入 WPF ViewModel。
7. Shell 图标、拖放和按目录聚合文件监听。

当前 Hatchable 原型保存在 `prototype/hatchable/`，用于 UI / 交互参考，不作为正式客户端运行时的一部分。

## 数据目录

```text
%LOCALAPPDATA%\DeskZone\
├─ deskzone.db
├─ settings.json
├─ backups\
├─ logs\
└─ cache\
```

## 许可证

尚未选择开源许可证。在正式确定仓库公开策略前，不默认授予额外开源许可。


## 本地后端

实现说明见 [`docs/BACKEND.md`](docs/BACKEND.md)。DeskZone 不提供远程业务后端，核心业务与用户数据全部留在本机。
