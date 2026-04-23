[CmdletBinding()]
param(
    [switch]$RemoveVolumes,
    [ValidateSet("auto", "gpu", "cpu")]
    [string]$SpeechProfile = "auto",
    [string]$ComposeFile = "docker/docker-compose.yml"
)

$ErrorActionPreference = "Stop"

$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

$composeFile = Join-Path $root $ComposeFile
$gpuComposeFile = Join-Path $root "docker/docker-compose.gpu.yml"

if (-not (Test-Path $composeFile)) {
    throw "Docker Compose file was not found: $composeFile"
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

$selectedProfile = Resolve-SpeechRuntimeProfile -RequestedProfile $SpeechProfile
$composeArgs = @("-f", $composeFile)
if ($selectedProfile -eq "gpu" -and (Test-Path $gpuComposeFile)) {
    $composeArgs += @("-f", $gpuComposeFile)
}
$composeArgs += @("down", "--remove-orphans")
if ($RemoveVolumes) {
    $composeArgs += "-v"
}

Write-Host "Stopping Docker services..." -ForegroundColor Yellow
Write-Host "Speech profile: $selectedProfile" -ForegroundColor DarkGray
& docker compose @composeArgs

if ($LASTEXITCODE -ne 0) {
    throw "docker compose failed with exit code $LASTEXITCODE"
}

Write-Host "Done. Services stopped." -ForegroundColor Green
