using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using NBomber.Contracts;
using NBomber.Contracts.Stats;
using NBomber.CSharp;

var settings = PerfSettings.Load();
var artifactsDirectory = ResolveArtifactsDirectory();
Directory.CreateDirectory(artifactsDirectory);

Console.WriteLine($"[perf] Base URL: {settings.BaseUrl}");
Console.WriteLine($"[perf] API URL: {settings.ApiBaseUrl}");
Console.WriteLine($"[perf] Artifacts: {artifactsDirectory}");

using var bootstrapClient = new HttpClient
{
    BaseAddress = new Uri(settings.ApiBaseUrl),
    Timeout = TimeSpan.FromSeconds(30)
};

var auth = new ApiAuthClient(bootstrapClient);
var token = await auth.AcquireTokenAsync(settings);
Console.WriteLine("[perf] Auth token acquired.");

var api = new PerfApiClient(settings.ApiBaseUrl, token);

var eventGenerator = new EventBatchGenerator(batchSize: settings.EventsBatchSize, duplicateRatePercent: 5);
var transcriptGenerator = new TranscriptBatchGenerator(batchSize: settings.TranscriptBatchSize);

var eventsScenarioSessionId = await api.CreateSessionAsync("Software Engineer", "en");
var transcriptScenarioSessionId = await api.CreateSessionAsync("Software Engineer", "en");
var finalizeScenarioSessionId = await api.CreateSessionAsync("Software Engineer", "en");

Console.WriteLine("[perf] Seeding finalize scenario session...");
await SeedFinalizeSessionAsync(api, finalizeScenarioSessionId, eventGenerator, transcriptGenerator);
Console.WriteLine("[perf] Finalize session seeded.");

var eventsScenario = Scenario.Create("events_batch_ingest", async context =>
{
    var step = await Step.Run("events_batch", context, async () =>
    {
        var batch = eventGenerator.NextBatch();
        using var response = await api.PostEventsBatchAsync(eventsScenarioSessionId, batch);

        return response.StatusCode switch
        {
            HttpStatusCode.OK => Response.Ok(statusCode: "200"),
            HttpStatusCode.TooManyRequests => Response.Fail(statusCode: "429"),
            _ => Response.Fail(statusCode: ((int)response.StatusCode).ToString())
        };
    });

    return step;
})
.WithWarmUpDuration(TimeSpan.FromSeconds(settings.WarmupSec))
.WithLoadSimulations(BuildLoadSimulations(settings));

var transcriptScenario = Scenario.Create("transcript_batch_ingest", async context =>
{
    var step = await Step.Run("transcript_batch", context, async () =>
    {
        var batch = transcriptGenerator.NextBatch();
        using var response = await api.PostTranscriptBatchAsync(transcriptScenarioSessionId, batch);

        return response.StatusCode switch
        {
            HttpStatusCode.OK => Response.Ok(statusCode: "200"),
            HttpStatusCode.TooManyRequests => Response.Fail(statusCode: "429"),
            _ => Response.Fail(statusCode: ((int)response.StatusCode).ToString())
        };
    });

    return step;
})
.WithWarmUpDuration(TimeSpan.FromSeconds(settings.WarmupSec))
.WithLoadSimulations(BuildLoadSimulations(settings));

var finalizeScenario = Scenario.Create("finalize_and_report", async context =>
{
    var finalizeStep = await Step.Run("finalize", context, async () =>
    {
        using var response = await api.FinalizeSessionAsync(finalizeScenarioSessionId);
        return response.StatusCode == HttpStatusCode.OK
            ? Response.Ok(statusCode: "200")
            : Response.Fail(statusCode: ((int)response.StatusCode).ToString());
    });

    if (!finalizeStep.IsError)
    {
        var reportStep = await Step.Run("get_report", context, async () =>
        {
            using var response = await api.GetReportAsync(finalizeScenarioSessionId);
            return response.StatusCode == HttpStatusCode.OK
                ? Response.Ok(statusCode: "200")
                : Response.Fail(statusCode: ((int)response.StatusCode).ToString());
        });

        if (reportStep.IsError)
            return reportStep;
    }

    return finalizeStep;
})
.WithWarmUpDuration(TimeSpan.FromSeconds(settings.WarmupSec))
.WithLoadSimulations(BuildLoadSimulations(settings));

