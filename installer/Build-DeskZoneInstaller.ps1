[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [ValidateSet('win-x64')]
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputPath = '',
    [string]$InstallerPath = '',
    [bool]$SelfContained = $true,
    [string]$CompilerPath = ''
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$installerRoot = $PSScriptRoot
$repoRoot = Split-Path -Parent $installerRoot
if ([string]::IsNullOrWhiteSpace($InstallerPath)) {
    $InstallerPath = Join-Path $repoRoot "artifacts\windows-installer-$(Get-Date -Format yyyyMMdd-HHmmss)\DeskZone-Setup.exe"
}
$InstallerPath = [IO.Path]::GetFullPath($InstallerPath)
$installerDirectory = Split-Path -Parent $InstallerPath
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $installerDirectory 'publish'
}
$OutputPath = [IO.Path]::GetFullPath($OutputPath)
$stagePath = Join-Path $installerDirectory 'package-source'

# Refuse existing output so an old EXE cannot be mistaken for a successful build.
foreach ($path in @($InstallerPath, $OutputPath, $stagePath)) {
    if (Test-Path -LiteralPath $path) {
        throw "输出已存在，请指定新的安装包/发布目录：$path"
    }
}
if ([string]::IsNullOrWhiteSpace($CompilerPath)) {
    $compilerCandidates = @(
        (Join-Path $env:LOCALAPPDATA 'Programs\Inno Setup 6\ISCC.exe'),
        (Join-Path ${env:ProgramFiles(x86)} 'Inno Setup 6\ISCC.exe'),
        (Join-Path $env:ProgramFiles 'Inno Setup 6\ISCC.exe')
    )
    $compilerCommand = Get-Command ISCC.exe -ErrorAction SilentlyContinue
    if ($null -ne $compilerCommand) {
        $compilerCandidates = @($compilerCommand.Source) + $compilerCandidates
    }
    $CompilerPath = $compilerCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf } | Select-Object -First 1
}
if ([string]::IsNullOrWhiteSpace($CompilerPath) -or -not (Test-Path -LiteralPath $CompilerPath -PathType Leaf)) {
    throw '找不到已安装的 Inno Setup 6 编译器。可通过 -CompilerPath 指定 ISCC.exe；本脚本不会联网安装工具。'
}
$CompilerPath = [IO.Path]::GetFullPath($CompilerPath)
$definitionPath = Join-Path $installerRoot 'DeskZone.iss'
$iconAsset = Join-Path $repoRoot 'src\DeskZone.App\Assets\DeskZoneLogo.ico'
$packageSources = @{
    'Install-DeskZone.ps1' = (Join-Path $installerRoot 'Install-DeskZone.ps1')
    'Start-DeskZoneInstaller.ps1' = (Join-Path $installerRoot 'Start-DeskZoneInstaller.ps1')
    'Install-DeskZone.cmd' = (Join-Path $installerRoot 'Install-DeskZone.cmd')
    'DeskZoneLogo.ico' = $iconAsset
    'DeskZoneLogoCompact.png' = (Join-Path $installerRoot 'assets\DeskZoneLogoCompact.png')
    'InstallerFolderIcon.png' = (Join-Path $installerRoot 'assets\InstallerFolderIcon.png')
}
foreach ($path in @($definitionPath) + @($packageSources.Values)) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "找不到安装包源文件：$path"
    }
}

& (Join-Path $installerRoot 'Publish-DeskZone.ps1') -Configuration $Configuration `
    -RuntimeIdentifier $RuntimeIdentifier -OutputPath $OutputPath `
    -SelfContained ($(if ($SelfContained) { 'true' } else { 'false' }))

$nativeSqliteCandidates = @(
    (Join-Path $OutputPath 'e_sqlite3.dll'),
    (Join-Path $OutputPath "runtimes\$RuntimeIdentifier\native\e_sqlite3.dll")
)
if (-not ($nativeSqliteCandidates | Where-Object { Test-Path -LiteralPath $_ -PathType Leaf })) {
    throw '发布目录中缺少 SQLite Windows 原生库。'
}
New-Item -ItemType Directory -Path $stagePath | Out-Null
foreach ($name in $packageSources.Keys) {
    if (Test-Path -LiteralPath (Join-Path $OutputPath $name)) {
        throw "安装器文件与发布文件重名：$name"
    }
    Copy-Item -LiteralPath $packageSources[$name] -Destination (Join-Path $stagePath $name)
}

# Preserve nested runtime/localization files, with a hashed inventory.
$manifest = @(
    foreach ($root in @($OutputPath, $stagePath)) {
        foreach ($file in Get-ChildItem -LiteralPath $root -Recurse -File -Force | Sort-Object FullName) {
            [PSCustomObject]@{
                Path = $file.FullName.Substring($root.Length).TrimStart('\', '/')
                Length = $file.Length
                SHA256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash
            }
        }
    }
)
$manifest | ConvertTo-Json -Depth 3 | Set-Content -LiteralPath (Join-Path $installerDirectory 'package-manifest.json') -Encoding UTF8
$compilerArguments = @(
    "/DPublishSource=$OutputPath",
    "/DInstallerSource=$stagePath",
    "/DIconFile=$iconAsset",
    "/O$installerDirectory",
    "/F$([IO.Path]::GetFileNameWithoutExtension($InstallerPath))",
    $definitionPath
)
$compilerOutput = & $CompilerPath @compilerArguments 2>&1
$compilerExitCode = $LASTEXITCODE
$compilerOutput | Set-Content -LiteralPath (Join-Path $installerDirectory 'compiler.log') -Encoding UTF8
$compilerOutput | Select-Object -Last 6 | ForEach-Object { Write-Host $_ }
if ($compilerExitCode -ne 0) {
    throw "Inno Setup 编译失败，退出码：$compilerExitCode。请查看 compiler.log；不要交付残留 EXE。"
}
if (-not (Test-Path -LiteralPath $InstallerPath -PathType Leaf) -or (Get-Item -LiteralPath $InstallerPath).Length -eq 0) {
    throw "Inno Setup 未生成有效输出：$InstallerPath"
}

# Embed the icon during compilation; never rewrite a completed SFX's resources.
$installerFile = Get-Item -LiteralPath $InstallerPath
$compilerVersion = [regex]::Match(($compilerOutput -join "`n"), 'Compiler engine version: Inno Setup ([^\r\n]+)').Groups[1].Value
[PSCustomObject]@{
    Installer = $InstallerPath
    Length = $installerFile.Length
    SHA256 = (Get-FileHash -LiteralPath $InstallerPath -Algorithm SHA256).Hash
    PayloadFiles = $manifest.Count
    Compiler = $CompilerPath
    CompilerVersion = $compilerVersion
    Configuration = $Configuration
    RuntimeIdentifier = $RuntimeIdentifier
    SelfContained = $SelfContained
    InstallerDefinitionSHA256 = (Get-FileHash -LiteralPath $definitionPath -Algorithm SHA256).Hash
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $installerDirectory 'build-receipt.json') -Encoding UTF8
Write-Host "DeskZone 安装包已生成：$InstallerPath（$($installerFile.Length) 字节，$($manifest.Count) 个文件）"
