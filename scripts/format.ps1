$ErrorActionPreference = 'Stop'

$dotnetFormatAvailable = $false
try {
  dotnet format -h | Out-Null
  if ($LASTEXITCODE -eq 0) {
    $dotnetFormatAvailable = $true
  }
}
catch {
  $dotnetFormatAvailable = $false
}

if ($dotnetFormatAvailable) {
  dotnet format src/backend/InterviewCoach.sln
}
else {
  Write-Host 'dotnet format not available, skipping backend formatting'
}

Push-Location src/frontend
try {
  npm ci
  npm run lint --if-present
}
finally {
  Pop-Location
}