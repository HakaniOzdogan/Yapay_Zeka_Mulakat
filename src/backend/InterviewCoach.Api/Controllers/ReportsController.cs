using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using InterviewCoach.Api.Services;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/reports")]
[Authorize]
public class ReportsController : ControllerBase
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

    public ReportsController(ApplicationDbContext db)
    {
        _db = db;
    }

    /// <summary>
    /// Returns the aggregated report for a session.
    /// </summary>
    [HttpGet("{sessionId:guid}")]
    [SessionOwnership]
    [ProducesResponseType(typeof(ReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ReportDto>> GetReport(Guid sessionId)
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
            .FirstOrDefaultAsync();

        if (session == null)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

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
            .FirstOrDefaultAsync();

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
            .ToListAsync();

        var transcript = await _db.TranscriptSegments
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .OrderBy(t => t.StartMs)
            .Select(t => new TranscriptLineDto
            {
                StartMs = t.StartMs,
                EndMs = t.EndMs,
                Text = t.Text
            })
            .ToListAsync();

        var derivedSeries = DerivedKeys.ToDictionary(
            key => key,
            _ => new List<DerivedPointDto>());

        var report = new ReportDto
        {
            Session = session,
            ScoreCard = scoreCard,
            Patterns = patterns,
            DerivedSeries = derivedSeries,
            Transcript = transcript
        };

        return Ok(report);
    }
}

public class ReportDto
{
    /// <summary>Session metadata.</summary>
    public SessionInfoDto Session { get; set; } = new();

    /// <summary>Computed scorecard values if available.</summary>
    public ScoreCardReadDto? ScoreCard { get; set; }

    /// <summary>Detected pattern hits.</summary>
    public List<PatternDto> Patterns { get; set; } = [];

    /// <summary>Derived metric time series grouped by metric type.</summary>
    public Dictionary<string, List<DerivedPointDto>> DerivedSeries { get; set; } = [];

    /// <summary>Ordered transcript lines.</summary>
    public List<TranscriptLineDto> Transcript { get; set; } = [];
}

public class SessionInfoDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Role { get; set; }
    public string? Language { get; set; }
    public string? Mode { get; set; }
    public string? Status { get; set; }
}

public class ScoreCardReadDto
{
    public int? EyeContact { get; set; }
    public int? Posture { get; set; }
    public int? Fidget { get; set; }
    public int? SpeakingRate { get; set; }
    public int? FillerWords { get; set; }
    public int? Overall { get; set; }
    public DateTime? CreatedAt { get; set; }
}

public class PatternDto
{
    public string Type { get; set; } = string.Empty;
    public long? StartMs { get; set; }
    public long? EndMs { get; set; }
    public int Severity { get; set; }
    public string Evidence { get; set; } = string.Empty;
}

public class DerivedPointDto
{
    public long WindowStartMs { get; set; }
    public long WindowEndMs { get; set; }
    public double Value { get; set; }
}

public class TranscriptLineDto
{
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public string Text { get; set; } = string.Empty;
}
