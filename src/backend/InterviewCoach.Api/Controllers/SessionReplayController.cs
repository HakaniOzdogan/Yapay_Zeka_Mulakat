using System.Text.Json;
using InterviewCoach.Api.Services;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/sessions")]
[Authorize]
public class SessionReplayController : ControllerBase
{
    private const int MaxMetricEvents = 200_000;
    private const int MaxTranscriptSegments = 50_000;
    private const int MaxEventPayloadBytes = 50 * 1024;
    private const int InsertChunkSize = 5000;

    private readonly ApplicationDbContext _db;
    private readonly IScoringService _scoringService;

    public SessionReplayController(ApplicationDbContext db, IScoringService scoringService)
    {
        _db = db;
        _scoringService = scoringService;
    }

    [HttpGet("{sessionId:guid}/replay/export")]
    [SessionOwnership]
    public async Task<ActionResult<SessionReplayDto>> Export(Guid sessionId, CancellationToken cancellationToken)
    {
        var session = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new
            {
                s.SelectedRole,
                s.Language,
                s.SettingsJson,
                s.CreatedAt
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (session == null)
            return NotFound();

        var metricEvents = await _db.MetricEvents
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.TsMs)
            .ThenBy(e => e.CreatedAt)
            .Select(e => new SessionReplayMetricEventDto
            {
                ClientEventId = e.ClientEventId,
                TsMs = e.TsMs,
                Source = e.Source,
                Type = e.Type,
                PayloadJson = e.PayloadJson
            })
            .ToListAsync(cancellationToken);

        var transcriptSegments = await _db.TranscriptSegments
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .OrderBy(t => t.StartMs)
            .ThenBy(t => t.EndMs)
            .Select(t => new SessionReplayTranscriptSegmentDto
            {
                ClientSegmentId = t.ClientSegmentId,
                StartMs = t.StartMs,
                EndMs = t.EndMs,
                Text = t.Text,
                Confidence = t.Confidence
            })
            .ToListAsync(cancellationToken);

        var replay = new SessionReplayDto
        {
            Version = 1,
            ExportedAtUtc = DateTime.UtcNow,
            Session = new SessionReplaySessionDto
            {
                Role = session.SelectedRole,
                Language = session.Language,
                Mode = TryExtractMode(session.SettingsJson),
                SettingsJson = session.SettingsJson,
                CreatedAtUtc = session.CreatedAt
            },
            MetricEvents = metricEvents,
            TranscriptSegments = transcriptSegments
        };

        return Ok(replay);
    }

