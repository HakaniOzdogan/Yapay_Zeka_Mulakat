using System.Diagnostics;
using System.Reflection;
using System.Text.Json;
using System.Text;
using System.Threading.RateLimiting;
using InterviewCoach.Api.Services;
using InterviewCoach.Application;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.OpenApi.Models;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);

const long ingestionBodyLimitBytes = 5 * 1024 * 1024;

builder.Services.AddProblemDetails(options =>
{
    options.CustomizeProblemDetails = context =>
    {
        context.ProblemDetails.Extensions["traceId"] = context.HttpContext.TraceIdentifier;
    };
});

builder.Services
    .AddControllers()
    .AddJsonOptions(options =>
    {
        options.JsonSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
        options.JsonSerializerOptions.DictionaryKeyPolicy = JsonNamingPolicy.CamelCase;
    });

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new OpenApiInfo
    {
        Title = "Interview Coach API",
        Version = "v1",
        Description = "AI Interview Coach - real-time coaching & offline analysis"
    });

    var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
    var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
    if (File.Exists(xmlPath))
    {
        c.IncludeXmlComments(xmlPath, includeControllerXmlComments: true);
    }

    c.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
    {
        Name = "Authorization",
        Type = SecuritySchemeType.Http,
        Scheme = "bearer",
        BearerFormat = "JWT",
        In = ParameterLocation.Header,
        Description = "Enter a valid JWT access token."
    });

    c.AddSecurityRequirement(new OpenApiSecurityRequirement
    {
        {
            new OpenApiSecurityScheme
            {
                Reference = new OpenApiReference
                {
                    Type = ReferenceType.SecurityScheme,
                    Id = "Bearer"
                }
            },
            Array.Empty<string>()
        }
    });
});

