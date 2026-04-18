using FluentAssertions;
using InterviewCoach.Api.Services;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Tests;

public class BatchCoachingJobServiceTests
{
    [Fact]
    public async Task CreateJob_WithExplicitSessionIds_Works()
    {
        using var setup = CreateSetup();
        await SeedSessionsAsync(setup.Db, 2);

        var request = new BatchCoachingJobCreateRequest
        {
            SessionIds = setup.Db.Sessions.Select(s => s.Id).ToList(),
            Options = new BatchCoachingJobOptionsDto { MaxSessions = 10 }
        };

        var result = await setup.Service.CreateJobAsync(request, Guid.NewGuid());

        result.Success.Should().BeTrue();
        result.TotalSessions.Should().Be(2);

        var itemCount = await setup.Db.BatchCoachingJobItems.CountAsync(i => i.JobId == result.JobId);
        itemCount.Should().Be(2);
    }

    [Fact]
    public async Task CreateJob_WithFilters_RespectsMaxSessionsCap()
    {
        using var setup = CreateSetup();
        await SeedSessionsAsync(setup.Db, 10, language: "tr");

        var request = new BatchCoachingJobCreateRequest
        {
            Filters = new BatchCoachingJobFilterDto { Language = "tr" },
            Options = new BatchCoachingJobOptionsDto { MaxSessions = 3 }
        };

        var result = await setup.Service.CreateJobAsync(request, Guid.NewGuid());

        result.Success.Should().BeTrue();
        result.TotalSessions.Should().Be(3);
    }

    [Fact]
    public async Task Worker_ProcessesQueuedJob_UpdatesCounts()
    {
        var fake = new FakeOrchestrator();
        using var setup = CreateSetup(fake);
        await SeedSessionsAsync(setup.Db, 3);

        var sessionIds = await setup.Db.Sessions.Select(x => x.Id).ToListAsync();
        foreach (var id in sessionIds)
        {
            fake.SetOutcome(id, success: true);
        }

        var created = await setup.Service.CreateJobAsync(new BatchCoachingJobCreateRequest
        {
            SessionIds = sessionIds,
            Options = new BatchCoachingJobOptionsDto { Force = true, Parallelism = 1 }
        }, Guid.NewGuid());

        var processed = await setup.Service.ProcessNextQueuedJobAsync();
        processed.Should().BeTrue();

        var job = await setup.Db.BatchCoachingJobs.FirstAsync(j => j.Id == created.JobId);
        job.Status.Should().Be(BatchCoachingJobStatus.Completed);
        job.ProcessedSessions.Should().Be(3);
        job.SucceededSessions.Should().Be(3);
        job.FailedSessions.Should().Be(0);
    }

    [Fact]
    public async Task Cancel_StopsFurtherProcessing_BestEffort()
    {
        var fake = new FakeOrchestrator(delayMs: 250);
        using var setup = CreateSetup(fake, new BatchCoachingOptions { Enabled = true, MaxParallelism = 1, PollIntervalSeconds = 1, MaxSessionsPerJob = 500 });
        await SeedSessionsAsync(setup.Db, 5);

        var sessionIds = await setup.Db.Sessions.Select(x => x.Id).ToListAsync();
        foreach (var id in sessionIds)
        {
            fake.SetOutcome(id, success: true);
        }

        var created = await setup.Service.CreateJobAsync(new BatchCoachingJobCreateRequest
        {
            SessionIds = sessionIds,
            Options = new BatchCoachingJobOptionsDto { Parallelism = 1 }
        }, Guid.NewGuid());

        var processingTask = setup.Service.ProcessNextQueuedJobAsync();
        await Task.Delay(120);

        await setup.Service.CancelJobAsync(created.JobId);
        await processingTask;

        var job = await setup.Db.BatchCoachingJobs.FirstAsync(j => j.Id == created.JobId);
        job.Status.Should().Be(BatchCoachingJobStatus.Canceled);
        job.ProcessedSessions.Should().BeLessThan(job.TotalSessions);
    }

    [Fact]
    public async Task OnlyIfNoCoach_SkipsExistingCoachingSessions()
    {
        var fake = new FakeOrchestrator();
        using var setup = CreateSetup(fake);
        await SeedSessionsAsync(setup.Db, 2);

        var ids = await setup.Db.Sessions.Select(s => s.Id).OrderBy(x => x).ToListAsync();
        fake.SetOutcome(ids[0], success: true);
        fake.SetOutcome(ids[1], success: true);

        setup.Db.LlmRuns.Add(new LlmRun
        {
            Id = Guid.NewGuid(),
            SessionId = ids[0],
            Kind = "coach",
            PromptVersion = "coach_v1",
            Model = "m",
            InputHash = Guid.NewGuid().ToString("N"),
            OutputJson = "{}",
            CreatedAt = DateTime.UtcNow
        });
        await setup.Db.SaveChangesAsync();

        var created = await setup.Service.CreateJobAsync(new BatchCoachingJobCreateRequest
        {
            SessionIds = ids,
            Filters = new BatchCoachingJobFilterDto { OnlyIfNoCoach = true },
            Options = new BatchCoachingJobOptionsDto { Force = false }
        }, Guid.NewGuid());

        await setup.Service.ProcessNextQueuedJobAsync();

        var job = await setup.Db.BatchCoachingJobs.FirstAsync(j => j.Id == created.JobId);
        job.SkippedSessions.Should().BeGreaterThanOrEqualTo(1);
    }