var result = NBomberRunner
    .RegisterScenarios(eventsScenario, transcriptScenario, finalizeScenario)
    .WithReportFolder(artifactsDirectory)
    .WithReportFileName($"perf_baseline_{DateTime.UtcNow:yyyyMMdd_HHmmss}")
    .Run();

PrintSummary(result);
SaveSummaryJson(result, artifactsDirectory);
return;

static async Task SeedFinalizeSessionAsync(
    PerfApiClient api,
    Guid sessionId,
    EventBatchGenerator eventGenerator,
    TranscriptBatchGenerator transcriptGenerator)
{
    for (var i = 0; i < 12; i++)
    {
        var eventsBatch = eventGenerator.NextBatch();
        using var response = await api.PostEventsBatchAsync(sessionId, eventsBatch);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Failed to seed events: {(int)response.StatusCode}");
    }

    for (var i = 0; i < 6; i++)
    {
        var transcriptBatch = transcriptGenerator.NextBatch();
        using var response = await api.PostTranscriptBatchAsync(sessionId, transcriptBatch);
        if (response.StatusCode != HttpStatusCode.OK)
            throw new InvalidOperationException($"Failed to seed transcript: {(int)response.StatusCode}");
    }
}

static string ResolveArtifactsDirectory()
{
    var current = new DirectoryInfo(Directory.GetCurrentDirectory());
    while (current != null)
    {
        if (Directory.Exists(Path.Combine(current.FullName, ".git")))
            return Path.Combine(current.FullName, "artifacts", "perf");

        current = current.Parent;
    }

    return Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "perf");
}

static LoadSimulation[] BuildLoadSimulations(PerfSettings settings)
{
    return
    [
        Simulation.RampingConstant(
            copies: settings.Vus,
            during: TimeSpan.FromSeconds(settings.RampSec)),
        Simulation.KeepConstant(
            copies: settings.Vus,
            during: TimeSpan.FromSeconds(settings.DurationSec))
    ];
}

static void PrintSummary(NodeStats result)
{
    Console.WriteLine();
    Console.WriteLine("=== PERF SUMMARY ===");

    foreach (var scenario in result.ScenarioStats)
    {
        Console.WriteLine($"scenario={scenario.ScenarioName}");
        Console.WriteLine($"  req_sec={scenario.Ok.Request.RPS:F2}");
        Console.WriteLine($"  latency_ms_p50={scenario.Ok.Latency.Percent50:F2}");
        Console.WriteLine($"  latency_ms_p95={scenario.Ok.Latency.Percent95:F2}");
        Console.WriteLine($"  error_rate_percent={scenario.Fail.Request.Percent:F2}");
    }

    Console.WriteLine("====================");
    Console.WriteLine();
}

static void SaveSummaryJson(NodeStats result, string artifactsDirectory)
{
    var timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
    var path = Path.Combine(artifactsDirectory, $"perf_summary_{timestamp}.json");

    var payload = new
    {
        generatedAtUtc = DateTime.UtcNow,
        scenarios = result.ScenarioStats.Select(s => new
        {
            scenario = s.ScenarioName,
            reqSec = Math.Round(Convert.ToDouble(s.Ok.Request.RPS), 2),
            p50Ms = Math.Round(Convert.ToDouble(s.Ok.Latency.Percent50), 2),
            p95Ms = Math.Round(Convert.ToDouble(s.Ok.Latency.Percent95), 2),
            errorRatePercent = Math.Round(Convert.ToDouble(s.Fail.Request.Percent), 2),
            okCount = s.Ok.Request.Count,
            failCount = s.Fail.Request.Count
        })
    };

    var json = JsonSerializer.Serialize(payload, new JsonSerializerOptions
    {
        WriteIndented = true
    });

    File.WriteAllText(path, json);
    Console.WriteLine($"[perf] Summary JSON: {path}");
}

