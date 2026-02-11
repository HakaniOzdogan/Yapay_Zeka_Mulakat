$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

Write-Host "Starting Docker services..." -ForegroundColor Cyan
docker compose -f docker/docker-compose.yml up --build -d

if ($LASTEXITCODE -ne 0) {
    throw "docker compose failed with exit code $LASTEXITCODE"
}

Write-Host "Opening frontend in browser..." -ForegroundColor Green
Start-Process "http://localhost:5173"

Write-Host "Done. Services:" -ForegroundColor Green
Write-Host "- Frontend: http://localhost:5173"
Write-Host "- Backend:  http://localhost:5000/swagger"
Write-Host "- Speech:   http://localhost:8000/health"
