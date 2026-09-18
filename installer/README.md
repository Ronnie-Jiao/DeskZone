# Installer

当前提供可直接运行的 PowerShell 安装/升级脚本；正式发布阶段可在此基础上封装为签名的 MSIX、WiX 或自解压安装包。所有安装方式都必须遵守
[`docs/INSTALLATION-AND-UPDATES.md`](../docs/INSTALLATION-AND-UPDATES.md) 中的数据保留约束。

发布前需要明确：

- 应用图标与版本号。
- 代码签名。
- 安装、升级和卸载时用户数据保留策略：程序目录与
  `%LOCALAPPDATA%\DeskZone` 必须严格分离。
- 自动更新策略。

## 使用方式

在仓库根目录执行：

```powershell
# 发布运行包；默认依赖目标电脑已安装的 .NET 10 Runtime
powershell -ExecutionPolicy Bypass -File .\installer\Publish-DeskZone.ps1

# 如果已恢复 win-x64 的 .NET 10 Runtime 包，也可以发布自包含版本
powershell -ExecutionPolicy Bypass -File .\installer\Publish-DeskZone.ps1 -SelfContained true

# 首次安装或升级安装；同一命令可重复执行
powershell -ExecutionPolicy Bypass -File .\installer\Install-DeskZone.ps1 -CreateDesktopShortcut

# 检查程序和用户数据是否存在
powershell -ExecutionPolicy Bypass -File .\installer\Install-DeskZone.ps1 -Action Verify

# 卸载程序，但保留用户数据
powershell -ExecutionPolicy Bypass -File .\installer\Install-DeskZone.ps1 -Action Uninstall
```

也可以双击 `Install-DeskZone.cmd`，它会调用同目录下的 PowerShell 安装脚本。

默认程序目录为：

```text
%LOCALAPPDATA%\Programs\DeskZone
```

默认用户数据目录为：

```text
%LOCALAPPDATA%\DeskZone
```

两个目录严格分离。升级安装前，脚本会关闭旧进程、创建升级前数据备份、以临时目录完成程序文件替换，并在替换失败时恢复旧程序目录。

## 不可违反的安装器规则

- 升级时只替换程序文件，不删除或覆盖 `%LOCALAPPDATA%\DeskZone`。
- 普通卸载只移除程序，不删除用户数据库、设置和备份。
- “卸载并清除数据”如果未来提供，必须是单独的、明确确认的操作。
- 安装器必须保留稳定的产品标识和升级标识，避免升级被识别成另一份独立安装。
