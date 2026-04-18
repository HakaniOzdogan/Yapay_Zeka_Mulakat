using System.Diagnostics;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using InterviewCoach.Api.Services;

namespace InterviewCoach.Tests;

public sealed class LlmPromptAbEvaluationTests
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    [SkippableFact]
    public async Task LlmPromptAbEvaluation_RunOnEvidenceFixtures_AndWriteSummary()
    {
        Skip.IfNot(IsEnabled("RUN_LLM_PROMPT_EVAL"), "RUN_LLM_PROMPT_EVAL is not enabled.");

        var settings = LlmEvalSettings.LoadFromEnvironment();
        using var http = new HttpClient
        {
            BaseAddress = new Uri(settings.BaseUrl),
            Timeout = TimeSpan.FromSeconds(settings.TimeoutSeconds)
        };

        var ollamaAvailable = await IsOllamaAvailableAsync(http);
        Skip.IfNot(ollamaAvailable, $"Ollama unavailable at {settings.BaseUrl}");

        var manifest = await LoadManifestAsync();
        manifest.Cases.Should().NotBeEmpty();

        var validator = new LlmCoachingService(new NoopOllamaClient());

        var summary = new LlmAbSummary
        {
            GeneratedAtUtc = DateTime.UtcNow,
            BaseUrl = settings.BaseUrl,
            Model = settings.Model,
            TotalCases = manifest.Cases.Count
        };

        foreach (var testCase in manifest.Cases)
        {
            var evidence = await LoadEvidenceAsync(testCase.EvidenceFile);

            var variantA = await EvaluateVariantAsync(http, validator, evidence, testCase, settings.Model, "coach_eval_A", BuildSystemPromptA, BuildUserPromptA);
            var variantB = await EvaluateVariantAsync(http, validator, evidence, testCase, settings.Model, "coach_eval_B", BuildSystemPromptB, BuildUserPromptB);

            var winner = DetermineWinner(variantA.OverallHeuristicScore, variantB.OverallHeuristicScore);
            var notes = BuildNotes(variantA, variantB);

            summary.Cases.Add(new LlmAbCaseResult
            {
                CaseId = testCase.CaseId,
                ExpectedFocus = testCase.ExpectedFocus,
                VariantA = variantA,
                VariantB = variantB,
                Winner = winner,
                Notes = notes
            });
        }

        ComputeAggregate(summary);
        PrintSummary(summary);
        var path = SaveSummary(summary);

        path.Should().NotBeNullOrWhiteSpace();
        summary.Cases.Should().HaveCount(manifest.Cases.Count);
    }

    private static async Task<VariantScore> EvaluateVariantAsync(
        HttpClient http,
        LlmCoachingService validator,
        EvidenceSummaryDto evidence,
        LlmEvalCase testCase,
        string model,
        string variantName,
        Func<string, string> systemPromptFactory,
        Func<EvidenceSummaryDto, IReadOnlyList<string>, string> userPromptFactory)
    {
        var systemPrompt = systemPromptFactory(evidence.Language);
        var userPrompt = userPromptFactory(evidence, testCase.ExpectedFocus);

        var sw = Stopwatch.StartNew();
        string rawOutput;
        try
        {
            rawOutput = await ChatWithOllamaAsync(http, model, systemPrompt, userPrompt);
        }
        catch (Exception ex)
        {
            return new VariantScore
            {
                Variant = variantName,
                SchemaValid = false,
                Errors = [$"Ollama request failed: {ex.Message}"],
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                OverallHeuristicScore = 0
            };
        }
        sw.Stop();

        var schemaValid = validator.TryParseAndValidate(rawOutput, out var parsed, out var parseErrors)
                          && HasOnlyExpectedTopLevelKeys(rawOutput);

        if (!schemaValid || parsed == null)
        {
            return new VariantScore
            {
                Variant = variantName,
                SchemaValid = false,
                Errors = parseErrors,
                LatencyMs = sw.Elapsed.TotalMilliseconds,
                SafetyFormatScore = ComputeSafetyFormatScore(rawOutput, false),
                OverallHeuristicScore = 0
            };
        }

        var feedbackCountScore = ComputeFeedbackCountScore(parsed.Feedback.Count);
        var groundingScore = ComputeGroundingScore(parsed.Feedback);
        var actionabilityScore = ComputeActionabilityScore(parsed.Feedback);
        var diversityScore = ComputeDiversityScore(parsed.Feedback);
        var drillQualityScore = ComputeDrillQualityScore(parsed.Drills);
        var safetyScore = ComputeSafetyFormatScore(rawOutput, true);

        var weighted = (feedbackCountScore * 0.10)
                     + (groundingScore * 0.25)
                     + (actionabilityScore * 0.20)
                     + (diversityScore * 0.15)
                     + (drillQualityScore * 0.20)
                     + (safetyScore * 0.10);

        return new VariantScore
        {
            Variant = variantName,
            SchemaValid = true,
            Errors = [],
            LatencyMs = sw.Elapsed.TotalMilliseconds,
            FeedbackCount = parsed.Feedback.Count,
            DrillCount = parsed.Drills.Count,
            FeedbackCountScore = feedbackCountScore,
            EvidenceGroundingScore = groundingScore,
            ActionabilityScore = actionabilityScore,
            DiversityScore = diversityScore,
            DrillQualityScore = drillQualityScore,
            SafetyFormatScore = safetyScore,
            OverallHeuristicScore = Math.Round(weighted, 2)
        };
    }

    private static async Task<string> ChatWithOllamaAsync(HttpClient http, string model, string systemPrompt, string userPrompt)
    {
        var payload = new
        {
            model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            stream = false,
            format = "json"
        };

        using var response = await http.PostAsJsonAsync("/api/chat", payload, JsonOptions);
        var text = await response.Content.ReadAsStringAsync();
        response.IsSuccessStatusCode.Should().BeTrue($"Ollama /api/chat failed: {(int)response.StatusCode} {text}");

        using var doc = JsonDocument.Parse(text);

        if (doc.RootElement.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out var contentElement) &&
            contentElement.ValueKind == JsonValueKind.String)
        {
            return contentElement.GetString() ?? string.Empty;
        }

        if (doc.RootElement.TryGetProperty("response", out var responseElement) &&
            responseElement.ValueKind == JsonValueKind.String)
        {
            return responseElement.GetString() ?? string.Empty;
        }

        throw new InvalidOperationException("Ollama response does not contain message content.");
    }

    private static async Task<bool> IsOllamaAvailableAsync(HttpClient http)
    {
        try
        {
            using var response = await http.GetAsync("/api/tags");
            return response.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<LlmEvalManifest> LoadManifestAsync()
    {
        var raw = await File.ReadAllTextAsync(FixturePath("manifest.json"));
        return JsonSerializer.Deserialize<LlmEvalManifest>(raw, JsonOptions)
               ?? throw new InvalidOperationException("Failed to parse LlmPromptEval manifest.");
    }

    private static async Task<EvidenceSummaryDto> LoadEvidenceAsync(string evidenceFile)
    {
        var raw = await File.ReadAllTextAsync(FixturePath(evidenceFile));
        return JsonSerializer.Deserialize<EvidenceSummaryDto>(raw, JsonOptions)
               ?? throw new InvalidOperationException($"Failed to parse evidence fixture: {evidenceFile}");
    }

    private static string FixturePath(params string[] parts)
    {
        var basePath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "Fixtures", "LlmPromptEval"));
        return Path.Combine(new[] { basePath }.Concat(parts).ToArray());
    }

    private static bool HasOnlyExpectedTopLevelKeys(string raw)
    {
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;

            var keys = doc.RootElement.EnumerateObject().Select(p => p.Name).ToHashSet(StringComparer.Ordinal);
            var expected = new HashSet<string>(["rubric", "overall", "feedback", "drills"], StringComparer.Ordinal);
            return keys.SetEquals(expected);
        }
        catch
        {
            return false;
        }
    }

    private static double ComputeFeedbackCountScore(int count)
    {
        if (count is >= 5 and <= 10)
            return 100;

        var diff = Math.Abs(count - 7);
        return Clamp100(100 - (diff * 15));
    }

    private static double ComputeGroundingScore(IReadOnlyList<LlmFeedbackItem> feedback)
    {
        if (feedback.Count == 0)
            return 0;

        var evidenceOk = feedback.Count(x => !string.IsNullOrWhiteSpace(x.Evidence));
        var timeOk = feedback.Count(x => x.TimeRangeMs is { Length: 2 } && x.TimeRangeMs[0] <= x.TimeRangeMs[1]);
        var categoryOk = feedback.Count(x => x.Category is "vision" or "audio" or "content" or "structure");

        var pEvidence = evidenceOk / (double)feedback.Count;
        var pTime = timeOk / (double)feedback.Count;
        var pCategory = categoryOk / (double)feedback.Count;

        return Math.Round(((pEvidence + pTime + pCategory) / 3.0) * 100.0, 2);
    }

    private static double ComputeActionabilityScore(IReadOnlyList<LlmFeedbackItem> feedback)
    {
        if (feedback.Count == 0)
            return 0;

        var suggestionOk = feedback.Count(x => !string.IsNullOrWhiteSpace(x.Suggestion));
        var exampleOk = feedback.Count(x => !string.IsNullOrWhiteSpace(x.ExamplePhrase));
        var lengthOk = feedback.Count(x =>
        {
            var len = (x.Suggestion ?? string.Empty).Trim().Length;
            return len is >= 20 and <= 300;
        });

        var pSuggestion = suggestionOk / (double)feedback.Count;
        var pExample = exampleOk / (double)feedback.Count;
        var pLen = lengthOk / (double)feedback.Count;

        return Math.Round(((pSuggestion + pExample + pLen) / 3.0) * 100.0, 2);
    }

    private static double ComputeDiversityScore(IReadOnlyList<LlmFeedbackItem> feedback)
    {
        if (feedback.Count == 0)
            return 0;

        var categories = feedback
            .Select(x => (x.Category ?? string.Empty).Trim().ToLowerInvariant())
            .Where(x => x is "vision" or "audio" or "content" or "structure")
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var uniqueTitles = feedback
            .Select(x => (x.Title ?? string.Empty).Trim())
            .Where(x => !string.IsNullOrWhiteSpace(x))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();

        var duplicatePenalty = feedback.Count == 0 ? 0 : (1.0 - (uniqueTitles / (double)feedback.Count)) * 25.0;
        var categoryScore = (categories / 4.0) * 100.0;
        return Math.Round(Clamp100(categoryScore - duplicatePenalty), 2);
    }

    private static double ComputeDrillQualityScore(IReadOnlyList<LlmDrill> drills)
    {
        if (drills.Count == 0)
            return 0;

        var valid = drills.Count(d =>
            !string.IsNullOrWhiteSpace(d.Title)
            && d.Steps is { Count: > 0 }
            && d.Steps.All(s => !string.IsNullOrWhiteSpace(s))
            && d.DurationMin > 0);

        return Math.Round((valid / (double)drills.Count) * 100.0, 2);
    }

    private static double ComputeSafetyFormatScore(string rawOutput, bool strictValid)
    {
        var score = 100.0;
        if (rawOutput.Contains("```", StringComparison.Ordinal))
            score -= 50;
        if (!strictValid)
            score -= 50;
        return Clamp100(score);
    }

    private static double Clamp100(double value) => Math.Max(0, Math.Min(100, value));

    private static string DetermineWinner(double scoreA, double scoreB)
    {
        if (Math.Abs(scoreA - scoreB) < 1.0)
            return "tie";
        return scoreA > scoreB ? "A" : "B";
    }

    private static string BuildNotes(VariantScore a, VariantScore b)
    {
        var notes = new List<string>();
        if (a.EvidenceGroundingScore > b.EvidenceGroundingScore + 0.5) notes.Add("A better grounding");
        if (b.EvidenceGroundingScore > a.EvidenceGroundingScore + 0.5) notes.Add("B better grounding");
        if (a.DiversityScore > b.DiversityScore + 0.5) notes.Add("A higher diversity");
        if (b.DiversityScore > a.DiversityScore + 0.5) notes.Add("B higher diversity");
        if (a.ActionabilityScore > b.ActionabilityScore + 0.5) notes.Add("A more actionable");
        if (b.ActionabilityScore > a.ActionabilityScore + 0.5) notes.Add("B more actionable");
        return notes.Count == 0 ? "No strong heuristic difference" : string.Join("; ", notes);
    }

    private static void ComputeAggregate(LlmAbSummary summary)
    {
        summary.WinsA = summary.Cases.Count(x => x.Winner == "A");
        summary.WinsB = summary.Cases.Count(x => x.Winner == "B");
        summary.Ties = summary.Cases.Count(x => x.Winner == "tie");

        summary.AverageScoreA = Math.Round(summary.Cases.Average(x => x.VariantA.OverallHeuristicScore), 2);
        summary.AverageScoreB = Math.Round(summary.Cases.Average(x => x.VariantB.OverallHeuristicScore), 2);

        summary.SchemaFailuresA = summary.Cases.Count(x => !x.VariantA.SchemaValid);
        summary.SchemaFailuresB = summary.Cases.Count(x => !x.VariantB.SchemaValid);

        var latA = summary.Cases.Select(x => x.VariantA.LatencyMs).ToList();
        var latB = summary.Cases.Select(x => x.VariantB.LatencyMs).ToList();

        summary.LatencyAAvgMs = Math.Round(latA.Average(), 2);
        summary.LatencyBAvgMs = Math.Round(latB.Average(), 2);
        summary.LatencyAP50Ms = Percentile(latA, 0.50);
        summary.LatencyAP95Ms = Percentile(latA, 0.95);
        summary.LatencyBP50Ms = Percentile(latB, 0.50);
        summary.LatencyBP95Ms = Percentile(latB, 0.95);
    }

    private static double Percentile(IReadOnlyList<double> values, double percentile)
    {
        if (values.Count == 0)
            return 0;
        var sorted = values.OrderBy(x => x).ToList();
        if (sorted.Count == 1)
            return Math.Round(sorted[0], 2);

        var index = (sorted.Count - 1) * percentile;
        var lower = (int)Math.Floor(index);
        var upper = (int)Math.Ceiling(index);
        if (lower == upper)
            return Math.Round(sorted[lower], 2);

        var fraction = index - lower;
        var value = sorted[lower] + ((sorted[upper] - sorted[lower]) * fraction);
        return Math.Round(value, 2);
    }

    private static void PrintSummary(LlmAbSummary summary)
    {
        Console.WriteLine();
        Console.WriteLine("=== LLM PROMPT A/B SUMMARY ===");
        Console.WriteLine($"cases={summary.TotalCases} winsA={summary.WinsA} winsB={summary.WinsB} ties={summary.Ties}");
        Console.WriteLine($"avgScoreA={summary.AverageScoreA} avgScoreB={summary.AverageScoreB}");
        Console.WriteLine($"schemaFailuresA={summary.SchemaFailuresA} schemaFailuresB={summary.SchemaFailuresB}");
        Console.WriteLine($"latencyA avg/p50/p95={summary.LatencyAAvgMs}/{summary.LatencyAP50Ms}/{summary.LatencyAP95Ms} ms");
        Console.WriteLine($"latencyB avg/p50/p95={summary.LatencyBAvgMs}/{summary.LatencyBP50Ms}/{summary.LatencyBP95Ms} ms");
        Console.WriteLine("==============================");
        Console.WriteLine();
    }

    private static string SaveSummary(LlmAbSummary summary)
    {
        var outDir = ResolveEvalArtifactsDirectory();
        var path = Path.Combine(outDir, $"llm-prompt-ab-{DateTime.UtcNow:yyyyMMdd-HHmmss}.json");
        var json = JsonSerializer.Serialize(summary, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, json, Encoding.UTF8);
        Console.WriteLine($"[llm-eval] summary: {path}");
        return path;
    }

    private static string ResolveEvalArtifactsDirectory()
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

    private static string BuildSystemPromptA(string language)
    {
        var lang = string.IsNullOrWhiteSpace(language) || language == "unknown" ? "en" : language;
        return $"""
You are coach_eval_A, a strict interview coaching evaluator.
Return JSON only. No markdown.
Return exactly keys: rubric, overall, feedback, drills.
Language: {lang}.
Use only evidence provided. Do not invent facts.
Feedback count: 5 to 10.
""";
    }

    private static string BuildSystemPromptB(string language)
    {
        var lang = string.IsNullOrWhiteSpace(language) || language == "unknown" ? "en" : language;
        return $"""
You are coach_eval_B, an evidence-grounded coaching engine.
Output must be valid JSON object only (no markdown fences, no extra keys).
Top-level keys allowed: rubric, overall, feedback, drills.
Language: {lang}.
Every feedback item must include concrete evidence, actionable suggestion, and valid time_range_ms.
If evidence is weak, explicitly say evidence is insufficient in evidence field while keeping schema valid.
""";
    }

    private static string BuildUserPromptA(EvidenceSummaryDto evidence, IReadOnlyList<string> expectedFocus)
    {
        var compact = JsonSerializer.Serialize(evidence);
        return $"""
Generate coaching output for this evidence.
Expected focus areas: [{string.Join(", ", expectedFocus)}]
Constraints:
- categories only: vision|audio|content|structure
- severity 1..5
- overall 0..100
- do not use markdown
EvidenceSummary:
{compact}
""";
    }

    private static string BuildUserPromptB(EvidenceSummaryDto evidence, IReadOnlyList<string> expectedFocus)
    {
        var compact = JsonSerializer.Serialize(evidence);
        return $"""
Create a coaching plan from the evidence below.
Prioritize these focus dimensions when relevant: [{string.Join(", ", expectedFocus)}].
Constraints:
1) feedback count 5..10
2) each feedback item: evidence + time_range_ms + suggestion + example_phrase
3) no fabricated claims beyond provided evidence
4) return strict JSON object only
EvidenceSummary:
{compact}
""";
    }

    private static bool IsEnabled(string key)
        => string.Equals(Environment.GetEnvironmentVariable(key), "true", StringComparison.OrdinalIgnoreCase);

    private sealed class NoopOllamaClient : IOllamaClient
    {
        public Task<string> ChatJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
            => throw new NotSupportedException();
    }

    private sealed class LlmEvalSettings
    {
        public string BaseUrl { get; init; } = "http://localhost:11434";
        public string Model { get; init; } = "qwen2.5:7b-instruct";
        public int TimeoutSeconds { get; init; } = 60;

        public static LlmEvalSettings LoadFromEnvironment()
        {
            var baseUrl = Environment.GetEnvironmentVariable("LLM_EVAL_BASE_URL");
            var model = Environment.GetEnvironmentVariable("LLM_EVAL_MODEL");
            var timeoutRaw = Environment.GetEnvironmentVariable("LLM_EVAL_TIMEOUT_SECONDS");
            var fallbackModel = Environment.GetEnvironmentVariable("LLM_MODEL");

            return new LlmEvalSettings
            {
                BaseUrl = string.IsNullOrWhiteSpace(baseUrl) ? "http://localhost:11434" : baseUrl.TrimEnd('/'),
                Model = string.IsNullOrWhiteSpace(model)
                    ? (string.IsNullOrWhiteSpace(fallbackModel) ? "qwen2.5:7b-instruct" : fallbackModel)
                    : model,
                TimeoutSeconds = int.TryParse(timeoutRaw, out var timeout) ? Math.Clamp(timeout, 10, 600) : 60
            };
        }
    }

    private sealed class LlmEvalManifest
    {
        public List<LlmEvalCase> Cases { get; set; } = [];
    }

    private sealed class LlmEvalCase
    {
        public string CaseId { get; set; } = string.Empty;
        public string EvidenceFile { get; set; } = string.Empty;
        public List<string> ExpectedFocus { get; set; } = [];
    }

    private sealed class VariantScore
    {
        public string Variant { get; set; } = string.Empty;
        public bool SchemaValid { get; set; }
        public List<string> Errors { get; set; } = [];
        public double LatencyMs { get; set; }
        public int FeedbackCount { get; set; }
        public int DrillCount { get; set; }
        public double FeedbackCountScore { get; set; }
        public double EvidenceGroundingScore { get; set; }
        public double ActionabilityScore { get; set; }
        public double DiversityScore { get; set; }
        public double DrillQualityScore { get; set; }
        public double SafetyFormatScore { get; set; }
        public double OverallHeuristicScore { get; set; }
    }

    private sealed class LlmAbCaseResult
    {
        public string CaseId { get; set; } = string.Empty;
        public List<string> ExpectedFocus { get; set; } = [];
        public VariantScore VariantA { get; set; } = new();
        public VariantScore VariantB { get; set; } = new();
        public string Winner { get; set; } = "tie";
        public string Notes { get; set; } = string.Empty;
    }

    private sealed class LlmAbSummary
    {
        public DateTime GeneratedAtUtc { get; set; }
        public string BaseUrl { get; set; } = string.Empty;
        public string Model { get; set; } = string.Empty;
        public int TotalCases { get; set; }
        public int WinsA { get; set; }
        public int WinsB { get; set; }
        public int Ties { get; set; }
        public double AverageScoreA { get; set; }
        public double AverageScoreB { get; set; }
        public int SchemaFailuresA { get; set; }
        public int SchemaFailuresB { get; set; }
        public double LatencyAAvgMs { get; set; }
        public double LatencyBAvgMs { get; set; }
        public double LatencyAP50Ms { get; set; }
        public double LatencyAP95Ms { get; set; }
        public double LatencyBP50Ms { get; set; }
        public double LatencyBP95Ms { get; set; }
        public List<LlmAbCaseResult> Cases { get; set; } = [];
    }
}
