using InterviewCoach.Api.Services;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/sessions/{sessionId}")]
public class ReportController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IScoringService _scoringService;

    public ReportController(ApplicationDbContext db, IScoringService scoringService)
    {
        _db = db;
        _scoringService = scoringService;
    }

    [HttpPost("finalize")]
    public async Task<ActionResult<ReportDto>> FinalizeSession(Guid sessionId)
    {
        var session = await _db.Sessions
            .Include(s => s.TranscriptSegments)
            .Include(s => s.MetricEvents)
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session == null)
            return NotFound();

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

        var report = new ReportDto
        {
            SessionId = sessionId,
            ScoreCard = scoreCard != null ? new ScoreCardDto(scoreCard) : null,
            FeedbackItems = feedbackItems.Select(f => new FeedbackItemDto(f)).ToList()
        };

        return Ok(report);
    }

    [HttpGet("report")]
    public async Task<ActionResult<ReportDto>> GetReport(Guid sessionId)
    {
        var session = await _db.Sessions
            .Include(s => s.ScoreCard)
            .Include(s => s.FeedbackItems)
            .FirstOrDefaultAsync(s => s.Id == sessionId);

        if (session == null)
            return NotFound();

        var report = new ReportDto
        {
            SessionId = sessionId,
            ScoreCard = session.ScoreCard != null ? new ScoreCardDto(session.ScoreCard) : null,
            FeedbackItems = session.FeedbackItems.Select(f => new FeedbackItemDto(f)).ToList()
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

public class ReportDto
{
    public Guid SessionId { get; set; }
    public ScoreCardDto? ScoreCard { get; set; }
    public List<FeedbackItemDto> FeedbackItems { get; set; } = [];
}

public class ScoreCardDto
{
    public ScoreCardDto() { }
    public ScoreCardDto(ScoreCard sc)
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

public class FeedbackItemDto
{
    public FeedbackItemDto() { }
    public FeedbackItemDto(FeedbackItem f)
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
