# Installer

当前提供可直接运行的 PowerShell 安装/升级脚本；正式发布阶段可在此基础上封装为签名的 MSIX、WiX 或自解压安装包。所有安装方式都必须遵守
[`docs/INSTALLATION-AND-UPDATES.md`](../docs/INSTALLATION-AND-UPDATES.md) 中的数据保留约束。

仓库现在也提供单文件 EXE 安装包构建脚本。它使用 Windows IExpress 将自包含发布文件和数据保留安装脚本封装为一个 `DeskZone-Setup.exe`。每次重新打包都会覆盖
`installer\output\DeskZone-Setup.exe`，不会按时间戳生成新的安装包文件；安装包文件和安装向导窗口都使用 DeskZone 图标。

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

# 生成单文件 EXE 安装包；默认使用目标电脑的 .NET 10 Desktop Runtime
powershell -ExecutionPolicy Bypass -File .\installer\Build-DeskZoneInstaller.ps1

# 如果本机已缓存对应运行时包，也可以生成自包含安装包
powershell -ExecutionPolicy Bypass -File .\installer\Build-DeskZoneInstaller.ps1 -SelfContained $true

# 如果已恢复 win-x64 的 .NET 10 Runtime 包，也可以发布自包含版本
powershell -ExecutionPolicy Bypass -File .\installer\Publish-DeskZone.ps1 -SelfContained true

# 打开带有目录选择、进度和完成页的安装向导
powershell -ExecutionPolicy Bypass -File .\installer\Install-DeskZone.ps1 -Interactive

# 检查程序和用户数据是否存在
powershell -ExecutionPolicy Bypass -File .\installer\Install-DeskZone.ps1 -Action Verify

# 卸载程序，但保留用户数据
powershell -ExecutionPolicy Bypass -File .\installer\Install-DeskZone.ps1 -Action Uninstall
```

也可以双击 `Install-DeskZone.cmd`，它会打开安装向导。首次安装完成后默认启用开机自动启动，并自动创建桌面和开始菜单快捷方式；升级安装会保留用户原来的开机启动选择，并更新已有快捷方式，不会重复创建桌面入口。若桌面上已有多个指向 DeskZone 正式程序的快捷方式，更新时会保留一个并清理重复入口；带 `--preview` 参数的预览快捷方式不会被修改。

默认程序目录为：

```text
%LOCALAPPDATA%\Programs\DeskZone
```

默认用户数据目录为：

```text
%LOCALAPPDATA%\DeskZone
```

两个目录严格分离。升级安装前，脚本会关闭旧进程、创建升级前数据备份、以临时目录完成程序文件替换，并在替换失败时恢复旧程序目录。
安装向导允许选择程序目录，显示安装进度，结束后可直接打开 DeskZone；首次安装会创建快捷方式，升级时会更新已有快捷方式而不会重复创建。选择过的安装目录会记录在用户数据目录中，后续升级默认仍使用同一目录。

## 不可违反的安装器规则

- 升级时只替换程序文件，不删除或覆盖 `%LOCALAPPDATA%\DeskZone`。
- 普通卸载只移除程序，不删除用户数据库、设置和备份。
- “卸载并清除数据”如果未来提供，必须是单独的、明确确认的操作。
- 安装器必须保留稳定的产品标识和升级标识，避免升级被识别成另一份独立安装。
