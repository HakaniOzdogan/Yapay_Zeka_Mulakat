FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

COPY InterviewCoach.sln ./
COPY InterviewCoach.Api/InterviewCoach.Api.csproj InterviewCoach.Api/
COPY InterviewCoach.Application/InterviewCoach.Application.csproj InterviewCoach.Application/
COPY InterviewCoach.Domain/InterviewCoach.Domain.csproj InterviewCoach.Domain/
COPY InterviewCoach.Infrastructure/InterviewCoach.Infrastructure.csproj InterviewCoach.Infrastructure/

RUN dotnet restore InterviewCoach.Api/InterviewCoach.Api.csproj

COPY . .
RUN dotnet publish InterviewCoach.Api/InterviewCoach.Api.csproj -c Release -o /app/publish /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

COPY --from=build /app/publish .

EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "InterviewCoach.Api.dll"]