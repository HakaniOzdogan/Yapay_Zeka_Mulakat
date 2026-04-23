using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using FluentAssertions;
using InterviewCoach.Api.Services;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Tests;

public class LlmCoachingOrchestratorTests
{
    [Fact]
    public async Task PrimarySuccess_FirstAttempt()
    {
        var sessionId = Guid.NewGuid();
        var db = CreateDb();
        await SeedSessionAsync(db, sessionId);

        var fakeLlm = new FakeLlmCoachingService();
        fakeLlm.Enqueue("primary-model", () => LlmCoachingResult.Success(CreateValidResponse(), JsonSerializer.Serialize(CreateValidResponse())));

        var orchestrator = CreateOrchestrator(db, fakeLlm, CreateDefaultSummary(sessionId), new LlmOptions
        {
            Model = "primary-model"
        });

        var result = await orchestrator.ExecuteAsync(sessionId, force: true);

        result.Success.Should().BeTrue();
        result.Metadata.SourcePath.Should().Be("primary");
        result.Metadata.Attempts.Should().Be(1);
        result.Metadata.ModelUsed.Should().Be("primary-model");
    }

    [Fact]
    public async Task PrimaryTimeout_ThenRetrySuccess()
    {
        var sessionId = Guid.NewGuid();
        var db = CreateDb();
        await SeedSessionAsync(db, sessionId);

        var fakeLlm = new FakeLlmCoachingService();
        fakeLlm.Enqueue("primary-model", () => throw new OperationCanceledException("timeout"));
        fakeLlm.Enqueue("primary-model", () => LlmCoachingResult.Success(CreateValidResponse(), JsonSerializer.Serialize(CreateValidResponse())));

        var orchestrator = CreateOrchestrator(db, fakeLlm, CreateDefaultSummary(sessionId), new LlmOptions
        {
            Model = "primary-model",
            Retry = new LlmRetryOptions
            {
                MaxAttemptsPrimary = 2,
                RetryOnTimeout = true,
                RetryOnInvalidJson = true,
                RetryOnHttp5xx = true,
                BackoffMs = [0, 0]
            }
        });

        var result = await orchestrator.ExecuteAsync(sessionId, force: true);

        result.Success.Should().BeTrue();
        result.Metadata.SourcePath.Should().Be("primary");
        result.Metadata.Attempts.Should().Be(2);
    }

    [Fact]
    public async Task PrimaryInvalidJson_ThenFallbackModelSuccess()
    {
        var sessionId = Guid.NewGuid();
        var db = CreateDb();
        await SeedSessionAsync(db, sessionId);

        var fakeLlm = new FakeLlmCoachingService();
        fakeLlm.Enqueue("primary-model", () => LlmCoachingResult.Failure(["invalid json"]));
        fakeLlm.Enqueue("fallback-model", () => LlmCoachingResult.Success(CreateValidResponse(), JsonSerializer.Serialize(CreateValidResponse())));

        var orchestrator = CreateOrchestrator(db, fakeLlm, CreateDefaultSummary(sessionId), new LlmOptions
        {
            Model = "primary-model",
            FallbackModels = ["fallback-model"],
            Retry = new LlmRetryOptions
            {
                MaxAttemptsPrimary = 1,
                RetryOnInvalidJson = true,
                RetryOnTimeout = true,
                RetryOnHttp5xx = true,
                BackoffMs = [0]
            },
            Fallback = new LlmFallbackOptions
            {
                Enabled = true,
                TryFallbackModelsOnFailure = true,
                UseCachedSameInputHashIfAllFail = true,
                UseCachedAnyPreviousForSessionIfSameInputMissing = false,
                CacheFallbackMaxAgeHours = 168
            }
        });

        var result = await orchestrator.ExecuteAsync(sessionId, force: true);

        result.Success.Should().BeTrue();
        result.Metadata.SourcePath.Should().Be("fallback_model");
        result.Metadata.FallbackUsed.Should().BeTrue();
        result.Metadata.ModelUsed.Should().Be("fallback-model");
    }