sealed class PerfSettings
{
    public string BaseUrl { get; init; } = "http://localhost:8080";
    public string ApiBaseUrl => BaseUrl.TrimEnd('/') + "/api";

    public string UserEmail { get; init; } = string.Empty;
    public string UserPassword { get; init; } = string.Empty;

    public int Vus { get; init; } = 8;
    public int DurationSec { get; init; } = 25;
    public int WarmupSec { get; init; } = 5;
    public int RampSec { get; init; } = 5;

    public int EventsBatchSize { get; init; } = 50;
    public int TranscriptBatchSize { get; init; } = 40;

    public static PerfSettings Load()
    {
        var configuration = BuildConfiguration();

        var baseUrl = GetString(configuration, "PERF_BASE_URL", "Perf:BaseUrl") ?? "http://localhost:8080";
        return new PerfSettings
        {
            BaseUrl = baseUrl,
            UserEmail = GetString(configuration, "PERF_USER_EMAIL", "Perf:UserEmail") ?? string.Empty,
            UserPassword = GetString(configuration, "PERF_USER_PASSWORD", "Perf:UserPassword") ?? string.Empty,
            Vus = Math.Clamp(GetInt(configuration, 8, "PERF_VUS", "Perf:Vus"), 1, 100),
            DurationSec = Math.Clamp(GetInt(configuration, 25, "PERF_DURATION_SEC", "Perf:DurationSec"), 5, 600),
            WarmupSec = Math.Clamp(GetInt(configuration, 5, "PERF_WARMUP_SEC", "Perf:WarmupSec"), 0, 120),
            RampSec = Math.Clamp(GetInt(configuration, 5, "PERF_RAMP_SEC", "Perf:RampSec"), 1, 120),
            EventsBatchSize = Math.Clamp(GetInt(configuration, 50, "PERF_EVENTS_BATCH", "Perf:EventsBatchSize"), 1, 2000),
            TranscriptBatchSize = Math.Clamp(GetInt(configuration, 40, "PERF_TRANSCRIPT_BATCH", "Perf:TranscriptBatchSize"), 1, 2000)
        };
    }

    private static IConfigurationRoot BuildConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath(AppContext.BaseDirectory)
            .AddJsonFile("appsettings.json", optional: true)
            .AddJsonFile("appsettings.local.json", optional: true)
            .AddEnvironmentVariables()
            .Build();
    }

    private static string? GetString(IConfiguration configuration, params string[] keys)
    {
        foreach (var key in keys)
        {
            var value = configuration[key];
            if (!string.IsNullOrWhiteSpace(value))
                return value;
        }

        return null;
    }

    private static int GetInt(IConfiguration configuration, int fallback, params string[] keys)
    {
        var value = GetString(configuration, keys);
        return int.TryParse(value, out var parsed) ? parsed : fallback;
    }
}

