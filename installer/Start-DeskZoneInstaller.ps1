[CmdletBinding()]
param(
    [ValidateSet('Install', 'Uninstall')]
    [string]$Action = 'Install',
    [string]$SourcePath = '',
    [string]$InstallPath = '',
    [switch]$CreateDesktopShortcut
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$transcriptStarted = $false
$logPath = ''
try {
    $logDirectory = Join-Path $env:LOCALAPPDATA 'DeskZone\logs'
    New-Item -ItemType Directory -Path $logDirectory -Force | Out-Null
    $logPath = Join-Path $logDirectory "installer-$(Get-Date -Format yyyyMMdd-HHmmss)-$PID.log"
    Start-Transcript -LiteralPath $logPath | Out-Null
    $transcriptStarted = $true
    $installScript = Join-Path $PSScriptRoot 'Install-DeskZone.ps1'
    if (-not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
        throw "找不到安装脚本：$installScript"
    }
    $installArguments = @{ Action = $Action; Interactive = $true }
    if (-not [string]::IsNullOrWhiteSpace($SourcePath)) { $installArguments.SourcePath = $SourcePath }
    if (-not [string]::IsNullOrWhiteSpace($InstallPath)) { $installArguments.InstallPath = $InstallPath }
    if ($CreateDesktopShortcut) { $installArguments.CreateDesktopShortcut = $true }
    & $installScript @installArguments
}
catch {
    $failure = $_ | Out-String
    $message = "安装程序无法继续。`r`n$($_.Exception.Message)"
    if (-not [string]::IsNullOrWhiteSpace($logPath)) {
        try { Add-Content -LiteralPath $logPath -Value $failure -Encoding UTF8 } catch { }
        $message += "`r`n`r`n日志：$logPath"
    }
    try {
        Add-Type -AssemblyName System.Windows.Forms
        [void][System.Windows.Forms.MessageBox]::Show($message, 'DeskZone 安装失败', 'OK', 'Error')
    }
    catch {
        $shell = New-Object -ComObject WScript.Shell
        [void]$shell.Popup($message, 0, 'DeskZone 安装失败', 16)
    }
    exit 1
}
finally {
    if ($transcriptStarted) {
        try { Stop-Transcript | Out-Null } catch { }
    }
}