var allowedOrigin = builder.Configuration["Cors:AllowedOrigin"];
builder.Services.AddCors(options =>
{
    options.AddPolicy("Frontend", policy =>
    {
        if (!string.IsNullOrWhiteSpace(allowedOrigin))
        {
            policy.WithOrigins(allowedOrigin)
                .AllowAnyMethod()
                .AllowAnyHeader();
        }
        else
        {
            policy.AllowAnyOrigin()
                .AllowAnyMethod()
                .AllowAnyHeader();
        }
    });
});

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        if (context.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            context.HttpContext.Response.Headers.RetryAfter = Math.Ceiling(retryAfter.TotalSeconds).ToString();
        }

        var problem = new ProblemDetails
        {
            Title = "Too Many Requests",
            Status = StatusCodes.Status429TooManyRequests,
            Detail = "Request rate limit exceeded.",
            Type = "https://datatracker.ietf.org/doc/html/rfc7807"
        };
        problem.Extensions["traceId"] = context.HttpContext.TraceIdentifier;

        await context.HttpContext.Response.WriteAsJsonAsync(problem, cancellationToken);
    };

    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
    {
        if (httpContext.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase))
        {
            return RateLimitPartition.GetNoLimiter("swagger");
        }

        var ip = GetClientIp(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"global:{ip}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 60,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("events-batch", httpContext =>
    {
        var ip = GetClientIp(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"events:{ip}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 30,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("transcript-batch", httpContext =>
    {
        var ip = GetClientIp(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"transcript:{ip}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 20,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });

    options.AddPolicy("llm-coach", httpContext =>
    {
        var ip = GetClientIp(httpContext);
        return RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: $"llm:{ip}",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0,
                AutoReplenishment = true
            });
    });
});

builder.Services.AddOptions<ScoringProfilesOptions>()
    .Bind(builder.Configuration.GetSection("ScoringProfiles"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<ScoringProfilesOptions>, ScoringProfilesOptionsValidator>();
builder.Services.Configure<LlmOptions>(
    builder.Configuration.GetSection("Llm"));
builder.Services.AddOptions<LlmOptimizationOptions>()
    .Bind(builder.Configuration.GetSection("LlmOptimization"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<LlmOptimizationOptions>, LlmOptimizationOptionsValidator>();
builder.Services.AddOptions<BatchCoachingOptions>()
    .Bind(builder.Configuration.GetSection("BatchCoaching"))
    .ValidateOnStart();
builder.Services.AddSingleton<IValidateOptions<BatchCoachingOptions>, BatchCoachingOptionsValidator>();
builder.Services.Configure<LlmGuardrailsOptions>(
    builder.Configuration.GetSection("LlmGuardrails"));
builder.Services.Configure<TelemetryOptions>(
    builder.Configuration.GetSection("Telemetry"));
builder.Services.Configure<RetentionOptions>(
    builder.Configuration.GetSection("Retention"));
builder.Services.Configure<PrivacyOptions>(
    builder.Configuration.GetSection("Privacy"));
builder.Services.Configure<AuthOptions>(
    builder.Configuration.GetSection("Auth"));

var authOptions = builder.Configuration.GetSection("Auth").Get<AuthOptions>() ?? new AuthOptions();
if (string.IsNullOrWhiteSpace(authOptions.JwtKey))
{
    authOptions.JwtKey = "dev-only-change-this-key-to-a-long-random-secret";
}

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateIssuerSigningKey = true,
            ValidateLifetime = true,
            ValidIssuer = authOptions.JwtIssuer,
            ValidAudience = authOptions.JwtAudience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(authOptions.JwtKey)),
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("AdminOnly", policy => policy.RequireRole(UserRoles.Admin));
});

builder.Services.AddSingleton<ApiTelemetry>();
builder.Services.AddSingleton<IRetentionRunState, RetentionRunState>();
builder.Services.AddSingleton<ITranscriptRedactionService, TranscriptRedactionService>();
builder.Services.AddScoped<IRetentionCleanupService, RetentionCleanupService>();
builder.Services.AddHostedService<RetentionBackgroundService>();

var telemetryConfig = builder.Configuration.GetSection("Telemetry").Get<TelemetryOptions>() ?? new TelemetryOptions();
if (string.IsNullOrWhiteSpace(telemetryConfig.ServiceVersion))
{
    telemetryConfig.ServiceVersion = typeof(Program).Assembly.GetName().Version?.ToString() ?? "unknown";
}

if (telemetryConfig.Enabled)
{
    var telemetryBuilder = builder.Services
        .AddOpenTelemetry()
        .ConfigureResource(resource =>
            resource.AddService(
                serviceName: telemetryConfig.ServiceName,
                serviceVersion: telemetryConfig.ServiceVersion));

    telemetryBuilder
        .WithTracing(tracing =>
        {
            tracing
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddSource(ApiTelemetry.ActivitySourceName);

            if (!string.IsNullOrWhiteSpace(telemetryConfig.OtlpEndpoint))
            {
                tracing.AddOtlpExporter(options => options.Endpoint = new Uri(telemetryConfig.OtlpEndpoint));
            }
        })
        .WithMetrics(metrics =>
        {
            metrics
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddMeter(ApiTelemetry.MeterName);

            if (!string.IsNullOrWhiteSpace(telemetryConfig.OtlpEndpoint))
            {
                metrics.AddOtlpExporter(options => options.Endpoint = new Uri(telemetryConfig.OtlpEndpoint));
            }
        });
}

builder.Services.AddScoped<IScoringService, ScoringService>();
builder.Services.AddScoped<IEvidenceSummaryService, EvidenceSummaryService>();
builder.Services.AddScoped<ILlmCoachingService, LlmCoachingService>();
builder.Services.AddScoped<ILlmCoachingGuardrailsService, LlmCoachingGuardrailsService>();
builder.Services.AddScoped<ILlmOptimizationService, LlmOptimizationService>();
builder.Services.AddScoped<ILlmCoachingOrchestrator, LlmCoachingOrchestrator>();
builder.Services.AddScoped<IBatchCoachingJobService, BatchCoachingJobService>();
builder.Services.AddHostedService<BatchCoachingWorker>();
builder.Services.AddScoped<IJwtTokenService, JwtTokenService>();
builder.Services.AddScoped<IPasswordHasher<User>, PasswordHasher<User>>();
builder.Services.AddHttpClient<IOllamaClient, OllamaClient>((sp, client) =>
{
    var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
    client.BaseAddress = new Uri(llm.BaseUrl.TrimEnd('/'));
    client.Timeout = TimeSpan.FromSeconds(llm.TimeoutSeconds > 0 ? llm.TimeoutSeconds : 60);
});
builder.Services.AddHttpClient<ILlmAnalysisService, OllamaLlmAnalysisService>((sp, client) =>
{
    var llm = sp.GetRequiredService<IOptions<LlmOptions>>().Value;
    client.BaseAddress = new Uri(llm.BaseUrl.TrimEnd('/'));
    client.Timeout = TimeSpan.FromSeconds(llm.TimeoutSeconds > 0 ? llm.TimeoutSeconds : 60);
});

var connectionString = builder.Configuration.GetConnectionString("Default");
builder.Services.AddDbContext<ApplicationDbContext>(options =>
{
    options.UseNpgsql(connectionString);
});

var app = builder.Build();

if (string.IsNullOrWhiteSpace(allowedOrigin))
{
    app.Logger.LogWarning("Cors:AllowedOrigin is not configured. Using permissive CORS policy.");
}

if (authOptions.JwtKey == "dev-only-change-this-key-to-a-long-random-secret")
{
    app.Logger.LogWarning("Auth:JwtKey is using development default value. Configure a secure key for production.");
}

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
    await db.Database.MigrateAsync();

    await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_name = 'Users'
    ) THEN
        CREATE TABLE ""Users"" (
            ""Id"" uuid NOT NULL PRIMARY KEY,
            ""Email"" character varying(320) NOT NULL,
            ""EmailNormalized"" character varying(320) NOT NULL,
            ""PasswordHash"" text NOT NULL,
            ""DisplayName"" text NULL,
            ""CreatedAtUtc"" timestamp with time zone NOT NULL,
            ""IsActive"" boolean NOT NULL,
            ""Role"" character varying(16) NOT NULL DEFAULT 'User'
        );
    END IF;
END $$;");

    await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_name = 'BatchCoachingJobs'
    ) THEN
        CREATE TABLE ""BatchCoachingJobs"" (
            ""Id"" uuid NOT NULL PRIMARY KEY,
            ""CreatedAtUtc"" timestamp with time zone NOT NULL,
            ""StartedAtUtc"" timestamp with time zone NULL,
            ""CompletedAtUtc"" timestamp with time zone NULL,
            ""Status"" character varying(16) NOT NULL,
            ""CreatedByUserId"" uuid NULL,
            ""FiltersJson"" jsonb NOT NULL,
            ""OptionsJson"" jsonb NOT NULL,
            ""TotalSessions"" integer NOT NULL,
            ""ProcessedSessions"" integer NOT NULL,
            ""SucceededSessions"" integer NOT NULL,
            ""FailedSessions"" integer NOT NULL,
            ""SkippedSessions"" integer NOT NULL,
            ""LastError"" text NULL,
            ""ProgressPercent"" double precision NULL
        );
    END IF;
