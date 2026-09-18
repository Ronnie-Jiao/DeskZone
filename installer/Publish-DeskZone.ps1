[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [string]$RuntimeIdentifier = 'win-x64',
    [string]$OutputPath = '',
    [ValidateSet('true', 'false', 'True', 'False', '1', '0')]
    [string]$SelfContained = 'false'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\DeskZone.App\DeskZone.App.csproj'
if ([string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = Join-Path $PSScriptRoot 'output\DeskZone'
}

$OutputPath = [IO.Path]::GetFullPath($OutputPath)
if (Test-Path -LiteralPath $OutputPath) {
    Remove-Item -LiteralPath $OutputPath -Recurse -Force
}
New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null

$selfContainedValue = if ($SelfContained -in @('true', 'True', '1')) { 'true' } else { 'false' }
$publishArguments = @(
    'publish',
    $projectPath,
    '--configuration', $Configuration,
    '--self-contained', $selfContainedValue,
    '--output', $OutputPath
)

# A framework-dependent Windows publish does not need a RID. Omitting it keeps
# the publish usable on machines where the .NET runtime pack is not cached;
# self-contained builds still carry the requested win-x64 RID and runtime.
if ($selfContainedValue -eq 'true') {
    $publishArguments = @(
        'publish',
        $projectPath,
        '--configuration', $Configuration,
        '--runtime', $RuntimeIdentifier,
        '--self-contained', $selfContainedValue,
        '--output', $OutputPath
    )
}

Write-Host "正在发布 DeskZone 到 $OutputPath ..."
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish 失败，退出码：$LASTEXITCODE"
}

$exePath = Join-Path $OutputPath 'DeskZone.exe'
if (-not (Test-Path -LiteralPath $exePath)) {
    throw "发布结果中没有找到 DeskZone.exe：$exePath"
}

Write-Host "发布完成：$OutputPath"
