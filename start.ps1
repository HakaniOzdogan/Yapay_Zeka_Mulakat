[CmdletBinding()]
param(
    [switch]$NoBrowser,
    [ValidateSet("auto", "gpu", "cpu")]
    [string]$SpeechProfile = "auto",
    [ValidateSet("ultra", "balanced", "quality")]
    [string]$LatencyProfile = "ultra",
    [ValidateSet("tiny", "small", "medium", "vibevoice")]
    [string]$SpeechModel = "medium",
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
                Write-Host "$Name is ready: $Url" -ForegroundColor Green
                return
            }
        } catch {
            Start-Sleep -Seconds 2
        }
    }

    throw "$Name did not become ready within $TimeoutSeconds seconds. Last checked: $Url"
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
            Reachable = $true
            Ready = $response.StatusCode -ge 200 -and $response.StatusCode -lt 300
            StatusCode = [int]$response.StatusCode
            Payload = $payload
            Message = $null
        }
    } catch {
        $body = Read-ErrorResponseBody -ErrorRecord $_
        $payload = $null
        if (-not [string]::IsNullOrWhiteSpace($body)) {
            try {
                $payload = $body | ConvertFrom-Json
            } catch {
                $payload = $null
            }
        }

        $statusCode = 0
        try {
            $statusCode = [int]$_.Exception.Response.StatusCode
        } catch {
            $statusCode = 0
        }

        $message = if ($payload -and $payload.failureReason -eq "startup_failed") {
            "Speech service is reachable, but model startup failed. Check SPEECH_COMPUTE_TYPE and SPEECH_DEVICE."
        } elseif ($payload -and $payload.status -eq "not_ready") {
            "Speech service is reachable, but the model is still warming up."
        } elseif ($payload -and $payload.status -eq "at_capacity") {
            "Speech service is reachable, but currently at capacity."
        } elseif ($statusCode -gt 0) {
            "Speech readiness check returned HTTP $statusCode."
        } else {
            "Speech readiness check could not reach the service yet."
        }

        return @{
            Reachable = ($statusCode -eq 503)
            Ready = $false
            StatusCode = $statusCode
            Payload = $payload
            Message = $message
        }
    }
}

function Test-HttpReachable {
    param(
        [string]$Url
    )

    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
        return $response.StatusCode -ge 200 -and $response.StatusCode -lt 400
    } catch {
        return $false
    }
}

function Get-SpeechDiagnostics {
    param(
        [string]$Url
    )

    try {
        $response = Invoke-WebRequest -Uri $Url -UseBasicParsing -TimeoutSec 5
        if ([string]::IsNullOrWhiteSpace($response.Content)) {
            return $null
        }
        return $response.Content | ConvertFrom-Json
    } catch {
        return $null
    }
}

function Test-HostNvidiaGpu {
    $cmd = Get-Command "nvidia-smi" -ErrorAction SilentlyContinue
    if (-not $cmd) {
        return $false
    }

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
    param(
        [string]$RequestedProfile
    )

    $gpuAvailable = (Test-HostNvidiaGpu) -and (Test-DockerNvidiaRuntime)
    switch ($RequestedProfile) {
        "gpu" {
            if (-not $gpuAvailable) {
                throw "SpeechProfile 'gpu' was requested, but NVIDIA GPU access is not available for Docker."
            }
            return "gpu"
        }
        "cpu" {
            return "cpu"
        }
        default {
            if ($gpuAvailable) {
                return "gpu"
            }
            return "cpu"
        }
    }
}

if (-not (Test-Path $envFile)) {
    if (-not (Test-Path $envExampleFile)) {
        throw "docker/.env is missing and docker/.env.example was not found."
    }

    Write-Host "docker/.env not found. Creating it from docker/.env.example..." -ForegroundColor Yellow
    Copy-Item $envExampleFile $envFile
}

$selectedProfile = Resolve-SpeechRuntimeProfile -RequestedProfile $SpeechProfile
$frontendPort = Get-EnvValue -Path $envFile -Key "FRONTEND_PORT" -DefaultValue "5173"
$apiPort = Get-EnvValue -Path $envFile -Key "API_PORT" -DefaultValue "8080"
$speechPort = Get-EnvValue -Path $envFile -Key "SPEECH_PORT" -DefaultValue "8000"

