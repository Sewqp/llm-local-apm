param(
    [ValidateRange(1024,65535)][int]$Port = 5219,
    [string]$DatabasePath
)
$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$runDirectory = Join-Path $repoRoot '.local-evidence/live'
if (!$DatabasePath) { $DatabasePath = Join-Path $runDirectory 'apm-v2.db' }
$DatabasePath = [IO.Path]::GetFullPath($DatabasePath)
New-Item -ItemType Directory -Force -Path (Split-Path -Parent $DatabasePath),$runDirectory | Out-Null
$publishDirectory = Join-Path $runDirectory 'collector'
Push-Location $repoRoot
try {
    npm.cmd --prefix dashboard run build
    if ($LASTEXITCODE -ne 0) { throw 'Dashboard build failed. Install dependencies with npm ci in dashboard first.' }
    dotnet publish src/CollectionServer/CollectionServer.csproj -c Release -o $publishDirectory --nologo
    if ($LASTEXITCODE -ne 0) { throw 'Collector build failed.' }
    New-Item -ItemType Directory -Force -Path (Join-Path $publishDirectory 'wwwroot') | Out-Null
    Copy-Item -Path (Join-Path $repoRoot 'dashboard/dist/*') -Destination (Join-Path $publishDirectory 'wwwroot') -Recurse -Force
    $env:ConnectionStrings__Apm = "Data Source=$DatabasePath"
    $env:ASPNETCORE_URLS = "http://127.0.0.1:$Port"
    Write-Host "Dashboard: http://127.0.0.1:$Port"
    Write-Host "Database: $DatabasePath"
    Set-Location $publishDirectory
    dotnet (Join-Path $publishDirectory 'CollectionServer.dll')
} finally { Pop-Location }
