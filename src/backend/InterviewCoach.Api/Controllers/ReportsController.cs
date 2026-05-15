using System.Text.Json;
using System.Text.Json.Serialization;
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
    ];

    private readonly ApplicationDbContext _db;

    public ReportsController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpGet("{sessionId:guid}")]
    [SessionOwnership]
    [ProducesResponseType(typeof(ReportDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ReportDto>> GetReport(Guid sessionId, CancellationToken cancellationToken)
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
            .FirstOrDefaultAsync(cancellationToken);

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

        // Load all questions with their timing info
        var questionEntities = await _db.Questions
            .AsNoTracking()
            .Where(q => q.SessionId == sessionId)
            .OrderBy(q => q.Order)
            .ToListAsync(cancellationToken);

        // Load all transcript segments for this session
        var allTranscript = await _db.TranscriptSegments
            .AsNoTracking()
            .Where(s => s.SessionId == sessionId)
            .OrderBy(s => s.StartMs)
            .Select(s => new { s.StartMs, s.EndMs, s.Text, s.QuestionOrder })
            .ToListAsync(cancellationToken);

        // Load all metric events for per-question windowing
        var allMetrics = await _db.MetricEvents
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId && e.Type == "vision_metrics_v1")
            .OrderBy(e => e.TsMs)
            .Select(e => new { e.TsMs, e.PayloadJson })
            .ToListAsync(cancellationToken);

        // Build per-question data
        var questions = new List<ReportQuestionDto>();
        for (int i = 0; i < questionEntities.Count; i++)
        {
            var q = questionEntities[i];

            // Per-question transcript: prefer QuestionOrder match, fall back to time window
            List<TranscriptLineDto> qTranscript;
            var byOrder = allTranscript
                .Where(s => s.QuestionOrder == q.Order)
                .Select(s => new TranscriptLineDto { StartMs = s.StartMs, EndMs = s.EndMs, Text = s.Text })
                .ToList();

            if (byOrder.Count > 0)
            {
                qTranscript = byOrder;
            }
            else if (q.StartMs.HasValue)
            {
                // Fallback: time-window based (for sessions recorded before QuestionOrder was tracked)
                var endBound = q.EndMs ?? (i + 1 < questionEntities.Count ? questionEntities[i + 1].StartMs : long.MaxValue);
                qTranscript = allTranscript
                    .Where(s => s.StartMs >= q.StartMs.Value && s.StartMs < (endBound ?? long.MaxValue))
                    .Select(s => new TranscriptLineDto { StartMs = s.StartMs, EndMs = s.EndMs, Text = s.Text })
                    .ToList();
            }
            else
            {
                qTranscript = [];
            }

            // Per-question metrics time series
            var qMetrics = new Dictionary<string, List<DerivedPointDto>>();
            foreach (var key in DerivedKeys)
                qMetrics[key] = [];

            if (q.StartMs.HasValue && allMetrics.Count > 0)
            {
                var endBound = q.EndMs
                    ?? (i + 1 < questionEntities.Count ? questionEntities[i + 1].StartMs : null);

                var windowEvents = allMetrics
                    .Where(e => e.TsMs >= q.StartMs.Value
                             && (endBound == null || e.TsMs <= endBound.Value))
                    .ToList();

                foreach (var ev in windowEvents)
                {
                    try
                    {
                        using var doc = JsonDocument.Parse(ev.PayloadJson);
                        var root = doc.RootElement;
                        foreach (var key in DerivedKeys)
                        {
                            if (root.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
                            {
                                qMetrics[key].Add(new DerivedPointDto
                                {
                                    WindowStartMs = ev.TsMs,
                                    WindowEndMs = ev.TsMs + 500,
                                    Value = val.GetDouble()
                                });
                            }
                        }
                    }
                    catch { /* malformed JSON — skip */ }
                }
            }

            questions.Add(new ReportQuestionDto
            {
                Id = q.Id,
                Order = q.Order,
                Prompt = q.Prompt,
                AudioUrl = q.AudioUrl,
                ScreenAudioUrl = q.ScreenAudioUrl,
                StartMs = q.StartMs,
                EndMs = q.EndMs,
                CreatedAt = q.CreatedAt,
                Transcript = qTranscript,
                Metrics = qMetrics
            });
        }

        // Session-level transcript (all segments, no filter)
        var transcriptLines = allTranscript
            .Select(s => new TranscriptLineDto { StartMs = s.StartMs, EndMs = s.EndMs, Text = s.Text })
            .ToList();

        // Session-level derived series (full session)
        var derivedSeries = DerivedKeys.ToDictionary(key => key, _ => new List<DerivedPointDto>());
        foreach (var ev in allMetrics)
        {
            try
            {
                using var doc = JsonDocument.Parse(ev.PayloadJson);
                var root = doc.RootElement;
                foreach (var key in DerivedKeys)
                {
                    if (root.TryGetProperty(key, out var val) && val.ValueKind == JsonValueKind.Number)
                    {
                        derivedSeries[key].Add(new DerivedPointDto
                        {
                            WindowStartMs = ev.TsMs,
                            WindowEndMs = ev.TsMs + 500,
                            Value = val.GetDouble()
                        });
                    }
                }
            }
            catch { /* malformed JSON — skip */ }
        }

        var report = new ReportDto
        {
            Session = session,
            ScoreCard = scoreCard,
            Patterns = patterns,
            Questions = questions,
            DerivedSeries = derivedSeries,
            Transcript = transcriptLines,
            TranscriptNotice = null
        };

        return Ok(report);
    }
}

public class ReportDto
{
    public SessionInfoDto Session { get; set; } = new();
    public ScoreCardReadDto? ScoreCard { get; set; }
    public List<PatternDto> Patterns { get; set; } = [];
    public List<ReportQuestionDto> Questions { get; set; } = [];
    public Dictionary<string, List<DerivedPointDto>> DerivedSeries { get; set; } = [];
    public List<TranscriptLineDto> Transcript { get; set; } = [];
    public string? TranscriptNotice { get; set; }
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

public class ReportQuestionDto
{
    public Guid Id { get; set; }
    public int Order { get; set; }
    public string Prompt { get; set; } = string.Empty;

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? AudioUrl { get; set; }

    [JsonIgnore(Condition = JsonIgnoreCondition.Never)]
    public string? ScreenAudioUrl { get; set; }

    public long? StartMs { get; set; }
    public long? EndMs { get; set; }
    public DateTime CreatedAt { get; set; }

    /// <summary>Transcript segments belonging to this question.</summary>
    public List<TranscriptLineDto> Transcript { get; set; } = [];

    /// <summary>Per-question metric time series (eyeContact, posture, fidget, headJitter).</summary>
    public Dictionary<string, List<DerivedPointDto>> Metrics { get; set; } = [];
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