END $$;");

    await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.tables
        WHERE table_name = 'BatchCoachingJobItems'
    ) THEN
        CREATE TABLE ""BatchCoachingJobItems"" (
            ""Id"" uuid NOT NULL PRIMARY KEY,
            ""JobId"" uuid NOT NULL REFERENCES ""BatchCoachingJobs""(""Id"") ON DELETE CASCADE,
            ""SessionId"" uuid NOT NULL,
            ""Status"" character varying(16) NOT NULL,
            ""Attempts"" integer NOT NULL,
            ""StartedAtUtc"" timestamp with time zone NULL,
            ""CompletedAtUtc"" timestamp with time zone NULL,
            ""ResultSource"" character varying(32) NULL,
            ""LlmRunId"" uuid NULL,
            ""Error"" text NULL
        );
    END IF;
END $$;");

    await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE schemaname = 'public' AND indexname = 'IX_BatchCoachingJobItems_JobId_Status'
    ) THEN
        CREATE INDEX ""IX_BatchCoachingJobItems_JobId_Status"" ON ""BatchCoachingJobItems"" (""JobId"", ""Status"");
    END IF;
END $$;");

    await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE schemaname = 'public' AND indexname = 'UX_BatchCoachingJobItems_JobId_SessionId'
    ) THEN
        CREATE UNIQUE INDEX ""UX_BatchCoachingJobItems_JobId_SessionId"" ON ""BatchCoachingJobItems"" (""JobId"", ""SessionId"");
    END IF;
