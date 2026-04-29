using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using InterviewCoach.Api.Services;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/sessions/{sessionId:guid}/report")]
[Authorize]
[SessionOwnership]
public class ReportExportController : ControllerBase
{
    private static readonly string[] DerivedKeys =
    [
        "eyeContact",
        "posture",
        "fidget",
        "headJitter",
        "wpm",
        "filler",
        "pauseMs"
    ];

    private readonly ApplicationDbContext _db;
    private readonly IEvidenceSummaryService _evidenceSummaryService;

    public ReportExportController(ApplicationDbContext db, IEvidenceSummaryService evidenceSummaryService)
    {
        _db = db;
        _evidenceSummaryService = evidenceSummaryService;
    }

    /// <summary>
    /// Downloads a JSON report export package.
    /// </summary>
    [HttpGet("export.json")]
    [Produces("application/json")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ExportJson(Guid sessionId, CancellationToken cancellationToken)
    {
        var exists = await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId, cancellationToken);
        if (!exists)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        var report = await BuildReportAsync(sessionId, cancellationToken);
        var evidenceSummary = await _evidenceSummaryService.BuildAsync(sessionId, cancellationToken);
        var latestLlmCoaching = await GetLatestLlmCoachingAsync(sessionId, cancellationToken);

        var package = new ReportExportPackageDto
        {
            Version = 1,
            ExportedAtUtc = DateTime.UtcNow,
            SessionId = sessionId,
            Report = report,
            EvidenceSummary = evidenceSummary,
            LatestLlmCoaching = latestLlmCoaching
        };

        var json = JsonSerializer.Serialize(package, new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            DictionaryKeyPolicy = JsonNamingPolicy.CamelCase,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
        });

