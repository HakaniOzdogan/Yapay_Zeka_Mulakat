using InterviewCoach.Api.Services;
using InterviewCoach.Application;
using InterviewCoach.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi.Models;

var builder = WebApplication.CreateBuilder(args);

// Add services
builder.Services.AddControllers();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Interview Coach API",
        Version = "v1",
        Description = "AI Interview Coach - real-time coaching & offline analysis"
    });
});

// Configure CORS
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        policy.AllowAnyOrigin()
            .AllowAnyMethod()
            .AllowAnyHeader();
    });
});

// Register ApplicationOptions
builder.Services.Configure<ApplicationOptions>(
    builder.Configuration.GetSection("Scoring"));

// Register Services
builder.Services.AddScoped<IScoringService, ScoringService>();
builder.Services.AddHttpClient<ILlmAnalysisService, OllamaLlmAnalysisService>();

// Register DbContext
var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

var app = builder.Build();

// Migrate database on startup
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();

    // Safety net for older/inconsistent local databases:
    // ensure Sessions.StatsJson exists even if a migration was skipped.
    await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_name = 'Sessions' AND column_name = 'StatsJson'
    ) THEN
        ALTER TABLE ""Sessions"" ADD COLUMN ""StatsJson"" text NOT NULL DEFAULT '{{}}';
    END IF;
END $$;");
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseAuthorization();
app.MapControllers();

app.Run();