    [Fact]
    public async Task AllModelsFail_ThenSameInputCacheReturned()
    {
        var sessionId = Guid.NewGuid();
        var db = CreateDb();
        await SeedSessionAsync(db, sessionId);

        var summary = CreateDefaultSummary(sessionId);
        var hash = ComputeInputHash(summary);
        var cachedResponse = CreateValidResponse();

        db.LlmRuns.Add(new LlmRun
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Kind = "coach",
            PromptVersion = "coach_v1",
            Model = "cached-model",
            InputHash = hash,
            OutputJson = JsonSerializer.Serialize(cachedResponse),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var fakeLlm = new FakeLlmCoachingService();
        fakeLlm.Enqueue("primary-model", () => LlmCoachingResult.Failure(["invalid"]));
        fakeLlm.Enqueue("fallback-model", () => LlmCoachingResult.Failure(["invalid"]));

        var orchestrator = CreateOrchestrator(db, fakeLlm, summary, new LlmOptions
        {
            Model = "primary-model",
            FallbackModels = ["fallback-model"],
            Retry = new LlmRetryOptions { MaxAttemptsPrimary = 1, BackoffMs = [0] },
            Fallback = new LlmFallbackOptions
            {
                Enabled = true,
                TryFallbackModelsOnFailure = true,
                UseCachedSameInputHashIfAllFail = true,
                UseCachedAnyPreviousForSessionIfSameInputMissing = false,
                CacheFallbackMaxAgeHours = 168
            }
        });

        var result = await orchestrator.ExecuteAsync(sessionId, force: true);

        result.Success.Should().BeTrue();
        result.Metadata.SourcePath.Should().Be("cache_same_input");
        result.Metadata.ModelUsed.Should().Be("cached-model");
    }

    [Fact]
    public async Task AllFail_NoCache_ReturnsFailed()
    {
        var sessionId = Guid.NewGuid();
        var db = CreateDb();
        await SeedSessionAsync(db, sessionId);

        var fakeLlm = new FakeLlmCoachingService();
        fakeLlm.Enqueue("primary-model", () => LlmCoachingResult.Failure(["invalid"]));
        fakeLlm.Enqueue("fallback-model", () => LlmCoachingResult.Failure(["invalid"]));

        var orchestrator = CreateOrchestrator(db, fakeLlm, CreateDefaultSummary(sessionId), new LlmOptions
        {
            Model = "primary-model",
            FallbackModels = ["fallback-model"],
            Retry = new LlmRetryOptions { MaxAttemptsPrimary = 1, BackoffMs = [0] },
            Fallback = new LlmFallbackOptions
            {
                Enabled = true,
                TryFallbackModelsOnFailure = true,
                UseCachedSameInputHashIfAllFail = false,
                UseCachedAnyPreviousForSessionIfSameInputMissing = false,
                CacheFallbackMaxAgeHours = 168
            }
        });

        var result = await orchestrator.ExecuteAsync(sessionId, force: true);

        result.Success.Should().BeFalse();
        result.Metadata.SourcePath.Should().Be("failed");
    }

    [Fact]
    public async Task ForceFalse_SameInputCacheHit_NoModelCall()
    {
        var sessionId = Guid.NewGuid();
        var db = CreateDb();
        await SeedSessionAsync(db, sessionId);

        var summary = CreateDefaultSummary(sessionId);
        var hash = ComputeInputHash(summary);
        var cachedResponse = CreateValidResponse();

        db.LlmRuns.Add(new LlmRun
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            Kind = "coach",
            PromptVersion = "coach_v1",
            Model = "cached-model",
            InputHash = hash,
            OutputJson = JsonSerializer.Serialize(cachedResponse),
            CreatedAt = DateTime.UtcNow
        });
        await db.SaveChangesAsync();

        var fakeLlm = new FakeLlmCoachingService();

        var orchestrator = CreateOrchestrator(db, fakeLlm, summary, new LlmOptions
        {
            Model = "primary-model"
        });

        var result = await orchestrator.ExecuteAsync(sessionId, force: false);

        result.Success.Should().BeTrue();
        result.Metadata.SourcePath.Should().Be("cache_same_input");
        fakeLlm.Calls.Should().Be(0);
    }

