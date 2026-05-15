[CmdletBinding()]
param(
    [switch]$NoBrowser,
    [ValidateSet("auto", "gpu", "cpu")]
    [string]$SpeechProfile = "auto",
    [ValidateSet("tiny", "small", "medium", "large-v3-turbo", "large-v3")]
    [string]$SpeechModel = "large-v3-turbo",
    [string]$ComposeFile = "docker/docker-compose.yml"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$composeFile = Join-Path $root $ComposeFile
$gpuComposeFile = Join-Path $root "docker/docker-compose.gpu.yml"
$envExampleFile = Join-Path $root "docker/.env.example"
$envFile = Join-Path $root "docker/.env"

if (-not (Test-Path $composeFile)) {
    throw "Docker Compose file was not found: $composeFile"
}

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
                Write-Host "$Name hazir: $Url" -ForegroundColor Green
                return
            }
        } catch {
            Start-Sleep -Seconds 2
        }
    }

    throw "$Name $TimeoutSeconds saniye icinde hazir olmadi. Son kontrol: $Url"
}

function Read-ErrorResponseBody {
    param(
        [Parameter(Mandatory = $true)]
        $ErrorRecord
    )

    try {
        $response = $ErrorRecord.Exception.Response
        if ($null -eq $response) {
            return $null
        }

        $stream = $response.GetResponseStream()
        if ($null -eq $stream) {
            return $null
        }

        $reader = New-Object System.IO.StreamReader($stream)
        return $reader.ReadToEnd()
    } catch {
        return $null
    }
}

function Get-SpeechReadiness {
    param(
        [string]$Url
    )

    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
        $payload = $null
        if (-not [string]::IsNullOrWhiteSpace($response.Content)) {
            $payload = $response.Content | ConvertFrom-Json
        }

        return @{
            Reachable  = $true
            Ready      = $response.StatusCode -ge 200 -and $response.StatusCode -lt 300
            StatusCode = [int]$response.StatusCode
            Payload    = $payload
            Message    = $null
        }
    } catch {
        $body = Read-ErrorResponseBody -ErrorRecord $_
        $payload = $null
        if (-not [string]::IsNullOrWhiteSpace($body)) {
            try { $payload = $body | ConvertFrom-Json } catch { $payload = $null }
        }

        $statusCode = 0
        try { $statusCode = [int]$_.Exception.Response.StatusCode } catch { $statusCode = 0 }

        $message = if ($payload -and $payload.failureReason -eq "startup_failed") {
            "Transkripsiyon servisi baslatma hatasi. SPEECH_COMPUTE_TYPE ve SPEECH_DEVICE kontrolu yapiniz."
        } elseif ($payload -and $payload.status -eq "not_ready") {
            "Transkripsiyon servisi model yukleniyor, lutfen bekleyiniz."
        } elseif ($payload -and $payload.status -eq "at_capacity") {
            "Transkripsiyon servisi kapasitede."
        } elseif ($statusCode -gt 0) {
            "Transkripsiyon servisi HTTP $statusCode dondu."
        } else {
            "Transkripsiyon servisine henuz ulasilamiyor."
        }

        return @{
            Reachable  = ($statusCode -eq 503)
            Ready      = $false
            StatusCode = $statusCode
            Payload    = $payload
            Message    = $message
        }
    }
}

function Test-HttpReachable {
    param([string]$Url)

    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
        return $response.StatusCode -ge 200 -and $response.StatusCode -lt 400
    } catch {
        return $false
    }
}

function Get-SpeechDiagnostics {
    param([string]$Url)

    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
        if ([string]::IsNullOrWhiteSpace($response.Content)) { return $null }
        return $response.Content | ConvertFrom-Json
    } catch {
        return $null
    }
}

function Test-HostNvidiaGpu {
    $cmd = Get-Command "nvidia-smi" -ErrorAction SilentlyContinue
    if (-not $cmd) { return $false }

    try {
        & $cmd.Source "-L" *> $null
        return $LASTEXITCODE -eq 0
    } catch {
        return $false
    }
}

function Test-DockerNvidiaRuntime {
    try {
        $runtimes = & docker info --format "{{json .Runtimes}}" 2>$null
        return $LASTEXITCODE -eq 0 -and ($runtimes -match '"nvidia"')
    } catch {
        return $false
    }
}

function Resolve-SpeechRuntimeProfile {
    param([string]$RequestedProfile)

    $gpuAvailable = (Test-HostNvidiaGpu) -and (Test-DockerNvidiaRuntime)
    switch ($RequestedProfile) {
        "gpu" {
            if (-not $gpuAvailable) {
                throw "SpeechProfile 'gpu' secildi ancak NVIDIA GPU Docker icin mevcut degil."
            }
            return "gpu"
        }
        "cpu" { return "cpu" }
        default {
            if ($gpuAvailable) { return "gpu" }
            return "cpu"
        }
    }
}

# --- Env dosyasi ---
if (-not (Test-Path $envFile)) {
    if (-not (Test-Path $envExampleFile)) {
        throw "docker/.env eksik ve docker/.env.example bulunamadi."
    }
    Write-Host "docker/.env bulunamadi. docker/.env.example'dan olusturuluyor..." -ForegroundColor Yellow
    Copy-Item $envExampleFile $envFile
}

