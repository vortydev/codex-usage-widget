param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Run
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw 'Install the .NET 8 SDK (or a newer compatible SDK) and ensure dotnet is on PATH.'
}

$projectPath = Join-Path $PSScriptRoot 'CodexUsageWidget.csproj'
$executablePath = Join-Path $PSScriptRoot "bin\$Configuration\net8.0-windows\CodexUsageWidget.exe"

Write-Host 'Close any running copy of the widget before rebuilding.'
& dotnet build $projectPath --configuration $Configuration
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Built: $executablePath"
if ($Run) {
    Start-Process -FilePath $executablePath
}
