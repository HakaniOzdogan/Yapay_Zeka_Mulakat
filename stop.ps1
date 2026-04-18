[CmdletBinding()]
param(
    [switch]$RemoveVolumes
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "Stopping Docker services..." -ForegroundColor Yellow
$composeArgs = @("-f", "docker/docker-compose.yml", "down", "--remove-orphans")
if ($RemoveVolumes) {
    $composeArgs += "-v"
}

& docker compose $composeArgs

if ($LASTEXITCODE -ne 0) {
    throw "docker compose failed with exit code $LASTEXITCODE"
}

Write-Host "Done. Services stopped." -ForegroundColor Green
