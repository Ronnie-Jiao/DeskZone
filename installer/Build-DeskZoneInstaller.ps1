[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputPath = '',
    [string]$InstallerPath = '',
    [bool]$SelfContained = $false
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$installerRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $installerRoot
$publishScript = Join-Path $installerRoot 'Publish-DeskZone.ps1'
$installScript = Join-Path $installerRoot 'Install-DeskZone.ps1'
$installCommand = Join-Path $installerRoot 'Install-DeskZone.cmd'
$iconAsset = Join-Path $repoRoot 'src\DeskZone.App\Assets\DeskZoneLogo.ico'
$iconUpdaterScript = Join-Path $installerRoot 'Set-DeskZoneInstallerIcon.ps1'
$backgroundAsset = Join-Path $installerRoot 'assets\InstallerBackground.png'
$logoAsset = Join-Path $installerRoot 'assets\DeskZoneLogoCompact.png'
$folderIconAsset = Join-Path $installerRoot 'assets\InstallerFolderIcon.png'

if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $installerRoot 'output\DeskZone'
}
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $installerRoot 'output\DeskZone-Setup.exe'
}

$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
$installerDirectory = Split-Path -Parent $InstallerPath
$stagePath = Join-Path ([IO.Path]::GetTempPath()) "DeskZone-installer-stage-$PID-$(Get-Date -Format yyyyMMddHHmmss)"
$sedPath = Join-Path ([IO.Path]::GetTempPath()) "DeskZone-installer-$PID.sed"

if (-not (Test-Path -LiteralPath $publishScript -PathType Leaf)) {
    throw "找不到发布脚本：$publishScript"
}
if (-not (Test-Path -LiteralPath $installScript -PathType Leaf)) {
    throw "找不到安装脚本：$installScript"
}
if (-not (Test-Path -LiteralPath $installCommand -PathType Leaf)) {
    throw "找不到安装入口：$installCommand"
}
if (-not (Test-Path -LiteralPath $iconUpdaterScript -PathType Leaf)) {
    throw "找不到安装包图标处理脚本：$iconUpdaterScript"
}
foreach ($asset in @($iconAsset, $backgroundAsset, $logoAsset, $folderIconAsset)) {
    if (-not (Test-Path -LiteralPath $asset -PathType Leaf)) {
        throw "找不到安装器界面资源：$asset"
    }
}

