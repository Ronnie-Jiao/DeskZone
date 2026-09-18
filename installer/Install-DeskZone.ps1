[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall', 'Verify')]
    [string]$Action = 'Install',
    [string]$SourcePath = '',
    [string]$InstallPath = '',
    [switch]$CreateDesktopShortcut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$dataPath = Join-Path $env:LOCALAPPDATA 'DeskZone'
if ([string]::IsNullOrWhiteSpace($InstallPath)) {
    $InstallPath = Join-Path $env:LOCALAPPDATA 'Programs\DeskZone'
}
$InstallPath = [IO.Path]::GetFullPath($InstallPath)

function Assert-SafeInstallPath {
    param([Parameter(Mandatory)][string]$Path)

    $fullPath = [IO.Path]::GetFullPath($Path).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $dataFullPath = [IO.Path]::GetFullPath($dataPath).TrimEnd([IO.Path]::DirectorySeparatorChar)
    $leaf = Split-Path -Leaf $fullPath

    if ([string]::IsNullOrWhiteSpace($leaf) -or $leaf -ne 'DeskZone') {
        throw "安装目录必须是名为 DeskZone 的专用目录，当前值：$fullPath"
    }
    if ($fullPath -eq $dataFullPath -or $fullPath.StartsWith($dataFullPath + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "安装目录不能位于用户数据目录中：$dataFullPath"
    }
    if ($fullPath -eq [IO.Path]::GetPathRoot($fullPath).TrimEnd([IO.Path]::DirectorySeparatorChar)) {
        throw "不允许把磁盘根目录作为安装目录：$fullPath"
    }

    return $fullPath
}

function Stop-DeskZone {
    $processes = @(Get-Process -Name 'DeskZone' -ErrorAction SilentlyContinue)
    foreach ($process in $processes) {
        if ($process.MainWindowHandle -ne 0) {
            [void]$process.CloseMainWindow()
        }
    }

    $deadline = [DateTime]::UtcNow.AddSeconds(10)
    while ([DateTime]::UtcNow -lt $deadline) {
        $remaining = @(Get-Process -Name 'DeskZone' -ErrorAction SilentlyContinue)
        if ($remaining.Count -eq 0) {
            return
        }
        Start-Sleep -Milliseconds 250
    }

    Get-Process -Name 'DeskZone' -ErrorAction SilentlyContinue | Stop-Process -Force
}

function Backup-UserData {
    if (-not (Test-Path -LiteralPath $dataPath)) {
        return $null
    }

    $backupRoot = Join-Path $dataPath 'backups'
    $stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
    $backupPath = Join-Path $backupRoot "installer-before-update-$stamp"
    New-Item -ItemType Directory -Path $backupPath -Force | Out-Null

    foreach ($name in @('deskzone.db', 'deskzone.db-wal', 'deskzone.db-shm', 'settings.json')) {
        $source = Join-Path $dataPath $name
        if (Test-Path -LiteralPath $source) {
            Copy-Item -LiteralPath $source -Destination $backupPath -Force
        }
    }

    $manifest = @{
        CreatedAtUtc = [DateTime]::UtcNow.ToString('O')
        Reason = 'Before installer update'
        DataDirectory = $dataPath
        InstallDirectory = $InstallPath
    }
    $manifest | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $backupPath 'backup-manifest.json') -Encoding UTF8
    return $backupPath
}

function Set-Shortcut {
    param(
        [Parameter(Mandatory)][string]$ShortcutPath,
        [Parameter(Mandatory)][string]$TargetPath,
        [Parameter(Mandatory)][string]$WorkingDirectory
    )

    $parent = Split-Path -Parent $ShortcutPath
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($ShortcutPath)
    $shortcut.TargetPath = $TargetPath
    $shortcut.WorkingDirectory = $WorkingDirectory
    $shortcut.IconLocation = "$TargetPath,0"
    $shortcut.Description = '桌序 DeskZone'
    $shortcut.Save()
}

function Remove-ShortcutIfPresent {
    param([Parameter(Mandatory)][string]$Path)
    if (Test-Path -LiteralPath $Path) {
        Remove-Item -LiteralPath $Path -Force
    }
}

function Install-DeskZone {
    param([Parameter(Mandatory)][string]$PackagePath)

    if (-not (Test-Path -LiteralPath $PackagePath -PathType Container)) {
        throw "发布目录不存在：$PackagePath。请先运行 Publish-DeskZone.ps1。"
    }
    $sourceExe = Join-Path $PackagePath 'DeskZone.exe'
    if (-not (Test-Path -LiteralPath $sourceExe -PathType Leaf)) {
        throw "发布目录中没有 DeskZone.exe：$PackagePath"
    }

    $backupPath = $null
    $stagePath = Join-Path (Split-Path -Parent $InstallPath) ".DeskZone-staging-$PID-$(Get-Date -Format yyyyMMddHHmmss)"
    $previousPath = Join-Path (Split-Path -Parent $InstallPath) ".DeskZone-previous-$PID-$(Get-Date -Format yyyyMMddHHmmss)"
    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null

    try {
        Stop-DeskZone
        $backupPath = Backup-UserData

        Get-ChildItem -LiteralPath $PackagePath -Force | Copy-Item -Destination $stagePath -Recurse -Force

        $hadExistingInstall = Test-Path -LiteralPath $InstallPath
        if ($hadExistingInstall) {
            Move-Item -LiteralPath $InstallPath -Destination $previousPath
        }

        try {
            Move-Item -LiteralPath $stagePath -Destination $InstallPath
        }
        catch {
            if ($hadExistingInstall -and (Test-Path -LiteralPath $previousPath)) {
                Move-Item -LiteralPath $previousPath -Destination $InstallPath
            }
            throw
        }

        $metadata = @{
            InstalledAtUtc = [DateTime]::UtcNow.ToString('O')
            InstallDirectory = $InstallPath
            DataDirectory = $dataPath
            DataPolicy = 'User data is never removed by update or normal uninstall.'
        }
        $metadata | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $InstallPath '.deskzone-install.json') -Encoding UTF8

        if (Test-Path -LiteralPath $previousPath) {
            Remove-Item -LiteralPath $previousPath -Recurse -Force
        }

        $startMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'DeskZone.lnk'
        Set-Shortcut -ShortcutPath $startMenuShortcut -TargetPath (Join-Path $InstallPath 'DeskZone.exe') -WorkingDirectory $InstallPath

        if ($CreateDesktopShortcut) {
            $desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'DeskZone.lnk'
            Set-Shortcut -ShortcutPath $desktopShortcut -TargetPath (Join-Path $InstallPath 'DeskZone.exe') -WorkingDirectory $InstallPath
        }

        Write-Host "DeskZone 安装/升级完成。"
        Write-Host "程序目录：$InstallPath"
        Write-Host "数据目录：$dataPath（未删除）"
        if ($backupPath) {
            Write-Host "升级前备份：$backupPath"
        }
    }
    finally {
        if (Test-Path -LiteralPath $stagePath) {
            Remove-Item -LiteralPath $stagePath -Recurse -Force
        }
    }
}