    [Fact]
    public async Task PersistedPayload_ContainsOptimizationMetadata()
    {
        var sessionId = Guid.NewGuid();
        var db = CreateDb();
        await SeedSessionAsync(db, sessionId);

        var fakeLlm = new FakeLlmCoachingService();
        fakeLlm.Enqueue("primary-model", () => LlmCoachingResult.Success(CreateValidResponse(), JsonSerializer.Serialize(CreateValidResponse())));

        var orchestrator = CreateOrchestrator(
            db,
            fakeLlm,
            CreateDefaultSummary(sessionId),
            new LlmOptions
        {
            Model = "primary-model"
        },
            optimizationEnabled: true);

        var result = await orchestrator.ExecuteAsync(sessionId, force: true);

        result.Success.Should().BeTrue();

        var savedEvent = await db.MetricEvents.FirstOrDefaultAsync(x => x.SessionId == sessionId && x.Type == "llm_coaching_v1");
        savedEvent.Should().NotBeNull();

        using var doc = JsonDocument.Parse(savedEvent!.PayloadJson);
        doc.RootElement.TryGetProperty("_meta", out var meta).Should().BeTrue();
        meta.TryGetProperty("optimization", out var optimization).Should().BeTrue();
        optimization.TryGetProperty("complexityScore", out _).Should().BeTrue();
        optimization.TryGetProperty("tierUsed", out _).Should().BeTrue();
        optimization.TryGetProperty("modelChosen", out _).Should().BeTrue();
    }

    private static LlmCoachingOrchestrator CreateOrchestrator(
        ApplicationDbContext db,
        FakeLlmCoachingService llm,
        EvidenceSummaryDto summary,
        LlmOptions options,
        bool optimizationEnabled = false)
    {
        var evidence = new FakeEvidenceSummaryService(summary);
        var guardrails = new LlmCoachingGuardrailsService(Options.Create(new LlmGuardrailsOptions()));
        var optimizationOptions = optimizationEnabled
            ? new LlmOptimizationOptions
            {
                Enabled = true,
                ModelRouting = new LlmOptimizationModelRoutingOptions
                {
                    Enabled = true,
                    Routes = new LlmOptimizationRoutes
                    {
                        Low = options.Model,
                        Medium = options.Model,
                        High = options.Model
                    }
                }
            }
            : new LlmOptimizationOptions { Enabled = false };

        return new LlmCoachingOrchestrator(
            db,
            evidence,
            llm,
            guardrails,
            new LlmOptimizationService(Options.Create(optimizationOptions), Options.Create(options)),
            new ApiTelemetry(),
            NullLogger<LlmCoachingOrchestrator>.Instance,
            Options.Create(options));
    }

    private static ApplicationDbContext CreateDb()
    {
        var options = new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"llm-orchestrator-tests-{Guid.NewGuid()}")
            .Options;