$frontendUrl = "http://localhost:$frontendPort"
$apiReadyUrl = "http://localhost:$apiPort/health/ready"
$swaggerUrl = "http://localhost:$apiPort/swagger"
$speechHealthUrl = "http://localhost:$speechPort/health"
$speechReadyUrl = "http://localhost:$speechPort/health/ready"
$speechDiagnosticsUrl = "http://localhost:$speechPort/health/diagnostics"
$selectedLatencyProfile = if (-not $PSBoundParameters.ContainsKey("LatencyProfile")) {
    switch ($SpeechModel) {
        "medium" { "balanced" }
        "vibevoice" { "quality" }
        default { $LatencyProfile }
    }
} else {
    $LatencyProfile
}

$env:SPEECH_PROFILE = $selectedProfile
$env:SPEECH_MODEL = if ($SpeechModel -eq "vibevoice") { "microsoft/VibeVoice-ASR-HF" } else { $SpeechModel }
$env:SPEECH_LATENCY_PROFILE = $selectedLatencyProfile
$env:SPEECH_CPU_THREADS = "8"
$env:SPEECH_NUM_WORKERS = "1"
switch ($selectedLatencyProfile) {
    "quality" {
        $env:SPEECH_BEAM_SIZE = "2"
        $env:SPEECH_BEST_OF = "2"
        $env:SPEECH_NO_SPEECH_THRESHOLD = "0.65"
    }
    "balanced" {
        $env:SPEECH_BEAM_SIZE = "1"
        $env:SPEECH_BEST_OF = "1"
        $env:SPEECH_NO_SPEECH_THRESHOLD = "0.6"
    }
    default {
        $env:SPEECH_BEAM_SIZE = "1"
        $env:SPEECH_BEST_OF = "1"
        $env:SPEECH_NO_SPEECH_THRESHOLD = "0.6"
    }
}
switch ($SpeechModel) {
    "vibevoice" {
        $env:STREAM_DECODE_INTERVAL_MS = "900"
        $env:VAD_MIN_SPEECH_MS = "600"
        $env:VAD_SILENCE_MS = "900"
        $env:STREAM_COMMIT_AGREEMENT_PASSES = "1"
        $env:STRICT_QUALITY_MODE = "false"
        $env:SPEECH_VIBEVOICE_TOKENIZER_CHUNK_SIZE = "960000"
    }
    "medium" {
        $env:STREAM_DECODE_INTERVAL_MS = "350"
        $env:VAD_MIN_SPEECH_MS = "300"
        $env:VAD_SILENCE_MS = "900"
        $env:STREAM_COMMIT_AGREEMENT_PASSES = "1"
        $env:STRICT_QUALITY_MODE = "true"
    }
    default {
        $env:STREAM_DECODE_INTERVAL_MS = "200"
        $env:VAD_MIN_SPEECH_MS = "250"
        $env:VAD_SILENCE_MS = "500"
        $env:STREAM_COMMIT_AGREEMENT_PASSES = "1"
        $env:STRICT_QUALITY_MODE = "false"
    }
}
if ($selectedProfile -eq "gpu") {
    $env:SPEECH_DEVICE = "cuda"
    $env:SPEECH_COMPUTE_TYPE = "int8_float16"
} else {
    $env:SPEECH_DEVICE = "cpu"
    $env:SPEECH_COMPUTE_TYPE = "int8"
}

$composeArgs = @("-f", $composeFile)
if ($selectedProfile -eq "gpu") {
    if (-not (Test-Path $gpuComposeFile)) {
        throw "GPU Docker Compose override was not found: $gpuComposeFile"
    }
    $composeArgs += @("-f", $gpuComposeFile)
}

Write-Host "Starting Docker Compose services..." -ForegroundColor Cyan
Write-Host "Speech profile: $selectedProfile" -ForegroundColor DarkGray
Write-Host "Speech runtime: model=$($env:SPEECH_MODEL), latency=$($env:SPEECH_LATENCY_PROFILE), beam=$($env:SPEECH_BEAM_SIZE), device=$($env:SPEECH_DEVICE), compute=$($env:SPEECH_COMPUTE_TYPE), threads=$($env:SPEECH_CPU_THREADS)" -ForegroundColor DarkGray
Write-Host "Speech streaming tune: decode_interval=$($env:STREAM_DECODE_INTERVAL_MS)ms, vad_min_speech=$($env:VAD_MIN_SPEECH_MS)ms, vad_silence=$($env:VAD_SILENCE_MS)ms, agreement_passes=$($env:STREAM_COMMIT_AGREEMENT_PASSES), strict_quality=$($env:STRICT_QUALITY_MODE)" -ForegroundColor DarkGray
& docker compose @composeArgs up --build -d

