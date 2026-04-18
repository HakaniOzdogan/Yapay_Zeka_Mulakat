using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using InterviewCoach.Tests.Helpers;
using InterviewCoach.Tests.Infrastructure;

namespace InterviewCoach.Tests;

[Collection(IntegrationTestCollection.Name)]
public sealed class GoldenSessionRegressionTests
{
    private readonly PostgresApiFixture _fixture;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public GoldenSessionRegressionTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task GoldenSession_Finalize_Report_Evidence_AreStable()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.DockerUnavailableReason);

        var client = _fixture.GetClient();
        await LoginAsSeededAdminAsync(client);

        var fixtureJson = await File.ReadAllTextAsync(GetFixturePath("session-replay-sample.json"));
        using var fixtureDoc = JsonDocument.Parse(fixtureJson);

        var importResponse = await client.PostAsJsonAsync("/api/sessions/replay/import", fixtureDoc.RootElement, JsonOptions);
        importResponse.IsSuccessStatusCode.Should().BeTrue($"Replay import should succeed but was {(int)importResponse.StatusCode}.");

        var importBody = await JsonDocument.ParseAsync(await importResponse.Content.ReadAsStreamAsync());
        var sessionId = importBody.RootElement.GetProperty("newSessionId").GetGuid();
        sessionId.Should().NotBeEmpty();

        var runResponse = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/replay/run", new { speed = 1.0 }, JsonOptions);
        runResponse.IsSuccessStatusCode.Should().BeTrue($"Replay run should succeed but was {(int)runResponse.StatusCode}.");

        var reportJson = await client.GetStringAsync($"/api/reports/{sessionId}");
        var evidenceJson = await client.GetStringAsync($"/api/sessions/{sessionId}/evidence-summary");

        using var reportDoc = JsonDocument.Parse(reportJson);
        using var evidenceDoc = JsonDocument.Parse(evidenceJson);

        AssertReportAndEvidenceShape(reportDoc.RootElement, evidenceDoc.RootElement);

        var updateSnapshots = IsEnabled("UPDATE_GOLDEN_SNAPSHOTS");
        var reportSnapshotPath = GetFixturePath("Expected", "report.snapshot.json");
        var evidenceSnapshotPath = GetFixturePath("Expected", "evidence-summary.snapshot.json");

        if (updateSnapshots)
        {
            Console.WriteLine("WARNING: UPDATE_GOLDEN_SNAPSHOTS=true, writing normalized outputs to golden snapshots.");
            await File.WriteAllTextAsync(reportSnapshotPath, JsonSnapshotComparer.NormalizeAndFormat(reportJson));
            await File.WriteAllTextAsync(evidenceSnapshotPath, JsonSnapshotComparer.NormalizeAndFormat(evidenceJson));
            return;
        }

        var expectedReport = await File.ReadAllTextAsync(reportSnapshotPath);
        var expectedEvidence = await File.ReadAllTextAsync(evidenceSnapshotPath);

        JsonSnapshotComparer.AssertMatches(expectedReport, reportJson);
        JsonSnapshotComparer.AssertMatches(expectedEvidence, evidenceJson);
    }

    [SkippableFact]
    public async Task GoldenSession_LlmCoach_SchemaOnly_WhenEnabled()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.DockerUnavailableReason);
        Skip.IfNot(IsEnabled("RUN_LLM_REGRESSION"), "RUN_LLM_REGRESSION is not enabled.");

        var client = _fixture.GetClient();
        await LoginAsSeededAdminAsync(client);

        var fixtureJson = await File.ReadAllTextAsync(GetFixturePath("session-replay-sample.json"));
        using var fixtureDoc = JsonDocument.Parse(fixtureJson);
        var importResponse = await client.PostAsJsonAsync("/api/sessions/replay/import", fixtureDoc.RootElement, JsonOptions);
        importResponse.IsSuccessStatusCode.Should().BeTrue($"Replay import should succeed but was {(int)importResponse.StatusCode}.");

        var importBody = await JsonDocument.ParseAsync(await importResponse.Content.ReadAsStreamAsync());
        var sessionId = importBody.RootElement.GetProperty("newSessionId").GetGuid();

        var runResponse = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/replay/run", new { speed = 1.0 }, JsonOptions);
        runResponse.IsSuccessStatusCode.Should().BeTrue($"Replay run should succeed but was {(int)runResponse.StatusCode}.");

        HttpResponseMessage llmResponse;
        try
        {
            llmResponse = await client.PostAsync(
                $"/api/sessions/{sessionId}/llm/coach?force=true",
                new StringContent("{}", Encoding.UTF8, "application/json"));
        }
        catch (Exception ex)
        {
            Skip.If(true, $"LLM endpoint unreachable: {ex.Message}");
            return;
        }

        if (llmResponse.StatusCode is HttpStatusCode.BadGateway or HttpStatusCode.ServiceUnavailable or HttpStatusCode.NotFound)
        {
            Skip.If(true, $"LLM service unavailable: {(int)llmResponse.StatusCode}");
            return;
        }

        llmResponse.IsSuccessStatusCode.Should().BeTrue($"LLM coach call should succeed but was {(int)llmResponse.StatusCode}.");

        var payload = await llmResponse.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(payload);
        ValidateLlmSchema(doc.RootElement);
    }

    private static void ValidateLlmSchema(JsonElement root)
    {
        root.TryGetProperty("rubric", out var rubric).Should().BeTrue();
        ValidateIntRange(rubric, "technical_correctness", 0, 5);
        ValidateIntRange(rubric, "depth", 0, 5);
        ValidateIntRange(rubric, "structure", 0, 5);
        ValidateIntRange(rubric, "clarity", 0, 5);
        ValidateIntRange(rubric, "confidence", 0, 5);

        root.TryGetProperty("overall", out var overall).Should().BeTrue();
        overall.GetInt32().Should().BeInRange(0, 100);

        root.TryGetProperty("feedback", out var feedback).Should().BeTrue();
        feedback.ValueKind.Should().Be(JsonValueKind.Array);
        feedback.GetArrayLength().Should().BeGreaterThan(0);

        foreach (var item in feedback.EnumerateArray())
        {
            item.TryGetProperty("category", out _).Should().BeTrue();
            item.TryGetProperty("title", out _).Should().BeTrue();
            item.TryGetProperty("evidence", out _).Should().BeTrue();
            item.TryGetProperty("suggestion", out _).Should().BeTrue();
            item.TryGetProperty("example_phrase", out _).Should().BeTrue();

            ValidateIntRange(item, "severity", 1, 5);

            item.TryGetProperty("time_range_ms", out var range).Should().BeTrue();
            range.ValueKind.Should().Be(JsonValueKind.Array);
            range.GetArrayLength().Should().Be(2);
            var timeRange = range.EnumerateArray().ToArray();
            var start = timeRange[0].GetInt64();
            var end = timeRange[1].GetInt64();
            start.Should().BeLessThanOrEqualTo(end);
        }

        root.TryGetProperty("drills", out var drills).Should().BeTrue();
        drills.ValueKind.Should().Be(JsonValueKind.Array);
        foreach (var drill in drills.EnumerateArray())
        {
            drill.TryGetProperty("title", out _).Should().BeTrue();
            drill.TryGetProperty("steps", out var steps).Should().BeTrue();
            steps.ValueKind.Should().Be(JsonValueKind.Array);
            drill.TryGetProperty("duration_min", out _).Should().BeTrue();
        }
    }

    private static void AssertReportAndEvidenceShape(JsonElement report, JsonElement evidence)
    {
        report.TryGetProperty("scoreCard", out var scoreCardElement).Should().BeTrue();
        scoreCardElement.ValueKind.Should().NotBe(JsonValueKind.Null);

        report.TryGetProperty("derivedSeries", out var derivedSeries).Should().BeTrue();
        foreach (var key in new[] { "eyeContact", "posture", "fidget", "headJitter", "wpm", "filler", "pauseMs" })
        {
            derivedSeries.TryGetProperty(key, out var series).Should().BeTrue($"Derived series should contain '{key}'.");
            series.ValueKind.Should().Be(JsonValueKind.Array);
        }

        report.TryGetProperty("patterns", out var patterns).Should().BeTrue();
        patterns.ValueKind.Should().Be(JsonValueKind.Array);
        foreach (var pattern in patterns.EnumerateArray())
        {
            pattern.TryGetProperty("type", out _).Should().BeTrue();
            pattern.TryGetProperty("startMs", out _).Should().BeTrue();
            pattern.TryGetProperty("endMs", out _).Should().BeTrue();
            pattern.TryGetProperty("severity", out _).Should().BeTrue();
        }

        evidence.TryGetProperty("highLevel", out _).Should().BeTrue();
        evidence.TryGetProperty("signals", out _).Should().BeTrue();
        evidence.TryGetProperty("worstWindows", out var worstWindows).Should().BeTrue();
        worstWindows.ValueKind.Should().Be(JsonValueKind.Array);
    }

    private static void ValidateIntRange(JsonElement element, string property, int min, int max)
    {
        element.TryGetProperty(property, out var value).Should().BeTrue();
        value.ValueKind.Should().Be(JsonValueKind.Number);
        value.GetInt32().Should().BeInRange(min, max);
    }

    private static Task LoginAsSeededAdminAsync(HttpClient client)
        => ApiTestClient.LoginAndSetBearerAsync(client, "admin.regression@example.com", "AdminPass123!");

    private static bool IsEnabled(string key)
        => string.Equals(Environment.GetEnvironmentVariable(key), "true", StringComparison.OrdinalIgnoreCase);

    private static string GetFixturePath(params string[] parts)
    {
        var basePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", "GoldenSessions"));
        return Path.Combine(new[] { basePath }.Concat(parts).ToArray());
    }
}