try {
    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $publishScript `
        -Configuration $Configuration `
        -RuntimeIdentifier $RuntimeIdentifier `
        -OutputPath $OutputPath `
        -SelfContained ($(if ($SelfContained) { 'true' } else { 'false' }))
    if ($LASTEXITCODE -ne 0) {
        throw "发布运行包失败，退出码：$LASTEXITCODE"
    }

    $publishedExe = Join-Path $OutputPath 'DeskZone.exe'
    if (-not (Test-Path -LiteralPath $publishedExe -PathType Leaf)) {
        throw "发布目录中没有 DeskZone.exe：$publishedExe"
    }

    New-Item -ItemType Directory -Path $stagePath -Force | Out-Null
    Copy-Item -LiteralPath $installScript -Destination (Join-Path $stagePath 'Install-DeskZone.ps1') -Force
    Copy-Item -LiteralPath $installCommand -Destination (Join-Path $stagePath 'Install-DeskZone.cmd') -Force
    Copy-Item -LiteralPath $iconAsset -Destination (Join-Path $stagePath 'DeskZoneLogo.ico') -Force
    # IExpress 会把 SED 中的嵌套目录展开到解压根目录；资源放在根目录，
    # 避免安装包运行时找不到安装器背景和图标。
    Copy-Item -LiteralPath $backgroundAsset -Destination (Join-Path $stagePath 'InstallerBackground.png') -Force
    Copy-Item -LiteralPath $logoAsset -Destination (Join-Path $stagePath 'DeskZoneLogoCompact.png') -Force
    Copy-Item -LiteralPath $folderIconAsset -Destination (Join-Path $stagePath 'InstallerFolderIcon.png') -Force
    Get-ChildItem -LiteralPath $OutputPath -Force -File | Copy-Item -Destination $stagePath -Force

    # Microsoft.Data.Sqlite 的 Windows 原生库位于 runtimes 子目录。
    # 安装包只需要目标 Windows 架构的运行库，避免把其它平台的文件一并打进去。
    $runtimeSourcePath = Join-Path $OutputPath (Join-Path 'runtimes' $RuntimeIdentifier)
    if (Test-Path -LiteralPath $runtimeSourcePath -PathType Container) {
        $runtimeStageRoot = Join-Path $stagePath 'runtimes'
        New-Item -ItemType Directory -Path $runtimeStageRoot -Force | Out-Null
        Copy-Item -LiteralPath $runtimeSourcePath -Destination $runtimeStageRoot -Recurse -Force
    }

    $nativeSqlitePath = Join-Path $stagePath (Join-Path (Join-Path 'runtimes' $RuntimeIdentifier) 'native\e_sqlite3.dll')
    if (-not (Test-Path -LiteralPath $nativeSqlitePath -PathType Leaf)) {
        throw "安装包暂存目录中缺少 SQLite Windows 原生库：$nativeSqlitePath"
    }

    $files = @(Get-ChildItem -LiteralPath $stagePath -Force -File -Recurse | Sort-Object FullName)
    if ($files.Count -eq 0) {
        throw "安装包暂存目录为空：$stagePath"
    }

    $sedLines = [System.Collections.Generic.List[string]]::new()
    $sedLines.Add('[Version]')
    $sedLines.Add('Class=IEXPRESS')
    $sedLines.Add('SEDVersion=3')
    $sedLines.Add('[Options]')
    $sedLines.Add('PackagePurpose=InstallApp')
    $sedLines.Add('ShowInstallProgramWindow=1')
    $sedLines.Add('HideExtractAnimation=1')
    $sedLines.Add('UseLongFileName=1')
    $sedLines.Add('InsideCompressed=1')
    $sedLines.Add('CAB_FixedSize=0')
    $sedLines.Add('CAB_ResvCodeSigning=0')
    $sedLines.Add('RebootMode=N')
    # 不显示 IExpress 外层确认框，直接进入自定义中文安装界面。
    # 安装/更新策略由 Install-DeskZone.ps1 统一处理：已有安装沿用原目录，用户数据目录不删除。
    $sedLines.Add('InstallPrompt=')
    $sedLines.Add('DisplayLicense=')
    $sedLines.Add('FinishMessage=')
    $sedLines.Add('TargetName=%TargetName%')
    $sedLines.Add('FriendlyName=%FriendlyName%')
    $sedLines.Add('AppLaunched=cmd.exe /c Install-DeskZone.cmd')
    $sedLines.Add('PostInstallCmd=<None>')
    $sedLines.Add('AdminQuietInstCmd=')
    $sedLines.Add('UserQuietInstCmd=')
    $stringLines = [System.Collections.Generic.List[string]]::new()
    $stringLines.Add('[Strings]')
    $stringLines.Add(('TargetName={0}' -f $InstallerPath))
    $stringLines.Add('FriendlyName=DeskZone 安装程序')

    for ($index = 0; $index -lt $files.Count; $index++) {
        $relativeName = $files[$index].FullName.Substring($stagePath.Length).TrimStart('\', '/')
        $relativeName = $relativeName -replace '/', '\'
        $stringLines.Add(('FILE{0}="{1}"' -f $index, $relativeName))
    }

    $sedLines.Add('SourceFiles=SourceFiles')
    $sedLines.Add('[SourceFiles]')
    $sedLines.Add("SourceFiles0=$stagePath\")
    $sedLines.Add('[SourceFiles0]')
    for ($index = 0; $index -lt $files.Count; $index++) {
        $sedLines.Add("%FILE$index%=")
    }
    $sedLines.AddRange($stringLines)

    $sedEncoding = [Text.Encoding]::GetEncoding([Globalization.CultureInfo]::CurrentUICulture.TextInfo.ANSICodePage)
    [IO.File]::WriteAllText($sedPath, ($sedLines -join "`n"), $sedEncoding)

    New-Item -ItemType Directory -Path $installerDirectory -Force | Out-Null
    if (Test-Path -LiteralPath $InstallerPath) {
        Remove-Item -LiteralPath $InstallerPath -Force
    }

    $iexpress = Join-Path $env:WINDIR 'System32\iexpress.exe'
    if (-not (Test-Path -LiteralPath $iexpress -PathType Leaf)) {
        throw "系统中找不到 IExpress：$iexpress"
    }

    $iexpressProcess = Start-Process -FilePath $iexpress `
        -ArgumentList @('/N', '/Q', $sedPath) `
        -Wait `
        -PassThru
    if ($iexpressProcess.ExitCode -ne 0) {
        throw "IExpress 打包失败，退出码：$($iexpressProcess.ExitCode)"
    }
    if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf)) {
        throw "IExpress 未生成安装包：$InstallerPath"
    }

    & powershell.exe -NoProfile -ExecutionPolicy Bypass -File $iconUpdaterScript `
        -ExecutablePath $InstallerPath `
        -IconPath $iconAsset
    if ($LASTEXITCODE -ne 0) {
        throw "安装包图标处理失败，退出码：$LASTEXITCODE"
    }

    $sizeMb = [Math]::Round((Get-Item -LiteralPath $InstallerPath).Length / 1MB, 1)
    Write-Host ('DeskZone 安装包已生成：{0}（{1} MB）' -f $InstallerPath, $sizeMb)
}
finally {
    if ($env:DESKZONE_KEEP_INSTALLER_DEBUG -ne '1') {
        if (Test-Path -LiteralPath $stagePath) {
            Remove-Item -LiteralPath $stagePath -Recurse -Force
        }
        if (Test-Path -LiteralPath $sedPath) {
            Remove-Item -LiteralPath $sedPath -Force
        }
    }
}