function Uninstall-DeskZone {
    Stop-DeskZone

    $startMenuShortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'DeskZone.lnk'
    $desktopShortcut = Join-Path ([Environment]::GetFolderPath('DesktopDirectory')) 'DeskZone.lnk'
    Remove-ShortcutIfPresent $startMenuShortcut
    Remove-ShortcutIfPresent $desktopShortcut

    if (Test-Path -LiteralPath $InstallPath) {
        Remove-Item -LiteralPath $InstallPath -Recurse -Force
    }

    Write-Host "DeskZone 程序已卸载。"
    Write-Host "用户数据已保留：$dataPath"
}

function Verify-DeskZone {
    Assert-SafeInstallPath $InstallPath | Out-Null
    $exePath = Join-Path $InstallPath 'DeskZone.exe'
    Write-Host "程序目录：$InstallPath"
    Write-Host "程序存在：$(Test-Path -LiteralPath $exePath)"
    Write-Host "数据目录：$dataPath"
    Write-Host "数据目录存在：$(Test-Path -LiteralPath $dataPath)"
    foreach ($name in @('deskzone.db', 'settings.json', 'backups')) {
        Write-Host "  $name：$(Test-Path -LiteralPath (Join-Path $dataPath $name))"
    }
}

Assert-SafeInstallPath $InstallPath | Out-Null

switch ($Action) {
    'Install' {
        if ([string]::IsNullOrWhiteSpace($SourcePath)) {
            $SourcePath = Join-Path $PSScriptRoot 'output\DeskZone'
        }
        Install-DeskZone ([IO.Path]::GetFullPath($SourcePath))
    }
    'Uninstall' { Uninstall-DeskZone }
    'Verify' { Verify-DeskZone }
}
