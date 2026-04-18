using System.Text.Json;
using System.Net.Http.Json;
using FluentAssertions;
using InterviewCoach.Tests.Helpers;
using InterviewCoach.Tests.Infrastructure;

namespace InterviewCoach.Tests;

[Collection(IntegrationTestCollection.Name)]
public sealed class EvaluationDatasetIntegrationTests
{
    private readonly PostgresApiFixture _fixture;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    public EvaluationDatasetIntegrationTests(PostgresApiFixture fixture)
    {
        _fixture = fixture;
    }

    [SkippableFact]
    public async Task EvaluationDataset_RunAndValidate_AllCases()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.DockerUnavailableReason);

        var client = _fixture.GetClient();
        await ApiTestClient.LoginAndSetBearerAsync(client, "admin.regression@example.com", "AdminPass123!");

        var manifest = await LoadManifestAsync();
        manifest.Cases.Should().NotBeEmpty();

        var profiles = manifest.Cases
            .Select(c => LoadLabel(c.LabelFile).Profile.Trim().ToLowerInvariant())
            .Distinct()
            .ToHashSet();

        profiles.Should().Contain("general");
        profiles.Should().Contain("technical");
        profiles.Should().Contain("hr");

        var summary = new EvaluationSummary
        {
            Dataset = manifest.DatasetName,
            Version = manifest.Version
        };

        foreach (var testCase in manifest.Cases)
        {
            var label = LoadLabel(testCase.LabelFile);
            var caseResult = await EvaluateCaseAsync(client, testCase, label);
            summary.Cases.Add(caseResult);
        }

        summary.TotalCases = summary.Cases.Count;
        summary.Passed = summary.Cases.Count(x => x.Passed);
        summary.Failed = summary.TotalCases - summary.Passed;
        summary.AverageScoreMidpointError = summary.ScoreErrorSamples == 0
            ? 0
            : Math.Round(summary.TotalScoreMidpointError / summary.ScoreErrorSamples, 3);

        PrintSummary(summary);
        SaveSummary(summary);

        var failures = summary.Cases
            .Where(c => !c.Passed)
            .SelectMany(c => c.Failures.Select(f => $"{c.CaseId} ({c.Profile}): {f}"))
            .ToList();

        failures.Should().BeEmpty(string.Join(Environment.NewLine, failures));
    }

    [SkippableFact]
    public async Task EvaluationDataset_ProfileSpecificScoring_ChangesOutput()
    {
        Skip.IfNot(_fixture.DockerAvailable, _fixture.DockerUnavailableReason);

        var client = _fixture.GetClient();
        await ApiTestClient.LoginAndSetBearerAsync(client, "admin.regression@example.com", "AdminPass123!");

        var replayPath = DatasetPath("good_balanced_general_01.replay.json");
        var replayRaw = await File.ReadAllTextAsync(replayPath);
        using var replayDoc = JsonDocument.Parse(replayRaw);

        var sessionGeneral = await ImportReplayAsync(client, replayDoc.RootElement);
        var sessionTechnical = await ImportReplayAsync(client, replayDoc.RootElement);

        await SetProfileAsync(client, sessionGeneral, "general");
        await SetProfileAsync(client, sessionTechnical, "technical");

        await RunReplayAsync(client, sessionGeneral);
        await RunReplayAsync(client, sessionTechnical);

        var reportGeneral = await ApiTestClient.GetJsonAndReadAsync(client, $"/api/reports/{sessionGeneral}");
        var reportTechnical = await ApiTestClient.GetJsonAndReadAsync(client, $"/api/reports/{sessionTechnical}");

        var g = reportGeneral.GetProperty("scoreCard");
        var t = reportTechnical.GetProperty("scoreCard");

        var differs =
            g.GetProperty("overall").GetInt32() != t.GetProperty("overall").GetInt32() ||
            g.GetProperty("eyeContact").GetInt32() != t.GetProperty("eyeContact").GetInt32() ||
            g.GetProperty("posture").GetInt32() != t.GetProperty("posture").GetInt32() ||
            g.GetProperty("fidget").GetInt32() != t.GetProperty("fidget").GetInt32() ||
            g.GetProperty("speakingRate").GetInt32() != t.GetProperty("speakingRate").GetInt32() ||
            g.GetProperty("fillerWords").GetInt32() != t.GetProperty("fillerWords").GetInt32();

        differs.Should().BeTrue("same replay should produce different scorecard values for different profiles");
    }

    private async Task<CaseEvaluationResult> EvaluateCaseAsync(
        HttpClient client,
        EvaluationCaseEntry testCase,
        CaseLabel label)
    {
        var replayRaw = await File.ReadAllTextAsync(DatasetPath(testCase.ReplayFile));
        using var replayDoc = JsonDocument.Parse(replayRaw);

        var sessionId = await ImportReplayAsync(client, replayDoc.RootElement);
        await SetProfileAsync(client, sessionId, label.Profile);
        await RunReplayAsync(client, sessionId);

        var report = await ApiTestClient.GetJsonAndReadAsync(client, $"/api/reports/{sessionId}");
        var evidence = await ApiTestClient.GetJsonAndReadAsync(client, $"/api/sessions/{sessionId}/evidence-summary");

        var result = new CaseEvaluationResult
        {
            CaseId = label.CaseId,
            Profile = label.Profile,
            SessionId = sessionId
        };

        var scoreCard = report.GetProperty("scoreCard");
        ValidateScoreRange("overall", scoreCard.GetProperty("overall").GetInt32(), label.Expected.ScoreCard.Overall, result);
        ValidateScoreRange("eyeContact", scoreCard.GetProperty("eyeContact").GetInt32(), label.Expected.ScoreCard.EyeContact, result);
        ValidateScoreRange("posture", scoreCard.GetProperty("posture").GetInt32(), label.Expected.ScoreCard.Posture, result);
        ValidateScoreRange("fidget", scoreCard.GetProperty("fidget").GetInt32(), label.Expected.ScoreCard.Fidget, result);
        ValidateScoreRange("speakingRate", scoreCard.GetProperty("speakingRate").GetInt32(), label.Expected.ScoreCard.SpeakingRate, result);

        var actualPatterns = report.GetProperty("patterns")
            .EnumerateArray()
            .Select(x => x.GetProperty("type").GetString() ?? string.Empty)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var mustContain in label.Expected.Patterns.MustContain)
        {
            if (!actualPatterns.Contains(mustContain))
            {
                result.Failures.Add($"pattern mustContain missing: '{mustContain}'");
                result.ExpectedPatternMissed++;
            }
            else
            {
                result.ExpectedPatternFound++;
            }
        }

        foreach (var mustNotContain in label.Expected.Patterns.MustNotContain)
        {
            if (actualPatterns.Contains(mustNotContain))
            {
                result.Failures.Add($"pattern mustNotContain violated: '{mustNotContain}'");
                result.UnexpectedPatternTriggered++;
            }
        }

        var worstWindowsCount = evidence.GetProperty("worstWindows").GetArrayLength();
        if (worstWindowsCount < label.Expected.Evidence.MinWorstWindows)
        {
            result.Failures.Add(
                $"evidence constraint failed: worstWindows={worstWindowsCount} < minWorstWindows={label.Expected.Evidence.MinWorstWindows}");
        }

        result.Passed = result.Failures.Count == 0;
        return result;
    }

    private static void ValidateScoreRange(
        string field,
        int actual,
        RangeExpectation expected,
        CaseEvaluationResult result)
    {
        var within = actual >= expected.Min && actual <= expected.Max;
        if (!within)
        {
            result.Failures.Add($"score out of range: {field} actual={actual}, expected=[{expected.Min},{expected.Max}]");
        }

        var midpoint = (expected.Min + expected.Max) / 2.0;
        result.TotalMidpointError += Math.Abs(actual - midpoint);
        result.ScoreSamples++;
    }

    private static async Task<Guid> ImportReplayAsync(HttpClient client, JsonElement replayRoot)
    {
        using var response = await client.PostAsJsonAsync("/api/sessions/replay/import", replayRoot, JsonOptions);
        response.IsSuccessStatusCode.Should().BeTrue($"Replay import should succeed but was {(int)response.StatusCode}");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync());
        return body.RootElement.GetProperty("newSessionId").GetGuid();
    }

    private static async Task SetProfileAsync(HttpClient client, Guid sessionId, string profile)
    {
        using var response = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/scoring/profile", new
        {
            profileName = profile
        }, JsonOptions);

        response.IsSuccessStatusCode.Should().BeTrue(
            $"Setting profile '{profile}' should succeed but was {(int)response.StatusCode}");
    }

    private static async Task RunReplayAsync(HttpClient client, Guid sessionId)
    {
        using var response = await client.PostAsJsonAsync($"/api/sessions/{sessionId}/replay/run", new { speed = 1.0 }, JsonOptions);
        response.IsSuccessStatusCode.Should().BeTrue($"Replay run should succeed but was {(int)response.StatusCode}");
    }

    private static async Task<EvaluationManifest> LoadManifestAsync()
    {
        var raw = await File.ReadAllTextAsync(DatasetPath("dataset.manifest.json"));
        return JsonSerializer.Deserialize<EvaluationManifest>(raw, JsonOptions)
               ?? throw new InvalidOperationException("Failed to parse dataset manifest.");
    }

    private static CaseLabel LoadLabel(string labelFile)
    {
        var raw = File.ReadAllText(DatasetPath(labelFile));
        return JsonSerializer.Deserialize<CaseLabel>(raw, JsonOptions)
               ?? throw new InvalidOperationException($"Failed to parse label file: {labelFile}");
    }

    private static string DatasetPath(params string[] parts)
    {
        var root = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", "EvaluationDataset"));
        return Path.Combine(new[] { root }.Concat(parts).ToArray());
    }

    private static string ResolveArtifactsEvalDirectory()
    {
        var current = new DirectoryInfo(Directory.GetCurrentDirectory());
        while (current != null)
        {
            if (Directory.Exists(Path.Combine(current.FullName, ".git")))
            {
                var path = Path.Combine(current.FullName, "artifacts", "eval");
                Directory.CreateDirectory(path);
                return path;
            }

            current = current.Parent;
        }

        var fallback = Path.Combine(Directory.GetCurrentDirectory(), "artifacts", "eval");
        Directory.CreateDirectory(fallback);
        return fallback;
    }

    private static void SaveSummary(EvaluationSummary summary)
    {
        summary.TotalScoreMidpointError = summary.Cases.Sum(c => c.TotalMidpointError);
        summary.ScoreErrorSamples = summary.Cases.Sum(c => c.ScoreSamples);
        summary.ExpectedPatternsFound = summary.Cases.Sum(c => c.ExpectedPatternFound);
        summary.ExpectedPatternsMissed = summary.Cases.Sum(c => c.ExpectedPatternMissed);
        summary.UnexpectedPatternsTriggered = summary.Cases.Sum(c => c.UnexpectedPatternTriggered);

        var path = Path.Combine(
            ResolveArtifactsEvalDirectory(),
            $"evaluation-summary-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");

        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions
        {
            WriteIndented = true
        });

        File.WriteAllText(path, json);
        Console.WriteLine($"[eval] summary json: {path}");
    }

    private static void PrintSummary(EvaluationSummary summary)
    {
        summary.TotalScoreMidpointError = summary.Cases.Sum(c => c.TotalMidpointError);
        summary.ScoreErrorSamples = summary.Cases.Sum(c => c.ScoreSamples);
        summary.ExpectedPatternsFound = summary.Cases.Sum(c => c.ExpectedPatternFound);
        summary.ExpectedPatternsMissed = summary.Cases.Sum(c => c.ExpectedPatternMissed);
        summary.UnexpectedPatternsTriggered = summary.Cases.Sum(c => c.UnexpectedPatternTriggered);
        summary.AverageScoreMidpointError = summary.ScoreErrorSamples == 0
            ? 0
            : Math.Round(summary.TotalScoreMidpointError / summary.ScoreErrorSamples, 3);

        Console.WriteLine();
        Console.WriteLine("=== EVALUATION SUMMARY ===");
        Console.WriteLine($"dataset={summary.Dataset} v{summary.Version}");
        Console.WriteLine($"totalCases={summary.TotalCases} passed={summary.Passed} failed={summary.Failed}");
        Console.WriteLine($"avgScoreMidpointError={summary.AverageScoreMidpointError:0.###}");
        Console.WriteLine($"expectedPatternsFound={summary.ExpectedPatternsFound}");
        Console.WriteLine($"expectedPatternsMissed={summary.ExpectedPatternsMissed}");
        Console.WriteLine($"unexpectedPatternsTriggered={summary.UnexpectedPatternsTriggered}");
        Console.WriteLine("==========================");
        Console.WriteLine();
    }

    private sealed class EvaluationManifest
    {
        public string DatasetName { get; set; } = string.Empty;
        public int Version { get; set; }
        public List<EvaluationCaseEntry> Cases { get; set; } = [];
    }

    private sealed class EvaluationCaseEntry
    {
        public string CaseId { get; set; } = string.Empty;
        public string ReplayFile { get; set; } = string.Empty;
        public string LabelFile { get; set; } = string.Empty;
    }

    private sealed class CaseLabel
    {
        public string CaseId { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
        public ExpectedSection Expected { get; set; } = new();
    }

    private sealed class ExpectedSection
    {
        public ScoreCardExpectation ScoreCard { get; set; } = new();
        public PatternExpectation Patterns { get; set; } = new();
        public EvidenceExpectation Evidence { get; set; } = new();
    }

    private sealed class ScoreCardExpectation
    {
        public RangeExpectation Overall { get; set; } = new();
        public RangeExpectation EyeContact { get; set; } = new();
        public RangeExpectation Posture { get; set; } = new();
        public RangeExpectation Fidget { get; set; } = new();
        public RangeExpectation SpeakingRate { get; set; } = new();
    }

    private sealed class PatternExpectation
    {
        public List<string> MustContain { get; set; } = [];
        public List<string> MustNotContain { get; set; } = [];
    }

    private sealed class EvidenceExpectation
    {
        public int MinWorstWindows { get; set; }
    }

    private sealed class RangeExpectation
    {
        public int Min { get; set; }
        public int Max { get; set; }
    }

    private sealed class CaseEvaluationResult
    {
        public string CaseId { get; set; } = string.Empty;
        public string Profile { get; set; } = string.Empty;
        public Guid SessionId { get; set; }
        public bool Passed { get; set; }
        public List<string> Failures { get; set; } = [];
        public double TotalMidpointError { get; set; }
        public int ScoreSamples { get; set; }
        public int ExpectedPatternFound { get; set; }
        public int ExpectedPatternMissed { get; set; }
        public int UnexpectedPatternTriggered { get; set; }
    }

    private sealed class EvaluationSummary
    {
        public string Dataset { get; set; } = string.Empty;
        public int Version { get; set; }
        public int TotalCases { get; set; }
        public int Passed { get; set; }
        public int Failed { get; set; }
        public double TotalScoreMidpointError { get; set; }
        public int ScoreErrorSamples { get; set; }
        public double AverageScoreMidpointError { get; set; }
        public int ExpectedPatternsFound { get; set; }
        public int ExpectedPatternsMissed { get; set; }
        public int UnexpectedPatternsTriggered { get; set; }
        public List<CaseEvaluationResult> Cases { get; set; } = [];
    }
}