END $$;");

    await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE schemaname = 'public' AND indexname = 'UX_Users_EmailNormalized'
    ) THEN
        CREATE UNIQUE INDEX ""UX_Users_EmailNormalized"" ON ""Users"" (""EmailNormalized"");
    END IF;
END $$;");

    await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_name = 'Users' AND column_name = 'Role'
    ) THEN
        ALTER TABLE ""Users"" ADD COLUMN ""Role"" character varying(16) NOT NULL DEFAULT 'User';
    END IF;
END $$;");

    await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE schemaname = 'public' AND indexname = 'IX_Users_Role'
    ) THEN
        CREATE INDEX ""IX_Users_Role"" ON ""Users"" (""Role"");
    END IF;
END $$;");

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

    await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_name = 'Sessions' AND column_name = 'ScoringProfile'
    ) THEN
        ALTER TABLE ""Sessions"" ADD COLUMN ""ScoringProfile"" text NULL;
    END IF;
END $$;");

    await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1
        FROM information_schema.columns
        WHERE table_name = 'Sessions' AND column_name = 'UserId'
    ) THEN
        ALTER TABLE ""Sessions"" ADD COLUMN ""UserId"" uuid NULL;
    END IF;
END $$;");

    await db.Database.ExecuteSqlRawAsync(@"
DO $$
BEGIN
    IF NOT EXISTS (
        SELECT 1 FROM pg_indexes
        WHERE schemaname = 'public' AND indexname = 'IX_Sessions_UserId_CreatedAt'
    ) THEN
        CREATE INDEX ""IX_Sessions_UserId_CreatedAt"" ON ""Sessions"" (""UserId"", ""CreatedAt"");
    END IF;
END $$;");

    var passwordHasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher<User>>();
    await SeedAdminIfConfiguredAsync(
        db,
        passwordHasher,
        authOptions,
        app.Environment,
        app.Logger);
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";

    if (context.Request.Path.StartsWithSegments("/swagger", StringComparison.OrdinalIgnoreCase))
    {
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'";
    }
    else
    {
        context.Response.Headers["Content-Security-Policy"] = "default-src 'self'";
    }

    await next();
});