    private static async Task SeedSessionsAsync(ApplicationDbContext db, int count, string language = "en")
    {
        for (var i = 0; i < count; i++)
        {
            db.Sessions.Add(new Session
            {
                Id = Guid.NewGuid(),
                CreatedAt = DateTime.UtcNow.AddMinutes(-i),
                Language = language,
                SelectedRole = "backend",
                Status = "Completed",
                SettingsJson = "{}",
                StatsJson = "{}"
            });
        }

        await db.SaveChangesAsync();
    }

    private static TestSetup CreateSetup(FakeOrchestrator? fake = null, BatchCoachingOptions? options = null)
    {
        var services = new ServiceCollection();
        var dbName = $"batch-job-tests-{Guid.NewGuid()}";

        services.AddDbContext<ApplicationDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.Configure<BatchCoachingOptions>(opts =>
        {
            var src = options ?? new BatchCoachingOptions
            {
                Enabled = true,
                PollIntervalSeconds = 1,
                MaxParallelism = 2,
                MaxSessionsPerJob = 500,
                ResetStaleRunningJobsOnStartup = true,
                StaleRunningJobMinutes = 30,
                ItemRetryCount = 0
            };

            opts.Enabled = src.Enabled;
            opts.PollIntervalSeconds = src.PollIntervalSeconds;
            opts.MaxParallelism = src.MaxParallelism;
            opts.MaxSessionsPerJob = src.MaxSessionsPerJob;
            opts.ResetStaleRunningJobsOnStartup = src.ResetStaleRunningJobsOnStartup;
            opts.StaleRunningJobMinutes = src.StaleRunningJobMinutes;
            opts.ItemRetryCount = src.ItemRetryCount;
        });

        services.AddSingleton<ApiTelemetry>();
        services.AddScoped<IBatchCoachingJobService, BatchCoachingJobService>();
        services.AddScoped<ILlmCoachingOrchestrator>(_ => fake ?? new FakeOrchestrator());
        services.AddLogging();

        var provider = services.BuildServiceProvider();
        var scope = provider.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var svc = scope.ServiceProvider.GetRequiredService<IBatchCoachingJobService>();

        return new TestSetup(provider, scope, db, svc);
    }

    private sealed class TestSetup : IDisposable
    {
        public TestSetup(ServiceProvider provider, IServiceScope scope, ApplicationDbContext db, IBatchCoachingJobService service)
        {
            Provider = provider;
            Scope = scope;
            Db = db;
            Service = service;
        }

        public ServiceProvider Provider { get; }
        public IServiceScope Scope { get; }
        public ApplicationDbContext Db { get; }
        public IBatchCoachingJobService Service { get; }

        public void Dispose()
        {
            Scope.Dispose();
            Provider.Dispose();
        }
    }

    private sealed class FakeOrchestrator : ILlmCoachingOrchestrator
    {
        private readonly Dictionary<Guid, bool> _outcomes = new();
        private readonly int _delayMs;

        public FakeOrchestrator(int delayMs = 0)
        {
            _delayMs = delayMs;
        }

        public void SetOutcome(Guid sessionId, bool success)
        {
            _outcomes[sessionId] = success;
        }

        public Task<LlmOptimizationPreviewDto?> PreviewOptimizationAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<LlmOptimizationPreviewDto?>(new LlmOptimizationPreviewDto
            {
                SessionId = sessionId,
                ComplexityScore = 10,
                ComplexityBand = "Low",
                TierSelected = "small",
                ModelSelected = "model"
            });
        }

        public async Task<LlmCoachingOrchestrationResult> ExecuteAsync(Guid sessionId, bool force, CancellationToken cancellationToken = default)
        {
            if (_delayMs > 0)
            {
                await Task.Delay(_delayMs, cancellationToken);
            }

            if (!_outcomes.TryGetValue(sessionId, out var success))
            {
                success = true;
            }

            if (success)
            {
                var response = new LlmCoachingResponse(
                    new LlmRubric(3, 3, 3, 3, 3),
                    70,
                    [
                        new LlmFeedbackItem("audio", 2, "title1", "ev1", [0, 1000], "s1", "e1"),
                        new LlmFeedbackItem("vision", 2, "title2", "ev2", [1000, 2000], "s2", "e2"),
                        new LlmFeedbackItem("content", 2, "title3", "ev3", [2000, 3000], "s3", "e3"),
                        new LlmFeedbackItem("structure", 2, "title4", "ev4", [3000, 4000], "s4", "e4"),
                        new LlmFeedbackItem("audio", 2, "title5", "ev5", [4000, 5000], "s5", "e5")
                    ],
                    [new LlmDrill("drill", ["step"], 5)]);

                return LlmCoachingOrchestrationResult.CreateSuccess(response, new LlmCoachingOrchestrationMetadata
                {
                    SourcePath = force ? "primary" : "cache_same_input",
                    ModelUsed = "fake-model",
                    Attempts = 1,
                    FallbackUsed = false,
                    ValidationFailures = 0,
                    GuardrailFailures = 0
                });
            }

            return LlmCoachingOrchestrationResult.CreateFailed(new LlmCoachingOrchestrationMetadata
            {
                SourcePath = "failed",
                ModelUsed = "fake-model",
                Attempts = 1,
                FallbackUsed = false,
                ValidationFailures = 1,
                GuardrailFailures = 0
            }, "simulated failure");
        }
    }
}
