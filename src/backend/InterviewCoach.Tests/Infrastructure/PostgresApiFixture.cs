using InterviewCoach.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Testcontainers.PostgreSql;

namespace InterviewCoach.Tests.Infrastructure;

public sealed class PostgresApiFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _container;

    public bool DockerAvailable { get; private set; }
    public string? DockerUnavailableReason { get; private set; }

    public InterviewCoachApiFactory? Factory { get; private set; }
    public HttpClient? Client { get; private set; }

    public async Task InitializeAsync()
    {
        try
        {
            _container = new PostgreSqlBuilder()
                .WithImage("postgres:16-alpine")
                .WithDatabase("interviewcoach_tests")
                .WithUsername("postgres")
                .WithPassword("postgres")
                .Build();

            await _container.StartAsync();

            DockerAvailable = true;
            Factory = new InterviewCoachApiFactory(_container.GetConnectionString());
            Client = Factory.CreateClient();

            using var scope = Factory.Services.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
            await db.Database.MigrateAsync();
        }
        catch (Exception ex)
        {
            DockerAvailable = false;
            DockerUnavailableReason = $"Docker/Testcontainers unavailable: {ex.Message}";
        }
    }

    public async Task DisposeAsync()
    {
        Client?.Dispose();

        if (Factory is not null)
        {
            await Factory.DisposeAsync();
        }

        if (_container is not null)
        {
            await _container.DisposeAsync();
        }
    }

    public HttpClient GetClient()
    {
        if (Client is null)
        {
            throw new InvalidOperationException("Test client is not initialized.");
        }

        return Client;
    }
}