app.Use(async (context, next) =>
{
    try
    {
        await next();
    }
    catch (Exception)
    {
        var problem = new ProblemDetails
        {
            Title = "Internal Server Error",
            Status = StatusCodes.Status500InternalServerError,
            Detail = "An unexpected error occurred.",
            Type = "https://datatracker.ietf.org/doc/html/rfc7807"
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;

        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        await context.Response.WriteAsJsonAsync(problem);
    }
});

app.Use(async (context, next) =>
{
    var path = context.Request.Path;
    var isProtectedIngestPath =
        path.StartsWithSegments("/api/sessions", StringComparison.OrdinalIgnoreCase) &&
        (path.Value?.EndsWith("/events/batch", StringComparison.OrdinalIgnoreCase) == true ||
         path.Value?.EndsWith("/transcript/batch", StringComparison.OrdinalIgnoreCase) == true ||
         path.Value?.EndsWith("/llm/coach", StringComparison.OrdinalIgnoreCase) == true);

    if (isProtectedIngestPath && context.Request.ContentLength.HasValue && context.Request.ContentLength.Value > ingestionBodyLimitBytes)
    {
        var problem = new ProblemDetails
        {
            Title = "Payload too large",
            Status = StatusCodes.Status413PayloadTooLarge,
            Detail = "Request body exceeds 5MB limit.",
            Type = "https://datatracker.ietf.org/doc/html/rfc7807"
        };
        problem.Extensions["traceId"] = context.TraceIdentifier;

        context.Response.StatusCode = StatusCodes.Status413PayloadTooLarge;
        await context.Response.WriteAsJsonAsync(problem);
        return;
    }

    await next();
});

app.Use(async (context, next) =>
{
    var telemetry = context.RequestServices.GetRequiredService<ApiTelemetry>();
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("RequestTelemetry");

    var sw = Stopwatch.StartNew();
    try
    {
        await next();
    }
    catch (Exception ex)
    {
        var routeSessionId = context.GetRouteValue("sessionId")?.ToString()
            ?? context.GetRouteValue("id")?.ToString();

        using (logger.BeginScope(new Dictionary<string, object?>
        {
            ["sessionId"] = routeSessionId,
            ["route"] = context.Request.Path.Value,
            ["requestId"] = context.TraceIdentifier
        }))
        {
            logger.LogError(ex, "Unhandled exception for request");
        }

        throw;
    }
    finally
    {
        sw.Stop();
        var endpoint = context.GetEndpoint() as RouteEndpoint;
        var route = endpoint?.RoutePattern.RawText ?? context.Request.Path.Value ?? "unknown";
        var method = context.Request.Method;
        var status = context.Response.StatusCode.ToString();

        telemetry.HttpRequestDurationMs.Record(
            sw.Elapsed.TotalMilliseconds,
            new KeyValuePair<string, object?>("route", route),
            new KeyValuePair<string, object?>("method", method),
            new KeyValuePair<string, object?>("status", status));
    }
});

app.UseHttpsRedirection();
app.UseCors("Frontend");
app.UseRateLimiter();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllers();

app.Run();

static string GetClientIp(HttpContext context)
{
    return context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
}

static async Task SeedAdminIfConfiguredAsync(
    ApplicationDbContext db,
    IPasswordHasher<User> passwordHasher,
    AuthOptions authOptions,
    IHostEnvironment environment,
    ILogger logger)
{
    var seedEmail = (authOptions.SeedAdminEmail ?? string.Empty).Trim().ToLowerInvariant();
    var seedPassword = authOptions.SeedAdminPassword ?? string.Empty;

    if (string.IsNullOrWhiteSpace(seedEmail))
    {
        return;
    }

    if (!environment.IsDevelopment())
    {
        logger.LogWarning("Auth:SeedAdminEmail is configured outside Development environment.");
    }

    if (string.IsNullOrWhiteSpace(seedPassword))
    {
        logger.LogWarning("Auth:SeedAdminEmail is configured but Auth:SeedAdminPassword is missing. Admin user seed skipped.");
        return;
    }

    if (seedPassword.Length < 8)
    {
        logger.LogWarning("Auth:SeedAdminPassword must be at least 8 characters. Admin user seed skipped.");
        return;
    }

    var existingUser = await db.Users.FirstOrDefaultAsync(u => u.EmailNormalized == seedEmail);
    if (existingUser == null)
    {
        var admin = new User
        {
            Id = Guid.NewGuid(),
            Email = seedEmail,
            EmailNormalized = seedEmail,
            DisplayName = "Admin",
            CreatedAtUtc = DateTime.UtcNow,
            IsActive = true,
            Role = UserRoles.Admin
        };
        admin.PasswordHash = passwordHasher.HashPassword(admin, seedPassword);

        db.Users.Add(admin);
        await db.SaveChangesAsync();

        logger.LogWarning("Seed admin user created for email {Email}. Replace credentials immediately.", seedEmail);
        return;
    }

    var changed = false;

    if (!existingUser.IsActive)
    {
        existingUser.IsActive = true;
        changed = true;
    }

    if (!string.Equals(existingUser.Role, UserRoles.Admin, StringComparison.OrdinalIgnoreCase))
    {
        existingUser.Role = UserRoles.Admin;
        changed = true;
    }

    if (changed)
    {
        existingUser.PasswordHash = passwordHasher.HashPassword(existingUser, seedPassword);
        await db.SaveChangesAsync();
        logger.LogWarning("Seed admin user promoted/updated for email {Email}. Replace credentials immediately.", seedEmail);
    }
}

public partial class Program { }
