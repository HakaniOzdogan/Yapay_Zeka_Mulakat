[CmdletBinding()]
param(
    [switch]$NoBrowser,
    [ValidateSet("auto", "gpu", "cpu")]
    [string]$SpeechProfile = "auto"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$rootScript = Join-Path $repoRoot "start.ps1"

if (-not (Test-Path $rootScript)) {
    throw "Root start script not found: $rootScript"
}

& $rootScript -NoBrowser:$NoBrowser -SpeechProfile $SpeechProfile
