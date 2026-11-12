$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\DeskZone.App\DeskZone.App.csproj'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    Write-Host '[DeskZone] .NET SDK was not found on PATH.' -ForegroundColor Red
    Read-Host 'Press Enter to close'
    exit 1
}

$runningProcesses = @(Get-Process -Name DeskZone -ErrorAction SilentlyContinue)
if ($runningProcesses.Count -gt 0) {
    $processIds = ($runningProcesses | ForEach-Object Id) -join ', '
    Write-Host "[DeskZone] DeskZone is already running (PID $processIds)." -ForegroundColor Yellow
    Write-Host 'Exit DeskZone completely from the notification area, then start this shortcut again.'
    Read-Host 'Press Enter to close'
    exit 2
}

Write-Host '[DeskZone] Starting Debug live preview.' -ForegroundColor Cyan
Write-Host '[DeskZone] Supported C# edits hot reload; XAML and other unsupported edits rebuild and restart.'
Write-Host '[DeskZone] Keep this window open. Press Ctrl+C to stop the watcher.'

Push-Location $repoRoot
try {
    & dotnet watch --project $projectPath --configuration Debug --non-interactive
    $watchExitCode = $LASTEXITCODE
}
finally {
    Pop-Location
}

if ($watchExitCode -ne 0) {
    Write-Host "[DeskZone] Live preview exited with code $watchExitCode." -ForegroundColor Red
    Read-Host 'Press Enter to close'
}
