[CmdletBinding()]
param(
    [switch]$RemoveVolumes,
    [ValidateSet("auto", "gpu", "cpu")]
    [string]$SpeechProfile = "auto"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$rootScript = Join-Path $repoRoot "stop.ps1"

if (-not (Test-Path $rootScript)) {
    throw "Root stop script not found: $rootScript"
}

& $rootScript -RemoveVolumes:$RemoveVolumes -SpeechProfile $SpeechProfile