    [HttpPost("replay/import")]
    [Authorize(Policy = "AdminOnly")]
    [ProducesResponseType(typeof(SessionReplayImportResponseDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<SessionReplayImportResponseDto>> Import(
        [FromBody] SessionReplayDto replay,
        CancellationToken cancellationToken)
    {
        if (!User.TryGetCurrentUserId(out var currentUserId))
            return this.UnauthorizedProblem("Invalid authenticated user context.");

        var validationErrors = ValidateReplay(replay);
        if (validationErrors.Count > 0)
            return BadRequest(new { errors = validationErrors });

        var newSession = new Session
        {
            Id = Guid.NewGuid(),
            CreatedAt = replay.Session.CreatedAtUtc,
            SelectedRole = replay.Session.Role ?? string.Empty,
            Language = string.IsNullOrWhiteSpace(replay.Session.Language) ? "tr" : replay.Session.Language,
            SettingsJson = string.IsNullOrWhiteSpace(replay.Session.SettingsJson) ? "{}" : replay.Session.SettingsJson,
            Status = "Created",
            StatsJson = "{}",
            UserId = currentUserId
        };

        var orderedEvents = replay.MetricEvents
            .OrderBy(e => e.TsMs)
            .ThenBy(e => e.ClientEventId)
            .Select(e => new MetricEvent
            {
                Id = Guid.NewGuid(),
                SessionId = newSession.Id,
                ClientEventId = e.ClientEventId,
                TsMs = e.TsMs,
                Source = string.IsNullOrWhiteSpace(e.Source) ? "Unknown" : e.Source,
                Type = e.Type ?? string.Empty,
                PayloadJson = e.PayloadJson,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        var orderedSegments = replay.TranscriptSegments
            .OrderBy(t => t.StartMs)
            .ThenBy(t => t.EndMs)
            .ThenBy(t => t.ClientSegmentId)
            .Select(t => new TranscriptSegment
            {
                Id = Guid.NewGuid(),
                SessionId = newSession.Id,
                ClientSegmentId = t.ClientSegmentId,
                StartMs = t.StartMs,
                EndMs = t.EndMs,
                Text = t.Text,
                Confidence = t.Confidence,
                CreatedAt = DateTime.UtcNow
            })
            .ToList();

        await using var transaction = await _db.Database.BeginTransactionAsync(cancellationToken);

        _db.Sessions.Add(newSession);
        await _db.SaveChangesAsync(cancellationToken);

        await InsertChunkedAsync(orderedEvents, cancellationToken);
        await InsertChunkedAsync(orderedSegments, cancellationToken);

        await transaction.CommitAsync(cancellationToken);

        return Ok(new SessionReplayImportResponseDto
        {
            NewSessionId = newSession.Id,
            MetricEventsInserted = orderedEvents.Count,
            TranscriptSegmentsInserted = orderedSegments.Count
        });
    }

    [HttpPost("{newSessionId:guid}/replay/run")]
    [SessionOwnership("newSessionId")]
    public async Task<ActionResult<LegacyReportDto>> RunReplay(
        Guid newSessionId,
        [FromBody] SessionReplayRunRequestDto? request,
        CancellationToken cancellationToken)
    {
        _ = request?.Speed;

        var session = await _db.Sessions
            .Include(s => s.TranscriptSegments)
            .Include(s => s.MetricEvents)
            .FirstOrDefaultAsync(s => s.Id == newSessionId, cancellationToken);

        if (session == null)
            return NotFound();

        var stats = ParseStats(session.StatsJson);
        var scoreCard = _scoringService.ComputeScoreCard(session, session.MetricEvents.ToList(), stats);
        var feedbackItems = _scoringService.GenerateFeedback(session, scoreCard, session.MetricEvents.ToList(), stats);

        if (scoreCard != null)
        {
            _db.ScoreCards.Add(scoreCard);
        }

        _db.FeedbackItems.AddRange(feedbackItems);
        session.Status = "Completed";

        await _db.SaveChangesAsync(cancellationToken);

        var report = new LegacyReportDto
        {
            SessionId = newSessionId,
            ScoreCard = scoreCard != null ? new LegacyScoreCardDto(scoreCard) : null,
            FeedbackItems = feedbackItems.Select(f => new LegacyFeedbackItemDto(f)).ToList()
        };

        return Ok(report);
    }

    private async Task InsertChunkedAsync<TEntity>(List<TEntity> entities, CancellationToken cancellationToken)
        where TEntity : class
    {
        if (entities.Count == 0)
            return;

        for (var i = 0; i < entities.Count; i += InsertChunkSize)
        {
            var count = Math.Min(InsertChunkSize, entities.Count - i);
            var chunk = entities.GetRange(i, count);

            _db.Set<TEntity>().AddRange(chunk);
            await _db.SaveChangesAsync(cancellationToken);
        }
    }

    private static List<string> ValidateReplay(SessionReplayDto replay)
    {
        var errors = new List<string>();

        if (replay.Version != 1)
            errors.Add("version must be 1.");

        if (replay.Session == null)
            errors.Add("session is required.");

        if (replay.MetricEvents.Count > MaxMetricEvents)
            errors.Add($"metricEvents exceeds max limit of {MaxMetricEvents}.");

        if (replay.TranscriptSegments.Count > MaxTranscriptSegments)
            errors.Add($"transcriptSegments exceeds max limit of {MaxTranscriptSegments}.");

        var duplicateMetricClientIds = replay.MetricEvents
            .GroupBy(e => e.ClientEventId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Take(5)
            .ToList();

        if (duplicateMetricClientIds.Count > 0)
            errors.Add("metricEvents contains duplicate clientEventId values.");

        var duplicateTranscriptClientIds = replay.TranscriptSegments
            .GroupBy(s => s.ClientSegmentId)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .Take(5)
            .ToList();

        if (duplicateTranscriptClientIds.Count > 0)
            errors.Add("transcriptSegments contains duplicate clientSegmentId values.");

        for (var i = 0; i < replay.MetricEvents.Count; i++)
        {
            var evt = replay.MetricEvents[i];
            if (string.IsNullOrWhiteSpace(evt.Type))
                errors.Add($"metricEvents[{i}].type is required.");

            if (string.IsNullOrWhiteSpace(evt.Source))
                errors.Add($"metricEvents[{i}].source is required.");

            if (string.IsNullOrWhiteSpace(evt.PayloadJson))
                errors.Add($"metricEvents[{i}].payloadJson is required.");

            if (!string.IsNullOrWhiteSpace(evt.PayloadJson) && System.Text.Encoding.UTF8.GetByteCount(evt.PayloadJson) > MaxEventPayloadBytes)
                errors.Add($"metricEvents[{i}].payloadJson exceeds 50KB.");

            if (!IsValidJson(evt.PayloadJson))
                errors.Add($"metricEvents[{i}].payloadJson must be valid JSON.");
        }

        for (var i = 0; i < replay.TranscriptSegments.Count; i++)
        {
            var segment = replay.TranscriptSegments[i];

            if (segment.EndMs < segment.StartMs)
                errors.Add($"transcriptSegments[{i}] has endMs < startMs.");

            if (segment.Text == null)
                errors.Add($"transcriptSegments[{i}].text is required.");
        }

        return errors;
    }

    private static bool IsValidJson(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return false;

        try
        {
            JsonDocument.Parse(input);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static string? TryExtractMode(string settingsJson)
    {
        if (string.IsNullOrWhiteSpace(settingsJson))
            return null;

        try
        {
            using var document = JsonDocument.Parse(settingsJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
                return null;

            if (document.RootElement.TryGetProperty("mode", out var modeElement) && modeElement.ValueKind == JsonValueKind.String)
                return modeElement.GetString();

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<string, object>? ParseStats(string statsJson)
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
                    {
                        stats[prop.Name] = intValue;
                    }
                    else if (prop.Value.TryGetSingle(out var floatValue))
                    {
                        stats[prop.Name] = floatValue;
                    }
                    else if (prop.Value.TryGetInt64(out var longValue))
                    {
                        stats[prop.Name] = longValue;
                    }
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

public class SessionReplayDto
{
    public int Version { get; set; } = 1;
    public DateTime ExportedAtUtc { get; set; } = DateTime.UtcNow;
    public SessionReplaySessionDto Session { get; set; } = new();
    public List<SessionReplayMetricEventDto> MetricEvents { get; set; } = [];
    public List<SessionReplayTranscriptSegmentDto> TranscriptSegments { get; set; } = [];
}

public class SessionReplaySessionDto
{
    public string? Role { get; set; }
    public string? Language { get; set; }
    public string? Mode { get; set; }
    public string SettingsJson { get; set; } = "{}";
    public DateTime CreatedAtUtc { get; set; }
}

public class SessionReplayMetricEventDto
{
    public Guid ClientEventId { get; set; }
    public long TsMs { get; set; }
    public string Source { get; set; } = string.Empty;
    public string Type { get; set; } = string.Empty;
    public string PayloadJson { get; set; } = "{}";
}

public class SessionReplayTranscriptSegmentDto
{
    public Guid ClientSegmentId { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public string Text { get; set; } = string.Empty;
    public double? Confidence { get; set; }
}

public class SessionReplayImportResponseDto
{
    public Guid NewSessionId { get; set; }
    public int MetricEventsInserted { get; set; }
    public int TranscriptSegmentsInserted { get; set; }
}

public class SessionReplayRunRequestDto
{
    public double Speed { get; set; } = 1.0;
}