sealed class ApiAuthClient
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _http;

    public ApiAuthClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> AcquireTokenAsync(PerfSettings settings)
    {
        var email = settings.UserEmail;
        var password = settings.UserPassword;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            var suffix = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            email = $"perf.{suffix}@example.com";
            password = "PerfPass123!";
        }

        var registerPayload = JsonSerializer.Serialize(new
        {
            email,
            password,
            displayName = "Perf User"
        });

        using (var registerContent = new StringContent(registerPayload, Encoding.UTF8, "application/json"))
        {
            var register = await _http.PostAsync("auth/register", registerContent);
            if (register.StatusCode != HttpStatusCode.OK && register.StatusCode != HttpStatusCode.Conflict)
            {
                var body = await register.Content.ReadAsStringAsync();
                throw new InvalidOperationException($"Register failed: {(int)register.StatusCode} {body}");
            }
        }

        var loginPayload = JsonSerializer.Serialize(new { email, password });
        using var loginContent = new StringContent(loginPayload, Encoding.UTF8, "application/json");
        var login = await _http.PostAsync("auth/login", loginContent);

        if (login.StatusCode != HttpStatusCode.OK)
        {
            var body = await login.Content.ReadAsStringAsync();
            throw new InvalidOperationException($"Login failed: {(int)login.StatusCode} {body}");
        }

        var loginBody = await login.Content.ReadAsStringAsync();
        var auth = JsonSerializer.Deserialize<AuthResponse>(loginBody, JsonOptions)
                   ?? throw new InvalidOperationException("Unable to parse login response.");

        return auth.Token;
    }

    private sealed class AuthResponse
    {
        public string Token { get; set; } = string.Empty;
    }
}

sealed class PerfApiClient
{
    private readonly HttpClient _http;

    public PerfApiClient(string apiBaseUrl, string bearerToken)
    {
        _http = new HttpClient
        {
            BaseAddress = new Uri(apiBaseUrl),
            Timeout = TimeSpan.FromSeconds(30)
        };

        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
    }

    public async Task<Guid> CreateSessionAsync(string role, string language)
    {
        var payload = JsonSerializer.Serialize(new { role, language });
        using var content = new StringContent(payload, Encoding.UTF8, "application/json");
        using var response = await _http.PostAsync("sessions", content);
        var body = await response.Content.ReadAsStringAsync();

        if (response.StatusCode != HttpStatusCode.OK && response.StatusCode != HttpStatusCode.Created)
            throw new InvalidOperationException($"Create session failed: {(int)response.StatusCode} {body}");

        var id = ExtractGuid(body, "sessionId") ?? ExtractGuid(body, "id");
        return id ?? throw new InvalidOperationException("Session id not found in create session response.");
    }

    public Task<HttpResponseMessage> PostEventsBatchAsync(Guid sessionId, IReadOnlyList<MetricEventRequest> events)
    {
        var payload = JsonSerializer.Serialize(events);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        return _http.PostAsync($"sessions/{sessionId}/events/batch", content);
    }

    public Task<HttpResponseMessage> PostTranscriptBatchAsync(Guid sessionId, IReadOnlyList<TranscriptSegmentRequest> segments)
    {
        var payload = JsonSerializer.Serialize(segments);
        var content = new StringContent(payload, Encoding.UTF8, "application/json");
        return _http.PostAsync($"sessions/{sessionId}/transcript/batch", content);
    }

    public Task<HttpResponseMessage> FinalizeSessionAsync(Guid sessionId)
        => _http.PostAsync($"sessions/{sessionId}/finalize", content: null);

    public Task<HttpResponseMessage> GetReportAsync(Guid sessionId)
        => _http.GetAsync($"reports/{sessionId}");

    private static Guid? ExtractGuid(string json, string propertyName)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty(propertyName, out var value) || value.ValueKind != JsonValueKind.String)
            return null;

        return Guid.TryParse(value.GetString(), out var guid) ? guid : null;
    }
}

sealed class EventBatchGenerator
{
    private readonly int _batchSize;
    private readonly int _duplicateRatePercent;
    private readonly ConcurrentQueue<Guid> _history = new();
    private long _counter;

    public EventBatchGenerator(int batchSize, int duplicateRatePercent)
    {
        _batchSize = batchSize;
        _duplicateRatePercent = duplicateRatePercent;
    }

