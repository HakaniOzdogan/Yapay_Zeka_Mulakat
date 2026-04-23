using System.Diagnostics;
using System.Text;
using System.Text.Json;
using InterviewCoach.Api.Services;
using InterviewCoach.Application;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Npgsql;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
[Authorize]
public class SessionsController : ControllerBase
{
    private const int MaxFieldLength = 64;

    private readonly ApplicationDbContext _db;
    private readonly ApiTelemetry _telemetry;
    private readonly ILogger<SessionsController> _logger;
    private readonly ITranscriptRedactionService _redactionService;
    private readonly ScoringProfilesOptions _scoringProfiles;

    public SessionsController(
        ApplicationDbContext db,
        ApiTelemetry telemetry,
        ILogger<SessionsController> logger,
        ITranscriptRedactionService redactionService,
        IOptions<ScoringProfilesOptions> scoringProfiles)
    {
        _db = db;
        _telemetry = telemetry;
        _logger = logger;
        _redactionService = redactionService;
        _scoringProfiles = scoringProfiles.Value;
    }

    /// <summary>
    /// Creates a new interview session.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(CreateSessionResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(CreateSessionResponse), StatusCodes.Status201Created)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<CreateSessionResponse>> CreateSession([FromBody] CreateSessionRequest request)
    {
        if (!User.TryGetCurrentUserId(out var currentUserId))
            return this.UnauthorizedProblem("Invalid authenticated user context.");

        var requestedProfile = string.IsNullOrWhiteSpace(request.ScoringProfile)
            ? _scoringProfiles.DefaultProfile
            : request.ScoringProfile.Trim();

        if (!_scoringProfiles.TryGetProfile(requestedProfile, out _))
        {
            return this.ValidationProblem(
                "One or more validation errors occurred.",
                new Dictionary<string, string[]>
                {
                    ["scoringProfile"] = [$"Unknown profile '{requestedProfile}'. Available: {string.Join(", ", _scoringProfiles.GetProfileNames())}"]
                });
        }

        var session = new Session
        {
            SelectedRole = request.Role ?? "Software Engineer",
            Language = request.Language ?? "tr",
            ScoringProfile = requestedProfile,
            UserId = currentUserId
        };

        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();

        var response = new CreateSessionResponse
        {
            SessionId = session.Id,
            CreatedAtUtc = session.CreatedAt
        };

        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, response);
    }

    [HttpGet("{id}")]
    [SessionOwnership("id")]
    public async Task<ActionResult<SessionDto>> GetSession(Guid id)
    {
        var session = await _db.Sessions
            .Include(s => s.Questions)
            .Include(s => s.ScoreCard)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (session == null)
            return this.NotFoundProblem($"Session '{id}' was not found.");

        return Ok(ToDto(session));
    }

    [HttpGet]
    public async Task<ActionResult<List<SessionDto>>> GetRecentSessions([FromQuery] int limit = 30)
    {
        if (!User.TryGetCurrentUserId(out var currentUserId))
            return this.UnauthorizedProblem("Invalid authenticated user context.");

        limit = Math.Clamp(limit, 1, 200);

        var sessions = await _db.Sessions
            .Include(s => s.ScoreCard)
            .Where(s => s.UserId == currentUserId)
            .OrderByDescending(s => s.CreatedAt)
            .Take(limit)
            .ToListAsync();

        return Ok(sessions.Select(ToDto).ToList());
    }

    [HttpGet("recent")]
    public async Task<ActionResult<List<SessionSummaryDto>>> GetRecent([FromQuery] int take = 20)
    {
        if (!User.TryGetCurrentUserId(out var currentUserId))
            return this.UnauthorizedProblem("Invalid authenticated user context.");

        take = Math.Clamp(take, 1, 100);

        var recent = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.UserId == currentUserId)
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => new SessionSummaryDto
            {
                Id = s.Id,
                CreatedAt = s.CreatedAt,
                Role = s.SelectedRole,
                Language = s.Language,
                Mode = null,
                Status = s.Status,
                OverallScore = _db.ScoreCards
                    .Where(sc => sc.SessionId == s.Id)
                    .Select(sc => (int?)sc.OverallScore)
                    .FirstOrDefault()
            })
            .Take(take)
            .ToListAsync();

        return Ok(recent);
    }

    /// <summary>
    /// Deletes a session and all related data.
    /// </summary>
    [HttpDelete("{sessionId:guid}")]
    [SessionOwnership]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> DeleteSession(Guid sessionId)
    {
        var sessionExists = await _db.Sessions.AnyAsync(s => s.Id == sessionId);
        if (!sessionExists)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        await using var tx = await _db.Database.BeginTransactionAsync();

        await _db.MetricEvents.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync();
        await _db.TranscriptSegments.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync();
        await TryDeleteLegacyRows(sessionId, "DerivedFeatures");
        await TryDeleteLegacyRows(sessionId, "PatternHits");
        await _db.ScoreCards.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync();
        await _db.LlmRuns.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync();
        await _db.FeedbackItems.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync();
        await _db.Questions.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync();
        await _db.Sessions.Where(x => x.Id == sessionId).ExecuteDeleteAsync();

        await tx.CommitAsync();
        return NoContent();
    }

    /// <summary>
    /// Transcript export is intentionally disabled in this build.
    /// </summary>
    [HttpGet("{sessionId:guid}/transcript/export")]
    [SessionOwnership]
    [Produces("text/plain")]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status410Gone)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<IActionResult> ExportTranscript(Guid sessionId)
    {
        var sessionExists = await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId);
        if (!sessionExists)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        var problem = new ProblemDetails
        {
            Title = "Transcript disabled",
            Status = StatusCodes.Status410Gone,
            Detail = "Transcript export is disabled in this build.",
            Type = "https://datatracker.ietf.org/doc/html/rfc7807"
        };
        problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
        return StatusCode(StatusCodes.Status410Gone, problem);
    }

    /// <summary>
    /// Ingests a batch of metric events.
    /// </summary>
    [EnableRateLimiting("events-batch")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    [HttpPost("{sessionId:guid}/events/batch")]
    [SessionOwnership]
    [ProducesResponseType(typeof(EventsBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<EventsBatchResponse>> IngestMetricEventsBatch(
        Guid sessionId,
        [FromBody] List<MetricEventIngestDto> events)
    {
        const int maxBatchSize = 2000;
        const int maxPayloadBytes = 50 * 1024;

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["route"] = "/api/sessions/{sessionId}/events/batch",
            ["requestId"] = HttpContext.TraceIdentifier
        });

        if (events.Count > maxBatchSize)
        {
            return this.ValidationProblem(
                "One or more validation errors occurred.",
                new Dictionary<string, string[]>
                {
                    ["events"] = [$"Max {maxBatchSize} events allowed per batch."]
                });
        }

        var sessionExists = await _db.Sessions.AnyAsync(s => s.Id == sessionId);
        if (!sessionExists)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        if (events.Count == 0)
            return Ok(new EventsBatchResponse { Inserted = 0, IgnoredDuplicates = 0 });

        var clientEventIds = events.Select(e => e.ClientEventId).Distinct().ToList();
        var existingClientEventIds = await _db.MetricEvents
            .Where(e => e.SessionId == sessionId && clientEventIds.Contains(e.ClientEventId))
            .Select(e => e.ClientEventId)
            .ToListAsync();

        var seenIds = existingClientEventIds.ToHashSet();
        var toInsert = new List<MetricEvent>(events.Count);
        var ignoredDuplicates = 0;
        long payloadBytesTotal = 0;

        foreach (var dto in events)
        {
            if (dto.TsMs < 0)
            {
                return this.ValidationProblem(
                    "One or more validation errors occurred.",
                    new Dictionary<string, string[]>
                    {
                        ["tsMs"] = ["tsMs must be non-negative."]
                    });
            }

            var source = (dto.Source ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(source) || source.Length > MaxFieldLength)
            {
                return this.ValidationProblem(
                    "One or more validation errors occurred.",
                    new Dictionary<string, string[]>
                    {
                        ["source"] = ["source is required and must be <= 64 chars."]
                    });
            }

            var type = (dto.Type ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(type) || type.Length > MaxFieldLength)
            {
                return this.ValidationProblem(
                    "One or more validation errors occurred.",
                    new Dictionary<string, string[]>
                    {
                        ["type"] = ["type is required and must be <= 64 chars."]
                    });
            }

            var payloadJson = dto.Payload.ValueKind == JsonValueKind.Undefined
                ? "{}"
                : dto.Payload.GetRawText();

            var payloadBytes = Encoding.UTF8.GetByteCount(payloadJson);
            payloadBytesTotal += payloadBytes;
            _telemetry.EventPayloadBytes.Record(
                payloadBytes,
                new KeyValuePair<string, object?>("source", source),
                new KeyValuePair<string, object?>("type", type));

            if (payloadBytes > maxPayloadBytes)
            {
                _logger.LogWarning("Rejected metric event payload over limit: payloadBytes={payloadBytes}", payloadBytes);
                return this.ValidationProblem(
                    "One or more validation errors occurred.",
                    new Dictionary<string, string[]>
                    {
                        ["payload"] = ["Payload exceeds 50KB per event."]
                    });
            }

            if (!seenIds.Add(dto.ClientEventId))
            {
                ignoredDuplicates++;
                continue;
            }

            toInsert.Add(new MetricEvent
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                ClientEventId = dto.ClientEventId,
                TsMs = dto.TsMs,
                Source = source,
                Type = type,
                PayloadJson = payloadJson,
                CreatedAt = DateTime.UtcNow
            });
        }

        var sw = Stopwatch.StartNew();
        using var activity = _telemetry.ActivitySource.StartActivity("events.batch.db_insert", ActivityKind.Internal);
        activity?.SetTag("session.id", sessionId.ToString());

        if (toInsert.Count > 0)
        {
            _db.MetricEvents.AddRange(toInsert);
            await _db.SaveChangesAsync();
        }

        sw.Stop();

        var groupedInserted = toInsert
            .GroupBy(e => new { e.Source, e.Type })
            .Select(g => new { g.Key.Source, g.Key.Type, Count = g.LongCount() })
            .ToList();

        foreach (var group in groupedInserted)
        {
            _telemetry.EventsInsertedTotal.Add(
                group.Count,
                new KeyValuePair<string, object?>("source", group.Source),
                new KeyValuePair<string, object?>("type", group.Type));
        }

        var groupedDuplicates = events
            .GroupBy(e => new { Source = (e.Source ?? string.Empty).Trim(), Type = (e.Type ?? string.Empty).Trim() })
            .Select(g => new
            {
                g.Key.Source,
                g.Key.Type,
                Count = g.LongCount() - groupedInserted.Where(gi => gi.Source == g.Key.Source && gi.Type == g.Key.Type).Select(gi => gi.Count).FirstOrDefault()
            })
            .Where(g => g.Count > 0)
            .ToList();

        foreach (var group in groupedDuplicates)
        {
            _telemetry.EventsDuplicatesTotal.Add(
                group.Count,
                new KeyValuePair<string, object?>("source", group.Source),
                new KeyValuePair<string, object?>("type", group.Type));
        }

        activity?.SetTag("inserted.count", toInsert.Count);
        activity?.SetTag("duplicates.count", ignoredDuplicates);
        activity?.SetTag("durationMs", sw.Elapsed.TotalMilliseconds);

        _logger.LogInformation(
            "Metric batch ingest summary: inserted={inserted}, duplicates={duplicates}, payloadBytes={payloadBytes}",
            toInsert.Count,
            ignoredDuplicates,
            payloadBytesTotal);

        return Ok(new EventsBatchResponse
        {
            Inserted = toInsert.Count,
            IgnoredDuplicates = ignoredDuplicates
        });
    }

    private SessionDto ToDto(Session session)
    {
        return new SessionDto
        {
            Id = session.Id,
            CreatedAt = session.CreatedAt,
            Status = session.Status,
            SelectedRole = session.SelectedRole,
            Language = session.Language,
            ScoringProfile = session.ScoringProfile,
            OverallScore = session.ScoreCard?.OverallScore
        };
    }

    private async Task TryDeleteLegacyRows(Guid sessionId, string tableName)
    {
        try
        {
            switch (tableName)
            {
                case "DerivedFeatures":
                    await _db.Database.ExecuteSqlRawAsync(@"DELETE FROM ""DerivedFeatures"" WHERE ""SessionId"" = {0}", sessionId);
                    break;
                case "PatternHits":
                    await _db.Database.ExecuteSqlRawAsync(@"DELETE FROM ""PatternHits"" WHERE ""SessionId"" = {0}", sessionId);
                    break;
                default:
                    return;
            }
        }
        catch (PostgresException ex) when (ex.SqlState == "42P01")
        {
            _logger.LogDebug("Table {tableName} not found while deleting session {sessionId}", tableName, sessionId);
        }
    }
}

