[CmdletBinding()]
param(
    [switch]$NoBrowser
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$composeFile = Join-Path $root "docker/docker-compose.yml"
$envExampleFile = Join-Path $root "docker/.env.example"
$envFile = Join-Path $root "docker/.env"

function Get-EnvValue {
    param(
        [string]$Path,
        [string]$Key,
        [string]$DefaultValue
    )

    if (-not (Test-Path $Path)) {
        return $DefaultValue
    }

    $line = Get-Content $Path | Where-Object { $_ -match "^$Key=" } | Select-Object -First 1
    if (-not $line) {
        return $DefaultValue
    }

    $value = $line.Substring($Key.Length + 1).Trim()
    if ([string]::IsNullOrWhiteSpace($value)) {
        return $DefaultValue
    }

    return $value
}

function Wait-ForHttpOk {
    param(
        [string]$Name,
        [string]$Url,
        [int]$TimeoutSeconds = 120
    )

    $deadline = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $deadline) {
        try {
            $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
            if ($response.StatusCode -ge 200 -and $response.StatusCode -lt 400) {
                Write-Host "$Name is ready: $Url" -ForegroundColor Green
                return
            }
        } catch {
            Start-Sleep -Seconds 2
        }
    }

    throw "$Name did not become ready within $TimeoutSeconds seconds. Last checked: $Url"
}

if (-not (Test-Path $envFile)) {
    if (-not (Test-Path $envExampleFile)) {
        throw "docker/.env is missing and docker/.env.example was not found."
    }

    Write-Host "docker/.env not found. Creating it from docker/.env.example..." -ForegroundColor Yellow
    Copy-Item $envExampleFile $envFile
}

$frontendPort = Get-EnvValue -Path $envFile -Key "FRONTEND_PORT" -DefaultValue "5173"
$apiPort = Get-EnvValue -Path $envFile -Key "API_PORT" -DefaultValue "8080"
$speechPort = Get-EnvValue -Path $envFile -Key "SPEECH_PORT" -DefaultValue "8000"

$frontendUrl = "http://localhost:$frontendPort"
$apiUrl = "http://localhost:$apiPort"
$swaggerUrl = "$apiUrl/swagger"
$speechHealthUrl = "http://localhost:$speechPort/health"
$apiHealthUrl = "$apiUrl/health"

Write-Host "Starting Docker services..." -ForegroundColor Cyan
docker compose -f $composeFile up --build -d

if ($LASTEXITCODE -ne 0) {
    throw "docker compose failed with exit code $LASTEXITCODE"
}

Write-Host "Waiting for services to become ready..." -ForegroundColor Cyan
Wait-ForHttpOk -Name "Backend API" -Url $apiHealthUrl
Wait-ForHttpOk -Name "Frontend" -Url $frontendUrl
Wait-ForHttpOk -Name "Speech service" -Url $speechHealthUrl

Write-Host ""
Write-Host "Services are up:" -ForegroundColor Green
Write-Host "- Frontend: $frontendUrl"
Write-Host "- Backend:  $swaggerUrl"
Write-Host "- Speech:   $speechHealthUrl"

if (-not $NoBrowser) {
    Write-Host "Opening frontend in browser..." -ForegroundColor Green
    Start-Process $frontendUrl
}