        return new ApplicationDbContext(options);
    }

    private static async Task SeedSessionAsync(ApplicationDbContext db, Guid sessionId)
    {
        db.Sessions.Add(new Session
        {
            Id = sessionId,
            CreatedAt = DateTime.UtcNow,
            Language = "en",
            SelectedRole = "SoftwareEngineer",
            Status = "Created",
            SettingsJson = "{}",
            StatsJson = "{}"
        });
        await db.SaveChangesAsync();
    }

    private static EvidenceSummaryDto CreateDefaultSummary(Guid sessionId)
    {
        return new EvidenceSummaryDto
        {
            SessionId = sessionId,
            Language = "en",
            HighLevel = new HighLevelDto
            {
                DurationMs = 20000,
                OverallScore = 70,
                TopIssues =
                [
                    new TopIssueDto
                    {
                        Issue = "eye_contact",
                        Evidence = "Eye contact dips around 1000ms",
                        TimeRangeMs = [1000, 3000]
                    }
                ]
            },
            Signals = new SignalsDto
            {
                Vision = new VisionSignalsDto
                {
                    EyeContactAvg = 0.6,
                    PostureAvg = 0.7,
                    FidgetAvg = 0.3,
                    HeadJitterAvg = 0.2
                },
                Audio = new AudioSignalsDto
                {
                    WpmMedian = 140,
                    FillerPerMin = 1,
                    PauseMsPerMin = 250
                }
            },
            WorstWindows =
            [
                new WorstWindowDto
                {
                    StartMs = 1000,
                    EndMs = 3000,
                    Metrics = new WindowMetricsDto
                    {
                        EyeContact = 0.4,
                        Posture = 0.7,
                        Fidget = 0.2,
                        HeadJitter = 0.2,
                        Wpm = 145,
                        Filler = 1,
                        PauseMs = 300
                    },
                    Reason = "low eye contact"
                }
            ],
            TranscriptSlices =
            [
                new TranscriptSliceDto
                {
                    StartMs = 1000,
                    EndMs = 3000,
                    Text = "I designed the service to be resilient under load"
                }
            ],
            Patterns =
            [
                new PatternSummaryDto
                {
                    Type = "structure",
                    StartMs = 1000,
                    EndMs = 3000,
                    Severity = 2,
                    Evidence = "Could be more concise"
                }
            ]
        };
    }

    private static LlmCoachingResponse CreateValidResponse()
    {
        var feedback = new List<LlmFeedbackItem>
        {
            new("vision", 3, "Eye contact dips", "Eye contact dropped in window 1000-3000", [1000, 3000], "Keep your gaze near camera for 2-3 sentence chunks.", "I will keep steady eye contact while explaining."),
            new("audio", 2, "Filler burst", "Filler count increased around 4000-5200", [4000, 5200], "Pause silently before complex points.", "Let me pause for a moment and structure this."),
            new("structure", 3, "Answer structure weak", "Response lacked clear opening between 7000-11000", [7000, 11000], "Use Situation-Action-Result ordering.", "Situation was X, action was Y, result was Z."),
            new("content", 2, "Depth can improve", "Technical detail was brief in 12000-15000", [12000, 15000], "Add one concrete metric and tradeoff.", "We cut latency by 30% with this tradeoff."),
            new("audio", 2, "Long pauses", "Pause duration rose in 16000-19000", [16000, 19000], "Keep transition phrases ready to avoid dead air.", "Next, I will cover implementation details.")
        };

        var drills = new List<LlmDrill>
        {
            new("Structured response drill", ["Pick one prompt", "Answer with STAR", "Review timing"], 8)
        };

        return new LlmCoachingResponse(new LlmRubric(3, 3, 3, 3, 3), 68, feedback, drills);
    }

    private static string ComputeInputHash(EvidenceSummaryDto summary)
    {
        var json = JsonSerializer.Serialize(summary);
        using var doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonical(writer, doc.RootElement);
        }

        var canonical = Encoding.UTF8.GetString(stream.ToArray());
        var bytes = Encoding.UTF8.GetBytes(canonical);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static void WriteCanonical(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(x => x.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonical(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonical(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            default:
                writer.WriteNullValue();
                break;
        }
    }

    private sealed class FakeEvidenceSummaryService : IEvidenceSummaryService
    {
        private readonly EvidenceSummaryDto _summary;

        public FakeEvidenceSummaryService(EvidenceSummaryDto summary)
        {
            _summary = summary;
        }

        public Task<EvidenceSummaryDto?> BuildAsync(Guid sessionId, CancellationToken cancellationToken = default)
        {
            return Task.FromResult<EvidenceSummaryDto?>(_summary.SessionId == sessionId ? _summary : null);
        }
    }

    private sealed class FakeLlmCoachingService : ILlmCoachingService
    {
        private readonly Dictionary<string, Queue<Func<LlmCoachingResult>>> _plans = new(StringComparer.OrdinalIgnoreCase);
        private readonly LlmCoachingService _parser = new(new DummyLlmClient());

        public int Calls { get; private set; }

        public void Enqueue(string model, Func<LlmCoachingResult> plan)
        {
            if (!_plans.TryGetValue(model, out var queue))
            {
                queue = new Queue<Func<LlmCoachingResult>>();
                _plans[model] = queue;
            }

            queue.Enqueue(plan);
        }

        public Task<LlmCoachingResult> GenerateAsync(EvidenceSummaryDto evidenceSummary, CancellationToken cancellationToken = default)
        {
            return GenerateWithModelAsync(evidenceSummary, string.Empty, cancellationToken);
        }

        public Task<LlmCoachingResult> GenerateWithModelAsync(EvidenceSummaryDto evidenceSummary, string model, CancellationToken cancellationToken = default)
        {
            Calls++;

            if (!_plans.TryGetValue(model, out var queue) || queue.Count == 0)
            {
                return Task.FromResult(LlmCoachingResult.Failure(["no plan"]));
            }

            var step = queue.Dequeue();
            return Task.FromResult(step());
        }

        public bool TryParseAndValidate(string json, out LlmCoachingResponse? response, out List<string> errors)
        {
            return _parser.TryParseAndValidate(json, out response, out errors);
        }
    }

    private sealed class DummyLlmClient : ILlmClient
    {
        public Task<LlmJsonResponse> GenerateJsonAsync(LlmJsonRequest request, CancellationToken cancellationToken = default)
        {
            throw new InvalidOperationException("Not used in this test.");
        }
    }
}