if ($LASTEXITCODE -ne 0) {
    throw "docker compose failed with exit code $LASTEXITCODE"
}

Write-Host "Waiting for core services..." -ForegroundColor Cyan
Wait-ForHttpOk -Name "Backend API" -Url $apiReadyUrl
Wait-ForHttpOk -Name "Frontend" -Url $frontendUrl
Wait-ForHttpOk -Name "Speech service" -Url $speechHealthUrl -TimeoutSeconds 60

$speechReadiness = Get-SpeechReadiness -Url $speechReadyUrl
if ($speechReadiness.Ready) {
    Write-Host "Speech service is ready: $speechReadyUrl" -ForegroundColor Green
} elseif ($speechReadiness.Reachable) {
    $payload = $speechReadiness.Payload
    if ($payload -and $payload.failureReason -eq "startup_failed") {
        Write-Host "Speech service startup failed: $speechReadyUrl" -ForegroundColor Red
        if ($payload.failureDetail) {
            Write-Host "Reason: $($payload.failureDetail)" -ForegroundColor Red
        }
        Write-Host "Action: verify SPEECH_COMPUTE_TYPE and SPEECH_DEVICE for this machine." -ForegroundColor Yellow
    } else {
        Write-Host "Speech service is still warming up: $speechReadyUrl" -ForegroundColor Yellow
        if ($payload) {
            Write-Host "Speech status: $($payload.status) | modelLoaded=$($payload.modelLoaded) | activeSessions=$($payload.activeSessions)" -ForegroundColor Yellow
        }
        Write-Host "Transcript startup may take longer on the first run while the model loads." -ForegroundColor Yellow
    }
} else {
    if (Test-HttpReachable -Url $speechHealthUrl) {
        Write-Host "Speech service is reachable and still loading: $speechReadyUrl" -ForegroundColor Yellow
        Write-Host "Speech HTTP endpoint is up; model warmup is still in progress." -ForegroundColor Yellow
    } else {
        Write-Host "Speech service readiness is unavailable right now: $speechReadyUrl" -ForegroundColor Yellow
        if ($speechReadiness.Message) {
            Write-Host $speechReadiness.Message -ForegroundColor Yellow
        }
        Write-Host "The stack is up, but live transcript may not be available until speech-service finishes starting." -ForegroundColor Yellow
    }
}

$speechDiagnostics = Get-SpeechDiagnostics -Url $speechDiagnosticsUrl

Write-Host ""
Write-Host "Services are up:" -ForegroundColor Green
Write-Host "- Frontend: $frontendUrl"
Write-Host "- Backend:  $swaggerUrl"
Write-Host "- Speech:   $speechReadyUrl"
if ($speechDiagnostics) {
    Write-Host "- Speech profile: $selectedProfile" -ForegroundColor Green
    Write-Host "- Speech model: $($speechDiagnostics.model)" -ForegroundColor Green
    Write-Host "- Latency profile: $($speechDiagnostics.latency_profile)" -ForegroundColor Green
    Write-Host "- Beam / best_of: $($speechDiagnostics.beam_size) / $($speechDiagnostics.best_of)" -ForegroundColor Green
    Write-Host "- Stream decode / VAD silence: $($speechDiagnostics.stream_decode_interval_ms)ms / $($speechDiagnostics.vad_silence_ms)ms" -ForegroundColor Green
    Write-Host "- VAD min speech / agreement: $($speechDiagnostics.vad_min_speech_ms)ms / $($speechDiagnostics.stream_commit_agreement_passes)" -ForegroundColor Green
    Write-Host "- Speech device: $($speechDiagnostics.device)" -ForegroundColor Green
    Write-Host "- Speech compute: $($speechDiagnostics.compute_type)" -ForegroundColor Green
    Write-Host "- Audio contract: $($speechDiagnostics.audio_input_contract)" -ForegroundColor Green
}

if (-not $NoBrowser) {
    Write-Host "Opening frontend in browser..." -ForegroundColor Green
    Start-Process $frontendUrl
}
