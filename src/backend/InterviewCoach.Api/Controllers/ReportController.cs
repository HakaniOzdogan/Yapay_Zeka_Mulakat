using System.Diagnostics;
using System.Text.Json;
using InterviewCoach.Api.Services;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/sessions/{sessionId}")]
[Authorize]
[SessionOwnership]
public class ReportController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IScoringService _scoringService;
    private readonly ApiTelemetry _telemetry;
    private readonly ILogger<ReportController> _logger;

    public ReportController(
        ApplicationDbContext db,
        IScoringService scoringService,
        ApiTelemetry telemetry,
        ILogger<ReportController> logger)
    {
        _db = db;
        _scoringService = scoringService;
        _telemetry = telemetry;
        _logger = logger;
    }

    /// <summary>
    /// Finalizes a session and returns scorecard and pattern summary.
    /// </summary>
    [HttpPost("finalize")]
    [ProducesResponseType(typeof(FinalizeSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<FinalizeSessionResponse>> FinalizeSession(Guid sessionId)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["route"] = "/api/sessions/{sessionId}/finalize",
            ["requestId"] = HttpContext.TraceIdentifier
        });

        var session = await _db.Sessions
            .Include(s => s.TranscriptSegments)
            .Include(s => s.MetricEvents)
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session == null)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        var sw = Stopwatch.StartNew();
        using var activity = _telemetry.ActivitySource.StartActivity("finalize.computation", ActivityKind.Internal);
        activity?.SetTag("session.id", sessionId.ToString());

        // Parse stats from Session
        var stats = ParseStats(session.StatsJson);

        // Compute scorecard using ScoringService
        var scoreCard = _scoringService.ComputeScoreCard(session, session.MetricEvents.ToList(), stats);

        // Generate feedback items using ScoringService
        var feedbackItems = _scoringService.GenerateFeedback(session, scoreCard, session.MetricEvents.ToList(), stats);

        // Save scorecard
        if (scoreCard != null)
        {
            _db.ScoreCards.Add(scoreCard);
        }

        // Save feedback items
        _db.FeedbackItems.AddRange(feedbackItems);

        // Update session status
        session.Status = "Completed";

        await _db.SaveChangesAsync();

        sw.Stop();
        _telemetry.FinalizeRunsTotal.Add(1);
        _telemetry.FinalizeDurationMs.Record(sw.Elapsed.TotalMilliseconds);

        activity?.SetTag("inserted.count", feedbackItems.Count + (scoreCard != null ? 1 : 0));
        activity?.SetTag("duplicates.count", 0);
        activity?.SetTag("durationMs", sw.Elapsed.TotalMilliseconds);

        _logger.LogInformation(
            "Finalize summary: derivedFeatureCount={derivedFeatureCount}, patternCount={patternCount}, overallScore={overallScore}",
            0,
            feedbackItems.Count,
            scoreCard?.OverallScore);

        var response = new FinalizeSessionResponse
        {
            SessionId = sessionId,
            ScoreCard = scoreCard != null ? new LegacyScoreCardDto(scoreCard) : null,
            Patterns = feedbackItems.Select(f => new FinalizePatternDto
            {
                Type = f.Category,
                StartMs = f.StartMs,
                EndMs = f.EndMs,
                Severity = f.Severity,
                Evidence = f.Details
            }).ToList(),
            DerivedFeatureCount = 0
        };

        return Ok(response);
    }

    /// <summary>
    /// Returns legacy report format for a finalized session.
    /// </summary>
    [HttpGet("report")]
    [ProducesResponseType(typeof(LegacyReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<LegacyReportDto>> GetReport(Guid sessionId)
    {
        var session = await _db.Sessions
            .Include(s => s.ScoreCard)
            .Include(s => s.FeedbackItems)
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session == null)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        var report = new LegacyReportDto
        {
            SessionId = sessionId,
            ScoreCard = session.ScoreCard != null ? new LegacyScoreCardDto(session.ScoreCard) : null,
            FeedbackItems = session.FeedbackItems.Select(f => new LegacyFeedbackItemDto(f)).ToList()
        };

        return Ok(report);
    }

    private Dictionary<string, object>? ParseStats(string statsJson)
    {
        if (string.IsNullOrWhiteSpace(statsJson) || statsJson == "{}")
            return null;

        try
        {
            using var doc = JsonDocument.Parse(statsJson);
            var stats = new Dictionary<string, object>();

            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Number)
                {
                    if (prop.Value.TryGetInt32(out var intValue))
                        stats[prop.Name] = intValue;
                    else if (prop.Value.TryGetSingle(out var floatValue))
                        stats[prop.Name] = floatValue;
                    else if (prop.Value.TryGetInt64(out var longValue))
                        stats[prop.Name] = longValue;
                }
                else
                {
                    stats[prop.Name] = prop.Value.GetRawText();
                }
            }

            return stats.Count > 0 ? stats : null;
        }
        catch
        {
            return null;
        }
    }
}

public class FinalizeSessionResponse
{
    /// <summary>Finalized session identifier.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Computed scorecard for the session.</summary>
    public LegacyScoreCardDto? ScoreCard { get; set; }

    /// <summary>Detected feedback patterns.</summary>
    public List<FinalizePatternDto> Patterns { get; set; } = [];

    /// <summary>Total derived features generated for the session.</summary>
    public int DerivedFeatureCount { get; set; }
}

public class FinalizePatternDto
{
    /// <summary>Pattern type/category.</summary>
    public string Type { get; set; } = string.Empty;

    /// <summary>Pattern start timestamp in milliseconds.</summary>
    public long? StartMs { get; set; }

    /// <summary>Pattern end timestamp in milliseconds.</summary>
    public long? EndMs { get; set; }

    /// <summary>Pattern severity value.</summary>
    public int Severity { get; set; }

    /// <summary>Evidence text describing the pattern.</summary>
    public string Evidence { get; set; } = string.Empty;
}

public class LegacyReportDto
{
    /// <summary>Session identifier.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Legacy scorecard model.</summary>
    public LegacyScoreCardDto? ScoreCard { get; set; }

    /// <summary>Legacy feedback list.</summary>
    public List<LegacyFeedbackItemDto> FeedbackItems { get; set; } = [];
}

public class LegacyScoreCardDto
{
    public LegacyScoreCardDto() { }
    public LegacyScoreCardDto(ScoreCard sc)
    {
        EyeContactScore = sc.EyeContactScore;
        SpeakingRateScore = sc.SpeakingRateScore;
        FillerScore = sc.FillerScore;
        PostureScore = sc.PostureScore;
        OverallScore = sc.OverallScore;
    }

    public int EyeContactScore { get; set; }
    public int SpeakingRateScore { get; set; }
    public int FillerScore { get; set; }
    public int PostureScore { get; set; }
    public int OverallScore { get; set; }
}

public class LegacyFeedbackItemDto
{
    public LegacyFeedbackItemDto() { }
    public LegacyFeedbackItemDto(FeedbackItem f)
    {
        Category = f.Category;
        Severity = f.Severity;
        Title = f.Title;
        Details = f.Details;
        Suggestion = f.Suggestion;
        ExampleText = f.ExampleText;
        StartMs = f.StartMs;
        EndMs = f.EndMs;
    }

    public string Category { get; set; } = string.Empty;
    public int Severity { get; set; }
    public string Title { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
    public string Suggestion { get; set; } = string.Empty;
    public string ExampleText { get; set; } = string.Empty;
    public long? StartMs { get; set; }
    public long? EndMs { get; set; }
}
