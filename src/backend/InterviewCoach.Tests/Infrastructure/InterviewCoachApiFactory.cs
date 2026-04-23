using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace InterviewCoach.Tests.Infrastructure;

public sealed class InterviewCoachApiFactory : WebApplicationFactory<Program>
{
    private readonly string _connectionString;

    public InterviewCoachApiFactory(string connectionString)
    {
        _connectionString = connectionString;
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");

        builder.ConfigureAppConfiguration((_, configBuilder) =>
        {
            configBuilder.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["ConnectionStrings:Default"] = _connectionString,
                ["Telemetry:Enabled"] = "false",
                ["Llm:Provider"] = "OpenAI",
                ["Llm:ApiKey"] = "test-openai-key",
                ["Llm:BaseUrl"] = "https://api.openai.com/v1",
                ["Llm:PrimaryModel"] = "gpt-5.4",
                ["Llm:LiveAnalysisModel"] = "gpt-5.4-mini",
                ["Auth:SeedAdminEmail"] = "admin.regression@example.com",
                ["Auth:SeedAdminPassword"] = "AdminPass123!"
            });
        });

        builder.ConfigureServices(services =>
        {
            var descriptor = services.SingleOrDefault(d => d.ServiceType == typeof(DbContextOptions<ApplicationDbContext>));
            if (descriptor is not null)
            {
                services.Remove(descriptor);
            }

            services.AddDbContext<ApplicationDbContext>(options =>
            {
                options.UseNpgsql(_connectionString);
            });
        });
    }
}
