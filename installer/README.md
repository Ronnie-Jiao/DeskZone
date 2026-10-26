# Installer

当前提供可直接运行的 PowerShell 安装/升级脚本；正式发布阶段可在此基础上封装为签名的 MSIX、WiX 或自解压安装包。所有安装方式都必须遵守
[`docs/INSTALLATION-AND-UPDATES.md`](../docs/INSTALLATION-AND-UPDATES.md) 中的数据保留约束。

仓库提供单文件 EXE 安装包构建脚本，使用本机已安装的 Inno Setup 6 将完整自包含发布目录（含子目录）和数据保留安装脚本封装为 `DeskZone-Setup.exe`。默认输出到新的
`artifacts\windows-installer-yyyyMMdd-HHmmss\`，保留旧产物；显式指定的发布目录或安装包已存在时会拒绝覆盖。安装包图标在编译时嵌入，不再对完成后的 EXE 修改资源。

Inno Setup 只负责显示解压进度、解压和等待安装向导退出，不注册第二份产品或卸载入口。目录选择、升级备份、程序替换、快捷方式和普通卸载仍由现有 PowerShell 向导处理。

发布前需要明确：

- 应用图标与版本号。
- 代码签名。
- 安装、升级和卸载时用户数据保留策略：程序目录与
  `%LOCALAPPDATA%\DeskZone` 必须严格分离。
- 自动更新策略。

## 使用方式

在仓库根目录执行：

```powershell
# 发布自包含运行包；默认不要求目标电脑预装 .NET 10 Desktop Runtime
powershell -ExecutionPolicy Bypass -File .\installer\Publish-DeskZone.ps1

# 生成 x64 自包含 EXE 安装包；不要求目标电脑预装 .NET 10 Desktop Runtime
powershell -ExecutionPolicy Bypass -File .\installer\Build-DeskZoneInstaller.ps1

# 编译器不在常见目录或 PATH 时，显式指定本机已有的 ISCC.exe
# powershell -ExecutionPolicy Bypass -File .\installer\Build-DeskZoneInstaller.ps1 -CompilerPath "C:\工具\Inno Setup 6\ISCC.exe"

# 如明确确认目标电脑已经安装对应运行时，才使用框架依赖版本
powershell -ExecutionPolicy Bypass -File .\installer\Publish-DeskZone.ps1 -SelfContained false

# 打开带有目录选择、进度和完成页的安装向导
powershell -STA -ExecutionPolicy Bypass -File .\installer\Start-DeskZoneInstaller.ps1

# 检查程序和用户数据是否存在
powershell -ExecutionPolicy Bypass -File .\installer\Install-DeskZone.ps1 -Action Verify

# 卸载程序，但保留用户数据
powershell -ExecutionPolicy Bypass -File .\installer\Install-DeskZone.ps1 -Action Uninstall
```

也可以双击 `Install-DeskZone.cmd`，它会打开安装向导。首次安装完成后默认启用开机自动启动，并自动创建桌面和开始菜单快捷方式；升级安装会保留用户原来的开机启动选择，并更新已有快捷方式，不会重复创建桌面入口。若桌面上已有多个指向 DeskZone 正式程序的快捷方式，更新时会保留一个并清理重复入口；带 `--preview` 参数的预览快捷方式不会被修改。

新用户首次安装时的默认程序目录为：

```text
D:\DeskZone
```

默认用户数据目录为：

```text
%LOCALAPPDATA%\DeskZone
```

两个目录严格分离。已有安装会继续使用用户数据中记录的原安装目录；只有没有历史安装位置的新用户才默认使用 `D:\DeskZone`。升级安装前，脚本会关闭旧进程、创建升级前数据备份、以临时目录完成程序文件替换，并在替换失败时恢复旧程序目录。
安装向导允许选择程序目录，显示安装进度，结束后可直接打开 DeskZone；首次安装会创建快捷方式，升级时会更新已有快捷方式而不会重复创建。选择过的安装目录会记录在用户数据目录中，后续升级默认仍使用同一目录。

安装向导采用精简的目录选择和进度页面。安装后会在 Windows“设置 → 应用 → 已安装的应用”注册 DeskZone；选择“卸载”会打开确认页，普通卸载仅移除程序和快捷方式，分类、设置及备份保留在 `%LOCALAPPDATA%\DeskZone`。

## 不可违反的安装器规则

- 升级时只替换程序文件，不删除或覆盖 `%LOCALAPPDATA%\DeskZone`。
- 普通卸载只移除程序，不删除用户数据库、设置和备份。
- “卸载并清除数据”如果未来提供，必须是单独的、明确确认的操作。
- 安装器必须保留稳定的产品标识和升级标识，避免升级被识别成另一份独立安装。

## 构建核对与启动诊断

- 构建仅接受编译器正常退出且生成非空 EXE；同目录保留 `compiler.log`、全部文件的长度/SHA-256 清单 `package-manifest.json` 和安装包哈希回执 `build-receipt.json`。编译失败留下的文件不能交付。
- 启动入口 `Start-DeskZoneInstaller.ps1` 捕获安装脚本加载、路径初始化和界面创建错误，显示错误提示并返回非零退出码。日志位于 `%LOCALAPPDATA%\DeskZone\logs\installer-yyyyMMdd-HHmmss-PID.log`；安装/卸载过程中已处理的异常也写入日志。
- EXE 直接以绝对路径启动 Windows PowerShell，等待向导退出后才清理临时目录；无法创建进程或入口返回失败时，外层显示错误。双击 CMD 的失败分支会保留控制台提示。
- 构建、PowerShell 语法解析、内嵌 C# 编译和静态文件清单核对均不等同于安装界面或升级功能验收。未获本轮明确授权时，不启动安装器或客户端。