    public IReadOnlyList<MetricEventRequest> NextBatch()
    {
        var list = new List<MetricEventRequest>(_batchSize);

        for (var i = 0; i < _batchSize; i++)
        {
            var sequence = Interlocked.Increment(ref _counter);
            var hasExisting = _history.TryPeek(out var existingId);
            var shouldDuplicate = sequence % 100 < _duplicateRatePercent && hasExisting;
            var eventId = shouldDuplicate ? existingId : CreateDeterministicGuid(sequence);
            if (!shouldDuplicate)
            {
                _history.Enqueue(eventId);
                while (_history.Count > 500)
                    _history.TryDequeue(out _);
            }

            var source = sequence % 2 == 0 ? "Vision" : "Audio";
            var type = source == "Vision" ? "vision_metrics_v1" : "audio_metrics_v1";
            var tsMs = sequence * 500;

            object payload = source == "Vision"
                ? new
                {
                    eyeContact = NormalizeWave(sequence, 0.55, 0.95),
                    posture = NormalizeWave(sequence + 7, 0.50, 0.90),
                    fidget = NormalizeWave(sequence + 13, 0.05, 0.45),
                    headJitter = NormalizeWave(sequence + 19, 0.04, 0.35),
                    eyeOpenness = NormalizeWave(sequence + 23, 0.50, 0.90),
                    calibrated = true
                }
                : new
                {
                    wpm = 130 + (int)(sequence % 30),
                    filler = (int)(sequence % 3),
                    pauseMs = 450 + (int)((sequence * 17) % 900)
                };

            list.Add(new MetricEventRequest
            {
                ClientEventId = eventId,
                TsMs = tsMs,
                Source = source,
                Type = type,
                Payload = payload
            });
        }

        return list;
    }

    private static double NormalizeWave(long x, double min, double max)
    {
        var wave = (Math.Sin(x * 0.09) + 1.0) / 2.0;
        return Math.Round(min + ((max - min) * wave), 4);
    }

    private static Guid CreateDeterministicGuid(long sequence)
    {
        Span<byte> bytes = stackalloc byte[16];
        BitConverter.TryWriteBytes(bytes, sequence);
        BitConverter.TryWriteBytes(bytes[8..], sequence * 97);
        return new Guid(bytes);
    }
}

sealed class TranscriptBatchGenerator
{
    private readonly int _batchSize;
    private long _counter;

    public TranscriptBatchGenerator(int batchSize)
    {
        _batchSize = batchSize;
    }

    public IReadOnlyList<TranscriptSegmentRequest> NextBatch()
    {
        var list = new List<TranscriptSegmentRequest>(_batchSize);
        var batchIndex = Interlocked.Increment(ref _counter);

        for (var i = 0; i < _batchSize; i++)
        {
            var seq = (batchIndex * _batchSize) + i;
            var baseStart = seq * 900;
            var overlapOffset = i % 3 switch
            {
                0 => -120,
                1 => 0,
                _ => 180
            };

            var start = Math.Max(0, baseStart + overlapOffset);
            var end = start + 680;

            list.Add(new TranscriptSegmentRequest
            {
                ClientSegmentId = CreateDeterministicGuid(seq),
                StartMs = start,
                EndMs = end,
                Text = $"candidate response segment {batchIndex}_{i} continues with practical detail",
                Confidence = Math.Round(0.82 + ((i % 10) * 0.01), 2)
            });
        }

        return list;
    }

    private static Guid CreateDeterministicGuid(long sequence)
    {
        using var sha = SHA256.Create();
        var bytes = Encoding.UTF8.GetBytes($"transcript:{sequence}");
        var hash = sha.ComputeHash(bytes);
        return new Guid(hash.AsSpan(0, 16));
    }
}

sealed class MetricEventRequest
{
    public Guid ClientEventId { get; set; }
    public long TsMs { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public object Payload { get; set; } = new { };
}

sealed class TranscriptSegmentRequest
{
    public Guid ClientSegmentId { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public string Text { get; set; } = string.Empty;
    public double? Confidence { get; set; }
}
