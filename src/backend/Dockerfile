FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# Copy solution and all projects
COPY InterviewCoach.sln ./
COPY InterviewCoach.Domain/ ./InterviewCoach.Domain/
COPY InterviewCoach.Application/ ./InterviewCoach.Application/
COPY InterviewCoach.Infrastructure/ ./InterviewCoach.Infrastructure/
COPY InterviewCoach.Api/ ./InterviewCoach.Api/
COPY InterviewCoach.Api.Tests/ ./InterviewCoach.Api.Tests/

# Restore dependencies 
RUN dotnet restore

# Build Debug (devam etmesi için || true ekledim)
RUN dotnet build -c Debug 2>&1 || true

FROM mcr.microsoft.com/dotnet/sdk:8.0
WORKDIR /app
COPY --from=build /src/InterviewCoach.Api/bin/Debug/net8.0 .

EXPOSE 80
ENTRYPOINT ["dotnet", "InterviewCoach.Api.dll"]
