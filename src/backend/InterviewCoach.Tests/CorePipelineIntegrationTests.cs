using FluentAssertions;
using InterviewCoach.Tests.Helpers;
using InterviewCoach.Tests.Infrastructure;

namespace InterviewCoach.Tests;

[Collection(IntegrationTestCollection.Name)]
public sealed class CorePipelineIntegrationTests
{
    private readonly PostgresApiFixture _fixture;

    public CorePipelineIntegrationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task CreateSession_Works()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.DockerUnavailableReason);

        var client = _fixture.GetClient();
        await AuthenticateAsUserAsync(client, "core.create");

        var body = new
        {
            role = "Software Engineer",
            language = "en"
        };

        var session = await ApiTestClient.PostJsonAndReadAsync(client, "/api/sessions", body);

        session.TryGetProperty("sessionId", out var idElement).Should().BeTrue();
        idElement.GetGuid().Should().NotBeEmpty();
    }

    [SkippableFact]
    public async Task EventsBatch_Idempotent()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.DockerUnavailableReason);

        var client = _fixture.GetClient();
        await AuthenticateAsUserAsync(client, "core.events");
        var sessionId = await CreateSessionAsync(client);

        var id1 = ApiTestClient.CreateDeterministicGuid(101);
        var id2 = ApiTestClient.CreateDeterministicGuid(102);

        var payload = new[]
        {
            new
            {
                clientEventId = id1,
                tsMs = 0L,
                source = "Vision",
                type = "combined",
                payload = new { eyeContact = 80, posture = 82, fidget = 20 }
            },
            new
            {
                clientEventId = id1,
                tsMs = 500L,
                source = "Vision",
                type = "combined",
                payload = new { eyeContact = 79, posture = 81, fidget = 22 }
            },
            new
            {
                clientEventId = id2,
                tsMs = 1000L,
                source = "Vision",
                type = "combined",
                payload = new { eyeContact = 78, posture = 80, fidget = 21 }
            }
        };

        var first = await ApiTestClient.PostJsonAndReadAsync(client, $"/api/sessions/{sessionId}/events/batch", payload);
        first.GetProperty("inserted").GetInt32().Should().Be(2);
        first.GetProperty("ignoredDuplicates").GetInt32().Should().Be(1);

        var second = await ApiTestClient.PostJsonAndReadAsync(client, $"/api/sessions/{sessionId}/events/batch", payload);
        second.GetProperty("inserted").GetInt32().Should().Be(0);
        second.GetProperty("ignoredDuplicates").GetInt32().Should().Be(3);
    }

    [SkippableFact]
    public async Task TranscriptBatch_MergesOverlaps()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.DockerUnavailableReason);

        var client = _fixture.GetClient();
        await AuthenticateAsUserAsync(client, "core.transcript");
        var sessionId = await CreateSessionAsync(client);

        var segments = new[]
        {
            new
            {
                clientSegmentId = ApiTestClient.CreateDeterministicGuid(201),
                startMs = 0L,
                endMs = 1000L,
                text = "Hello",
                confidence = 0.9
            },
            new
            {
                clientSegmentId = ApiTestClient.CreateDeterministicGuid(202),
                startMs = 900L,
                endMs = 1700L,
                text = "world",
                confidence = 0.8
            },
            new
            {
                clientSegmentId = ApiTestClient.CreateDeterministicGuid(203),
                startMs = 1800L,
                endMs = 2300L,
                text = "again",
                confidence = 0.85
            }
        };

        var ingest = await ApiTestClient.PostJsonAndReadAsync(client, $"/api/sessions/{sessionId}/transcript/batch", segments);
        ingest.GetProperty("mergedOutputCount").GetInt32().Should().BeLessThan(segments.Length);

        var report = await ApiTestClient.GetJsonAndReadAsync(client, $"/api/reports/{sessionId}");
        var transcript = report.GetProperty("transcript");

        transcript.GetArrayLength().Should().BeGreaterThan(0);

        long lastStart = -1;
        foreach (var segment in transcript.EnumerateArray())
        {
            var start = segment.GetProperty("startMs").GetInt64();
            start.Should().BeGreaterThanOrEqualTo(lastStart);
            lastStart = start;
        }

        var mergedText = string.Join(" ", transcript.EnumerateArray().Select(s => s.GetProperty("text").GetString()));
        mergedText.Should().Contain("Hello");
        mergedText.Should().Contain("world");
    }

    [SkippableFact]
    public async Task Finalize_ProducesScorecardAndDerived()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.DockerUnavailableReason);

        var client = _fixture.GetClient();
        await AuthenticateAsUserAsync(client, "core.finalize");
        var sessionId = await CreateSessionAsync(client);

        var events = new List<object>();

        var tick = 0;
        for (var ts = 0; ts < 20_000; ts += 500)
        {
            events.Add(new
            {
                clientEventId = ApiTestClient.CreateDeterministicGuid(1000 + tick),
                tsMs = (long)ts,
                source = "Vision",
                type = "combined",
                payload = new { eyeContact = 82, posture = 80, fidget = 25 }
            });
            tick++;
        }

        for (var i = 0; i < 8; i++)
        {
            events.Add(new
            {
                clientEventId = ApiTestClient.CreateDeterministicGuid(2000 + i),
                tsMs = (long)(i * 2500),
                source = "Audio",
                type = "audio_metrics",
                payload = new { wpm = 138, filler = 1, pauseMs = 250 }
            });
        }

        var eventsResult = await ApiTestClient.PostJsonAndReadAsync(client, $"/api/sessions/{sessionId}/events/batch", events);
        eventsResult.GetProperty("inserted").GetInt32().Should().Be(events.Count);

        var finalize = await ApiTestClient.PostJsonAndReadAsync(client, $"/api/sessions/{sessionId}/finalize", new { });
        finalize.TryGetProperty("scoreCard", out var scoreCard).Should().BeTrue();
        scoreCard.ValueKind.Should().NotBe(System.Text.Json.JsonValueKind.Null);

        finalize.TryGetProperty("patterns", out var patternsFromFinalize).Should().BeTrue();
        patternsFromFinalize.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);

        var report = await ApiTestClient.GetJsonAndReadAsync(client, $"/api/reports/{sessionId}");
        var derivedSeries = report.GetProperty("derivedSeries");
        derivedSeries.TryGetProperty("eyeContact", out var eyeContactSeries).Should().BeTrue();
        derivedSeries.TryGetProperty("wpm", out var wpmSeries).Should().BeTrue();
        eyeContactSeries.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
        wpmSeries.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);

        report.TryGetProperty("patterns", out var patterns).Should().BeTrue();
        patterns.ValueKind.Should().Be(System.Text.Json.JsonValueKind.Array);
    }

    private static async Task<Guid> CreateSessionAsync(HttpClient client)
    {
        var session = await ApiTestClient.PostJsonAndReadAsync(client, "/api/sessions", new
        {
            role = "Software Engineer",
            language = "en"
        });

        return session.GetProperty("sessionId").GetGuid();
    }

    private static Task AuthenticateAsUserAsync(HttpClient client, string tag)
    {
        var email = $"integration.{tag}@example.com";
        const string password = "TestPass123!";
        return ApiTestClient.LoginAndSetBearerAsync(client, email, password, "Integration User");
    }
}