# --- Profil ve port okuma ---
$selectedProfile = Resolve-SpeechRuntimeProfile -RequestedProfile $SpeechProfile
$frontendPort   = Get-EnvValue -Path $envFile -Key "FRONTEND_PORT" -DefaultValue "5173"
$apiPort        = Get-EnvValue -Path $envFile -Key "API_PORT"      -DefaultValue "8080"
$speechPort     = Get-EnvValue -Path $envFile -Key "SPEECH_PORT"   -DefaultValue "8000"

$frontendUrl         = "http://localhost:$frontendPort"
$apiReadyUrl         = "http://localhost:$apiPort/health/ready"
$swaggerUrl          = "http://localhost:$apiPort/swagger"
$speechHealthUrl     = "http://localhost:$speechPort/health"
$speechReadyUrl      = "http://localhost:$speechPort/health/ready"
$speechDiagnosticsUrl = "http://localhost:$speechPort/health/diagnostics"

# --- Speech servis ortam degiskenleri (batch transkripsiyon modu) ---
$env:SPEECH_MODEL       = $SpeechModel
$env:SPEECH_CPU_THREADS = "8"
$env:SPEECH_NUM_WORKERS = "1"

if ($selectedProfile -eq "gpu") {
    $env:SPEECH_DEVICE       = "cuda"
    $env:SPEECH_COMPUTE_TYPE = "int8_float16"
} else {
    $env:SPEECH_DEVICE       = "cpu"
    $env:SPEECH_COMPUTE_TYPE = "int8"
}

# --- Docker Compose argumanlari ---
$composeArgs = @("-f", $composeFile)
if ($selectedProfile -eq "gpu") {
    if (-not (Test-Path $gpuComposeFile)) {
        throw "GPU Docker Compose dosyasi bulunamadi: $gpuComposeFile"
    }
    $composeArgs += @("-f", $gpuComposeFile)
}

Write-Host ""
Write-Host "=== Interview AI Baslatiliyor ===" -ForegroundColor Cyan
Write-Host "Speech profili : $selectedProfile | model: $($env:SPEECH_MODEL) | device: $($env:SPEECH_DEVICE) | compute: $($env:SPEECH_COMPUTE_TYPE)" -ForegroundColor DarkGray
Write-Host ""

& docker compose @composeArgs up --build -d

if ($LASTEXITCODE -ne 0) {
    throw "docker compose hata kodu $LASTEXITCODE ile basarisiz oldu."
}

Write-Host ""
Write-Host "Servisler bekleniyor..." -ForegroundColor Cyan
Wait-ForHttpOk -Name "Backend API"  -Url $apiReadyUrl
Wait-ForHttpOk -Name "Frontend"     -Url $frontendUrl
Wait-ForHttpOk -Name "Speech servisi" -Url $speechHealthUrl -TimeoutSeconds 60

$speechReadiness = Get-SpeechReadiness -Url $speechReadyUrl
if ($speechReadiness.Ready) {
    Write-Host "Speech servisi hazir (batch transkripsiyon aktif)." -ForegroundColor Green
} elseif ($speechReadiness.Reachable) {
    $payload = $speechReadiness.Payload
    if ($payload -and $payload.failureReason -eq "startup_failed") {
        Write-Host "Speech servisi baslatma hatasi." -ForegroundColor Red
        if ($payload.failureDetail) {
            Write-Host "Sebep: $($payload.failureDetail)" -ForegroundColor Red
        }
        Write-Host "Cozum: SPEECH_COMPUTE_TYPE ve SPEECH_DEVICE degerlerini kontrol edin." -ForegroundColor Yellow
    } else {
        Write-Host "Ses transkripsiyon servisi model yukleniyor, ilk calistirmada bu surec uzun surebilir." -ForegroundColor Yellow
        if ($payload) {
            Write-Host "Durum: $($payload.status) | modelLoaded=$($payload.modelLoaded)" -ForegroundColor Yellow
        }
    }
} else {
    Write-Host "Speech servisi henuz hazir degil, mülakat yapilabilir ancak transkript raporda gecikmeli gelebilir." -ForegroundColor Yellow
    if ($speechReadiness.Message) {
        Write-Host $speechReadiness.Message -ForegroundColor Yellow
    }
}

$speechDiagnostics = Get-SpeechDiagnostics -Url $speechDiagnosticsUrl

Write-Host ""
Write-Host "=== Servisler Hazir ===" -ForegroundColor Green
Write-Host "  Frontend  : $frontendUrl"
Write-Host "  Backend   : $swaggerUrl"
Write-Host "  Speech    : $speechHealthUrl"
if ($speechDiagnostics) {
    Write-Host "  Model     : $($speechDiagnostics.model)"    -ForegroundColor Green
    Write-Host "  Device    : $($speechDiagnostics.device)"   -ForegroundColor Green
    Write-Host "  Compute   : $($speechDiagnostics.compute_type)" -ForegroundColor Green
}
Write-Host ""

if (-not $NoBrowser) {
    Write-Host "Tarayici aciliyor: $frontendUrl" -ForegroundColor Green
    Start-Process $frontendUrl
}
