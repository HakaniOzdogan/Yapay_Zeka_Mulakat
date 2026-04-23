using InterviewCoach.Api.Services;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/sessions/{sessionId:guid}/transcript")]
[Authorize]
[SessionOwnership]
public class TranscriptController : ControllerBase
{
    private const int MaxBatchSize = 500;

    private readonly ApplicationDbContext _db;
    private readonly ILogger<TranscriptController> _logger;

    public TranscriptController(ApplicationDbContext db, ILogger<TranscriptController> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>
    /// Ingests a batch of transcript segments (idempotent — duplicates are silently ignored).
    /// </summary>
    [EnableRateLimiting("transcript-batch")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    [HttpPost("batch")]
    [ProducesResponseType(typeof(TranscriptBatchResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<TranscriptBatchResponse>> IngestTranscriptBatch(
        Guid sessionId,
        [FromBody] List<TranscriptSegmentIngestDto> segments,
        CancellationToken cancellationToken)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["route"] = "/api/sessions/{sessionId}/transcript/batch",
            ["requestId"] = HttpContext.TraceIdentifier
        });

        if (segments.Count > MaxBatchSize)
        {
            return this.ValidationProblem(
                "One or more validation errors occurred.",
                new Dictionary<string, string[]>
                {
                    ["segments"] = [$"Max {MaxBatchSize} segments allowed per batch."]
                });
        }

        var sessionExists = await _db.Sessions.AnyAsync(s => s.Id == sessionId, cancellationToken);
        if (!sessionExists)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        if (segments.Count == 0)
            return Ok(new TranscriptBatchResponse { Inserted = 0, IgnoredDuplicates = 0 });

        var clientSegmentIds = segments.Select(s => s.ClientSegmentId).Distinct().ToList();
        var existingIds = await _db.TranscriptSegments
            .Where(t => t.SessionId == sessionId && clientSegmentIds.Contains(t.ClientSegmentId))
            .Select(t => t.ClientSegmentId)
            .ToListAsync(cancellationToken);

        var seenIds = existingIds.ToHashSet();
        var toInsert = new List<TranscriptSegment>(segments.Count);
        var ignoredDuplicates = 0;

        foreach (var dto in segments)
        {
            if (dto.StartMs < 0 || dto.EndMs < dto.StartMs)
            {
                return this.ValidationProblem(
                    "One or more validation errors occurred.",
                    new Dictionary<string, string[]>
                    {
                        ["startMs"] = ["startMs must be non-negative and endMs must be >= startMs."]
                    });
            }

            if (!seenIds.Add(dto.ClientSegmentId))
            {
                ignoredDuplicates++;
                continue;
            }

            toInsert.Add(new TranscriptSegment
            {
                Id = Guid.NewGuid(),
                SessionId = sessionId,
                ClientSegmentId = dto.ClientSegmentId,
                StartMs = dto.StartMs,
                EndMs = dto.EndMs,
                Text = (dto.Text ?? string.Empty).Trim(),
                Confidence = dto.Confidence,
                CreatedAt = DateTime.UtcNow
            });
        }

        if (toInsert.Count > 0)
        {
            _db.TranscriptSegments.AddRange(toInsert);
            await _db.SaveChangesAsync(cancellationToken);
        }

        _logger.LogInformation(
            "Transcript batch ingest: inserted={inserted}, duplicates={duplicates}",
            toInsert.Count,
            ignoredDuplicates);

        return Ok(new TranscriptBatchResponse
        {
            Inserted = toInsert.Count,
            IgnoredDuplicates = ignoredDuplicates,
            MergedOutputCount = toInsert.Count
        });
    }
}

public class TranscriptSegmentIngestDto
{
    /// <summary>Idempotency key for the segment.</summary>
    /// <example>2dc0c003-4f51-43ea-95b2-3b44045e9fc4</example>
    public Guid ClientSegmentId { get; set; }

    /// <summary>Segment start timestamp in milliseconds.</summary>
    /// <example>1500</example>
    public long StartMs { get; set; }

    /// <summary>Segment end timestamp in milliseconds.</summary>
    /// <example>2200</example>
    public long EndMs { get; set; }

    /// <summary>Recognized transcript text.</summary>
    /// <example>Hello, my name is Alex.</example>
    public string Text { get; set; } = string.Empty;

    /// <summary>Optional confidence score from ASR.</summary>
    /// <example>0.92</example>
    public double? Confidence { get; set; }
}

public class TranscriptBatchResponse
{
    /// <summary>Number of inserted transcript segments.</summary>
    public int Inserted { get; set; }

    /// <summary>Number of duplicate segments ignored by idempotency.</summary>
    public int IgnoredDuplicates { get; set; }

    /// <summary>Number of merged segments persisted in the affected time window.</summary>
    public int MergedOutputCount { get; set; }
}