        return File(
            Encoding.UTF8.GetBytes(json),
            "application/json",
            $"interview-report-{sessionId}.json");
    }

    /// <summary>
    /// Downloads a Markdown report summary.
    /// </summary>
    [HttpGet("export.md")]
    [Produces("text/markdown")]
    [ProducesResponseType(typeof(FileContentResult), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ExportMarkdown(Guid sessionId, CancellationToken cancellationToken)
    {
        var exists = await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId, cancellationToken);
        if (!exists)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        var report = await BuildReportAsync(sessionId, cancellationToken);
        var evidenceSummary = await _evidenceSummaryService.BuildAsync(sessionId, cancellationToken);
        var latestLlmCoaching = await GetLatestLlmCoachingAsync(sessionId, cancellationToken);

        var markdown = BuildMarkdown(sessionId, report, evidenceSummary, latestLlmCoaching);

        return File(
            Encoding.UTF8.GetBytes(markdown),
            "text/markdown",
            $"interview-report-{sessionId}.md");
    }

    private async Task<ReportDto> BuildReportAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new SessionInfoDto
            {
                Id = s.Id,
                CreatedAt = s.CreatedAt,
                Role = s.SelectedRole,
                Language = s.Language,
                Mode = null,
                Status = s.Status
            })
            .FirstAsync(cancellationToken);

        var scoreCard = await _db.ScoreCards
            .AsNoTracking()
            .Where(sc => sc.SessionId == sessionId)
            .Select(sc => new ScoreCardReadDto
            {
                EyeContact = sc.EyeContactScore,
                Posture = sc.PostureScore,
                Fidget = null,
                SpeakingRate = sc.SpeakingRateScore,
                FillerWords = sc.FillerScore,
                Overall = sc.OverallScore,
                CreatedAt = null
            })
            .FirstOrDefaultAsync(cancellationToken);

        var patterns = await _db.FeedbackItems
            .AsNoTracking()
            .Where(p => p.SessionId == sessionId)
            .OrderBy(p => p.StartMs)
            .Select(p => new PatternDto
            {
                Type = p.Category,
                StartMs = p.StartMs,
                EndMs = p.EndMs,
                Severity = p.Severity,
                Evidence = p.Details
            })
            .ToListAsync(cancellationToken);

        var questions = await _db.Questions
            .AsNoTracking()
            .Where(q => q.SessionId == sessionId)
            .OrderBy(q => q.Order)
            .Select(q => new ReportQuestionDto
            {
                Id = q.Id,
                Order = q.Order,
                Prompt = q.Prompt,
                AudioUrl = q.AudioUrl,
                CreatedAt = q.CreatedAt
            })
            .ToListAsync(cancellationToken);

        var derivedSeries = DerivedKeys.ToDictionary(
            key => key,
            _ => new List<DerivedPointDto>());

        return new ReportDto
        {
            Session = session,
            ScoreCard = scoreCard,
            Patterns = patterns,
            Questions = questions,
            DerivedSeries = derivedSeries,
            Transcript = [],
            TranscriptNotice = "Transcript is disabled in this build."
        };
    }

    private async Task<JsonElement?> GetLatestLlmCoachingAsync(Guid sessionId, CancellationToken cancellationToken)
    {
        var latestRun = await _db.LlmRuns
            .AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.Kind == "coach")
            .OrderByDescending(r => r.CreatedAt)
            .Select(r => new
            {
                r.Id,
                r.PromptVersion,
                r.Model,
                r.InputHash,
                r.CreatedAt,
                r.OutputJson
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (latestRun != null)
        {
            var runPayload = TryAttachMeta(
                latestRun.OutputJson,
                latestRun.PromptVersion,
                latestRun.Model,
                latestRun.InputHash,
                latestRun.Id,
                latestRun.CreatedAt);

            if (runPayload.HasValue)
                return runPayload.Value;
        }

        var latestMetricPayload = await _db.MetricEvents
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId && e.Type == "llm_coaching_v1")
            .OrderByDescending(e => e.CreatedAt)
            .Select(e => e.PayloadJson)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(latestMetricPayload))
            return null;

        return TryParseJsonElement(latestMetricPayload);
    }

    private static JsonElement? TryAttachMeta(
        string outputJson,
        string promptVersion,
        string model,
        string inputHash,
        Guid llmRunId,
        DateTime createdAtUtc)
    {
        try
        {
            using var outputDoc = JsonDocument.Parse(outputJson);
            if (outputDoc.RootElement.ValueKind != JsonValueKind.Object)
                return outputDoc.RootElement.Clone();

            using var stream = new MemoryStream();
            using (var writer = new Utf8JsonWriter(stream))
            {
                writer.WriteStartObject();
                writer.WritePropertyName("_meta");
                writer.WriteStartObject();
                writer.WriteString("promptVersion", promptVersion);
                writer.WriteString("model", model);
                writer.WriteString("inputHash", inputHash);
                writer.WriteString("llmRunId", llmRunId);
                writer.WriteString("createdAtUtc", createdAtUtc);
                writer.WriteEndObject();

                foreach (var property in outputDoc.RootElement.EnumerateObject())
                {
                    if (string.Equals(property.Name, "_meta", StringComparison.OrdinalIgnoreCase))
                        continue;

                    writer.WritePropertyName(property.Name);
                    property.Value.WriteTo(writer);
                }

                writer.WriteEndObject();
            }

            using var mergedDoc = JsonDocument.Parse(stream.ToArray());
            return mergedDoc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static JsonElement? TryParseJsonElement(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.Clone();
        }
        catch
        {
            return null;
        }
    }

    private static string BuildMarkdown(
        Guid sessionId,
        ReportDto report,
        EvidenceSummaryDto? evidenceSummary,
        JsonElement? latestLlmCoaching)
    {
        var sb = new StringBuilder(4096);

        sb.AppendLine("# Interview Report Export");
        sb.AppendLine();
        sb.AppendLine($"- Session ID: `{sessionId}`");
        sb.AppendLine($"- Session Date (UTC): {report.Session.CreatedAt:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine($"- Exported At (UTC): {DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC");
        sb.AppendLine();

        sb.AppendLine("## Scorecard");
        if (report.ScoreCard == null)
        {
            sb.AppendLine("- Not available.");
        }
        else
        {
            sb.AppendLine($"- Eye Contact: {ToText(report.ScoreCard.EyeContact)}");
            sb.AppendLine($"- Posture: {ToText(report.ScoreCard.Posture)}");
            sb.AppendLine($"- Fidget: {ToText(report.ScoreCard.Fidget)}");
            sb.AppendLine($"- Speaking Rate: {ToText(report.ScoreCard.SpeakingRate)}");
            sb.AppendLine($"- Filler Words: {ToText(report.ScoreCard.FillerWords)}");
            sb.AppendLine($"- Overall: {ToText(report.ScoreCard.Overall)}");
        }

        sb.AppendLine();
        sb.AppendLine("## Questions and Audio Recordings");
        AppendQuestionsAndAudio(sb, report);

        sb.AppendLine();
        sb.AppendLine("## Patterns");
        if (report.Patterns.Count == 0)
        {
            sb.AppendLine("- None.");
        }
        else
        {
            foreach (var pattern in report.Patterns.Take(10))
            {
                var start = pattern.StartMs ?? 0;
                var end = pattern.EndMs ?? start;
                sb.AppendLine($"- [{FormatMs(start)}-{FormatMs(end)}] **{pattern.Type}** (severity {pattern.Severity}): {pattern.Evidence}");
            }
        }

        sb.AppendLine();
        sb.AppendLine("## Signal Summary");
        if (evidenceSummary?.Signals != null)
        {
            var v = evidenceSummary.Signals.Vision;
            var a = evidenceSummary.Signals.Audio;
            sb.AppendLine($"- Vision eyeContactAvg: {ToFixed(v.EyeContactAvg)}");
            sb.AppendLine($"- Vision postureAvg: {ToFixed(v.PostureAvg)}");
            sb.AppendLine($"- Vision fidgetAvg: {ToFixed(v.FidgetAvg)}");
            sb.AppendLine($"- Vision headJitterAvg: {ToFixed(v.HeadJitterAvg)}");
            sb.AppendLine($"- Audio wpmMedian: {ToFixed(a.WpmMedian)}");
            sb.AppendLine($"- Audio fillerPerMin: {ToFixed(a.FillerPerMin)}");
            sb.AppendLine($"- Audio pauseMsPerMin: {ToFixed(a.PauseMsPerMin)}");
        }
        else
        {
            sb.AppendLine("- Not available.");
        }

        sb.AppendLine();
        sb.AppendLine("## AI Coach");
        if (latestLlmCoaching is not { } coachingElement)
        {
            sb.AppendLine("- Not available.");
        }
        else
        {
            AppendAiCoach(sb, coachingElement);
        }

        sb.AppendLine();
        sb.AppendLine("## Transcript Excerpt");
        AppendTranscriptExcerpt(sb, report, evidenceSummary);

        return sb.ToString();
    }

    private static void AppendQuestionsAndAudio(StringBuilder sb, ReportDto report)
    {
        if (report.Questions.Count == 0)
        {
            sb.AppendLine("- No questions found.");
            return;
        }

        foreach (var question in report.Questions)
        {
            sb.AppendLine($"- Soru {question.Order}: {NormalizeMarkdownLine(question.Prompt)}");
            sb.AppendLine($"  - Audio: {ToAudioText(question.AudioUrl)}");
        }
    }

    private static void AppendAiCoach(StringBuilder sb, JsonElement coaching)
    {
        if (coaching.ValueKind != JsonValueKind.Object)
        {
            sb.AppendLine("- Invalid coaching payload format.");
            return;
        }

        if (coaching.TryGetProperty("_meta", out var meta) && meta.ValueKind == JsonValueKind.Object)
        {
            if (meta.TryGetProperty("model", out var model))
                sb.AppendLine($"- Model: {model.GetString()}");
            if (meta.TryGetProperty("promptVersion", out var promptVersion))
                sb.AppendLine($"- Prompt Version: {promptVersion.GetString()}");
            if (meta.TryGetProperty("inputHash", out var inputHash))
                sb.AppendLine($"- Input Hash: `{inputHash.GetString()}`");
        }

        if (coaching.TryGetProperty("rubric", out var rubric) && rubric.ValueKind == JsonValueKind.Object)
        {
            sb.AppendLine("- Rubric:");
            AppendRubricLine(sb, rubric, "technical_correctness");
            AppendRubricLine(sb, rubric, "depth");
            AppendRubricLine(sb, rubric, "structure");
            AppendRubricLine(sb, rubric, "clarity");
            AppendRubricLine(sb, rubric, "confidence");
        }

        if (coaching.TryGetProperty("overall", out var overall) && overall.ValueKind == JsonValueKind.Number)
        {
            sb.AppendLine($"- Overall: {overall.GetRawText()}");
        }

        if (coaching.TryGetProperty("feedback", out var feedback) && feedback.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("- Top Feedback:");
            foreach (var item in feedback.EnumerateArray().Take(5))
            {
                var category = item.TryGetProperty("category", out var c) ? c.GetString() : "unknown";
                var title = item.TryGetProperty("title", out var t) ? t.GetString() : "-";
                var evidence = item.TryGetProperty("evidence", out var e) ? e.GetString() : "-";
                var suggestion = item.TryGetProperty("suggestion", out var s) ? s.GetString() : "-";

                var rangeText = string.Empty;
                if (item.TryGetProperty("time_range_ms", out var range)
                    && range.ValueKind == JsonValueKind.Array
                    && range.GetArrayLength() == 2)
                {
                    var vals = range.EnumerateArray().ToArray();
                    if (vals[0].TryGetInt64(out var startMs) && vals[1].TryGetInt64(out var endMs))
                    {
                        rangeText = $" [{FormatMs(startMs)}-{FormatMs(endMs)}]";
                    }
                }

                sb.AppendLine($"  - **{category}**{rangeText}: {title}");
                sb.AppendLine($"    - Evidence: {evidence}");
                sb.AppendLine($"    - Suggestion: {suggestion}");
            }
        }

        if (coaching.TryGetProperty("drills", out var drills) && drills.ValueKind == JsonValueKind.Array)
        {
            sb.AppendLine("- Drills:");
            foreach (var drill in drills.EnumerateArray().Take(3))
            {
                var title = drill.TryGetProperty("title", out var t) ? t.GetString() : "Drill";
                var duration = drill.TryGetProperty("duration_min", out var d) ? d.GetRawText() : "-";
                sb.AppendLine($"  - {title} ({duration} min)");

                if (drill.TryGetProperty("steps", out var steps) && steps.ValueKind == JsonValueKind.Array)
                {
                    foreach (var step in steps.EnumerateArray().Take(5))
                    {
                        if (step.ValueKind == JsonValueKind.String)
                            sb.AppendLine($"    - {step.GetString()}");
                    }
                }
            }
        }
    }

    private static void AppendRubricLine(StringBuilder sb, JsonElement rubric, string key)
    {
        if (rubric.TryGetProperty(key, out var value) && value.ValueKind == JsonValueKind.Number)
        {
            sb.AppendLine($"  - {key}: {value.GetRawText()}");
        }
    }

    private static void AppendTranscriptExcerpt(StringBuilder sb, ReportDto report, EvidenceSummaryDto? evidenceSummary)
    {
        if (!string.IsNullOrWhiteSpace(report.TranscriptNotice))
        {
            sb.AppendLine($"- {report.TranscriptNotice}");
            return;
        }

        if (evidenceSummary?.TranscriptSlices.Count > 0)
        {
            foreach (var slice in evidenceSummary.TranscriptSlices.Take(3))
            {
                sb.AppendLine($"- [{FormatMs(slice.StartMs)}-{FormatMs(slice.EndMs)}] {slice.Text}");
            }

            return;
        }

        if (report.Transcript.Count == 0)
        {
            sb.AppendLine("- Transcript not available.");
            return;
        }

        var first = report.Transcript.Take(3);
        var last = report.Transcript.Skip(Math.Max(0, report.Transcript.Count - 3));

        foreach (var line in first)
        {
            sb.AppendLine($"- [{FormatMs(line.StartMs)}-{FormatMs(line.EndMs)}] {line.Text}");
        }

        if (report.Transcript.Count > 6)
        {
            sb.AppendLine("- ...");
        }

        foreach (var line in last)
        {
            if (report.Transcript.Count <= 6 && first.Contains(line))
                continue;

            sb.AppendLine($"- [{FormatMs(line.StartMs)}-{FormatMs(line.EndMs)}] {line.Text}");
        }
    }

    private static string FormatMs(long ms)
    {
        var totalSeconds = Math.Max(0, ms / 1000);
        var minutes = totalSeconds / 60;
        var seconds = totalSeconds % 60;
        return $"{minutes:00}:{seconds:00}";
    }

    private static string ToText(int? value) => value?.ToString() ?? "-";

    private static string ToFixed(double? value) => value.HasValue ? value.Value.ToString("0.###") : "-";

    private static string ToAudioText(string? audioUrl)
        => string.IsNullOrWhiteSpace(audioUrl) ? "Not available" : audioUrl.Trim();

    private static string NormalizeMarkdownLine(string? value)
        => string.IsNullOrWhiteSpace(value)
            ? "-"
            : value.Replace("\r", " ").Replace("\n", " ").Trim();
}

public class ReportExportPackageDto
{
    public int Version { get; set; }
    public DateTime ExportedAtUtc { get; set; }
    public Guid SessionId { get; set; }
    public ReportDto Report { get; set; } = new();
    public EvidenceSummaryDto? EvidenceSummary { get; set; }
    public JsonElement? LatestLlmCoaching { get; set; }
}
