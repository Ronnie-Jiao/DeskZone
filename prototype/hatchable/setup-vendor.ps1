$ErrorActionPreference = "Stop"

$vendor = Join-Path $PSScriptRoot "public\vendor"
New-Item -ItemType Directory -Force -Path $vendor | Out-Null

Invoke-WebRequest "https://unpkg.com/react@18.3.1/umd/react.production.min.js" -OutFile (Join-Path $vendor "react.js")
Invoke-WebRequest "https://unpkg.com/react-dom@18.3.1/umd/react-dom.production.min.js" -OutFile (Join-Path $vendor "react-dom.js")
Invoke-WebRequest "https://unpkg.com/htm@3.1.1/dist/htm.js" -OutFile (Join-Path $vendor "htm.js")

Write-Host "DeskZone vendor dependencies downloaded." -ForegroundColor Green