public class CreateSessionRequest
{
    /// <summary>Target interview role.</summary>
    /// <example>Software Engineer</example>
    public string? Role { get; set; }

    /// <summary>Language code.</summary>
    /// <example>en</example>
    public string? Language { get; set; }

    /// <summary>Optional scoring profile name.</summary>
    /// <example>general</example>
    public string? ScoringProfile { get; set; }
}

public class CreateSessionResponse
{
    /// <summary>Newly created session identifier.</summary>
    public Guid SessionId { get; set; }

    /// <summary>Session creation timestamp in UTC.</summary>
    public DateTime CreatedAtUtc { get; set; }
}

public class SessionDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string SelectedRole { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public string? ScoringProfile { get; set; }
    public int? OverallScore { get; set; }
}

public class MetricEventIngestDto
{
    /// <summary>Idempotency key for the event.</summary>
    /// <example>18f6d88a-5528-4ae1-bf87-18bb2e14e8b3</example>
    public Guid ClientEventId { get; set; }

    /// <summary>Event timestamp in milliseconds.</summary>
    /// <example>1500</example>
    public long TsMs { get; set; }

    /// <summary>Metric source.</summary>
    /// <example>Vision</example>
    public string Source { get; set; } = string.Empty;

    /// <summary>Metric type identifier.</summary>
    /// <example>vision_metrics_v1</example>
    public string Type { get; set; } = string.Empty;

    /// <summary>Raw metric payload JSON object.</summary>
    public JsonElement Payload { get; set; }
}

public class EventsBatchResponse
{
    /// <summary>Number of inserted events.</summary>
    public int Inserted { get; set; }

    /// <summary>Number of duplicate events ignored by idempotency.</summary>
    public int IgnoredDuplicates { get; set; }
}

public class SessionSummaryDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string? Role { get; set; }
    public string? Language { get; set; }
    public string? Mode { get; set; }
    public int? OverallScore { get; set; }
    public string? Status { get; set; }
}
