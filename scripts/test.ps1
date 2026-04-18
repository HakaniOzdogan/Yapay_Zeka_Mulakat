$ErrorActionPreference = 'Stop'

dotnet test src/backend/InterviewCoach.sln
Push-Location src/frontend
try {
  npm ci
  npm run build
}
finally {
  Pop-Location
}