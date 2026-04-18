using System.Diagnostics;
using System.Text.RegularExpressions;
using InterviewCoach.Api.Services;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/sessions/{sessionId:guid}/transcript")]
[Authorize]
[SessionOwnership]
public class TranscriptController : ControllerBase
{
    private const int MaxBatchSize = 2000;
    private const int MaxTextLength = 4000;
    private const long MaxTimestampMs = 86_400_000;

    private readonly ApplicationDbContext _db;
    private readonly ApiTelemetry _telemetry;
    private readonly ILogger<TranscriptController> _logger;
    private readonly ITranscriptRedactionService _redactionService;
    private readonly PrivacyOptions _privacyOptions;

    public TranscriptController(
        ApplicationDbContext db,
        ApiTelemetry telemetry,
        ILogger<TranscriptController> logger,
        ITranscriptRedactionService redactionService,
        IOptions<PrivacyOptions> privacyOptions)
    {
        _db = db;
        _telemetry = telemetry;
        _logger = logger;
        _redactionService = redactionService;
        _privacyOptions = privacyOptions.Value;
    }

    /// <summary>
    /// Ingests and merges transcript segments for a session.
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
        [FromBody] List<TranscriptSegmentIngestDto> segments)
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

        var sessionExists = await _db.Sessions.AnyAsync(s => s.Id == sessionId);
        if (!sessionExists)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        if (segments.Count == 0)
        {
            return Ok(new TranscriptBatchResponse
            {
                Inserted = 0,
                IgnoredDuplicates = 0,
                MergedOutputCount = 0
            });
        }

        var normalized = new List<TranscriptSegmentCandidate>(segments.Count);
        foreach (var segment in segments)
        {
            if (segment.StartMs < 0 || segment.EndMs < 0 || segment.StartMs > MaxTimestampMs || segment.EndMs > MaxTimestampMs)
            {
                return this.ValidationProblem(
                    "One or more validation errors occurred.",
                    new Dictionary<string, string[]>
                    {
                        ["startMs"] = ["startMs and endMs must be within 0..86400000."],
                        ["endMs"] = ["startMs and endMs must be within 0..86400000."]
                    });
            }

            if (segment.EndMs < segment.StartMs)
            {
                return this.ValidationProblem(
                    "One or more validation errors occurred.",
                    new Dictionary<string, string[]>
                    {
                        ["endMs"] = ["endMs cannot be less than startMs."]
                    });
            }

            var text = NormalizeText(segment.Text);
            if (string.IsNullOrWhiteSpace(text))
            {
                return this.ValidationProblem(
                    "One or more validation errors occurred.",
                    new Dictionary<string, string[]>
                    {
                        ["text"] = ["text cannot be empty."]
                    });
            }

            if (text.Length > MaxTextLength)
            {
                return this.ValidationProblem(
                    "One or more validation errors occurred.",
                    new Dictionary<string, string[]>
                    {
                        ["text"] = [$"Text cannot exceed {MaxTextLength} characters."]
                    });
            }

            text = ApplyRedactionOnIngest(text);

            normalized.Add(new TranscriptSegmentCandidate
            {
                ClientSegmentId = segment.ClientSegmentId,
                StartMs = segment.StartMs,
                EndMs = segment.EndMs,
                Text = text,
                Confidence = segment.Confidence,
                ConfidenceSum = segment.Confidence ?? 0,
                ConfidenceCount = segment.Confidence.HasValue ? 1 : 0
            });
        }

        var inputClientIds = normalized.Select(s => s.ClientSegmentId).Distinct().ToList();
        var existingClientIds = await _db.TranscriptSegments
            .Where(s => s.SessionId == sessionId && inputClientIds.Contains(s.ClientSegmentId))
            .Select(s => s.ClientSegmentId)
            .ToListAsync();

        var seenClientIds = existingClientIds.ToHashSet();
        var newIncoming = new List<TranscriptSegmentCandidate>(normalized.Count);
        var ignoredDuplicates = 0;

        foreach (var candidate in normalized)
        {
            if (!seenClientIds.Add(candidate.ClientSegmentId))
            {
                ignoredDuplicates++;
                continue;
            }

            newIncoming.Add(candidate);
        }

        if (newIncoming.Count == 0)
        {
            _telemetry.TranscriptDuplicatesTotal.Add(ignoredDuplicates);
            _logger.LogInformation(
                "Transcript batch summary: inserted={inserted}, duplicates={duplicates}, mergedOutputCount={mergedOutputCount}",
                0,
                ignoredDuplicates,
                0);
            return Ok(new TranscriptBatchResponse
            {
                Inserted = 0,
                IgnoredDuplicates = ignoredDuplicates,
                MergedOutputCount = 0
            });
        }

        var minStart = newIncoming.Min(s => s.StartMs);
        var maxEnd = newIncoming.Max(s => s.EndMs);
        var windowStart = Math.Max(0, minStart - 1000);
        var windowEnd = maxEnd + 1000;

        var existingOverlapping = await _db.TranscriptSegments
            .Where(s => s.SessionId == sessionId && s.EndMs >= windowStart && s.StartMs <= windowEnd)
            .ToListAsync();

        var combined = existingOverlapping
            .Select(s => new TranscriptSegmentCandidate
            {
                ClientSegmentId = s.ClientSegmentId,
                StartMs = s.StartMs,
                EndMs = s.EndMs,
                Text = ApplyRedactionOnIngest(NormalizeText(s.Text)),
                Confidence = s.Confidence,
                ConfidenceSum = s.Confidence ?? 0,
                ConfidenceCount = s.Confidence.HasValue ? 1 : 0
            })
            .Concat(newIncoming)
            .OrderBy(s => s.StartMs)
            .ThenBy(s => s.EndMs)
            .ToList();

        var merged = MergeCandidates(combined);

        var sw = Stopwatch.StartNew();
        using var activity = _telemetry.ActivitySource.StartActivity("transcript.batch.merge_transaction", ActivityKind.Internal);
        activity?.SetTag("session.id", sessionId.ToString());

        await using var tx = await _db.Database.BeginTransactionAsync();

        _db.TranscriptSegments.RemoveRange(existingOverlapping);

        var mergedEntities = merged.Select(m => new TranscriptSegment
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ClientSegmentId = m.ClientSegmentId,
            StartMs = m.StartMs,
            EndMs = m.EndMs,
            Text = m.Text,
            Confidence = m.ConfidenceCount == 0 ? null : m.ConfidenceSum / m.ConfidenceCount,
            CreatedAt = DateTime.UtcNow
        }).ToList();

        _db.TranscriptSegments.AddRange(mergedEntities);
        await _db.SaveChangesAsync();
        await tx.CommitAsync();

        sw.Stop();

        _telemetry.TranscriptInsertedTotal.Add(newIncoming.Count);
        _telemetry.TranscriptDuplicatesTotal.Add(ignoredDuplicates);

        activity?.SetTag("inserted.count", newIncoming.Count);
        activity?.SetTag("duplicates.count", ignoredDuplicates);
        activity?.SetTag("durationMs", sw.Elapsed.TotalMilliseconds);

        _logger.LogInformation(
            "Transcript batch summary: inserted={inserted}, duplicates={duplicates}, mergedOutputCount={mergedOutputCount}",
            newIncoming.Count,
            ignoredDuplicates,
            mergedEntities.Count);

        return Ok(new TranscriptBatchResponse
        {
            Inserted = newIncoming.Count,
            IgnoredDuplicates = ignoredDuplicates,
            MergedOutputCount = mergedEntities.Count
        });
    }

    private string ApplyRedactionOnIngest(string text)
    {
        if (!_privacyOptions.RedactTranscripts || !_privacyOptions.RedactOnIngest)
            return text;

        return _redactionService.Redact(text);
    }

    private static List<TranscriptSegmentCandidate> MergeCandidates(List<TranscriptSegmentCandidate> ordered)
    {
        if (ordered.Count == 0)
            return [];

        var result = new List<TranscriptSegmentCandidate>();
        var current = TranscriptSegmentCandidate.From(ordered[0]);

        for (var i = 1; i < ordered.Count; i++)
        {
            var next = ordered[i];
            if (ShouldMerge(current, next))
            {
                current = Merge(current, next);
                continue;
            }

            result.Add(current);
            current = TranscriptSegmentCandidate.From(next);
        }

        result.Add(current);
        return result;
    }

    private static bool ShouldMerge(TranscriptSegmentCandidate current, TranscriptSegmentCandidate next)
    {
        if (next.StartMs <= current.EndMs)
            return true;

        var gap = next.StartMs - current.EndMs;
        return gap <= 250 && LooksContinuous(current.Text, next.Text);
    }

    private static bool LooksContinuous(string currentText, string nextText)
    {
        if (string.IsNullOrWhiteSpace(currentText) || string.IsNullOrWhiteSpace(nextText))
            return false;

        var endsWithSentenceEnd = currentText.EndsWith('.') || currentText.EndsWith('!') || currentText.EndsWith('?');
        if (endsWithSentenceEnd)
            return false;

        var firstLetter = nextText.FirstOrDefault(char.IsLetter);
        return firstLetter != default && char.IsLower(firstLetter);
    }

    private static TranscriptSegmentCandidate Merge(TranscriptSegmentCandidate current, TranscriptSegmentCandidate next)
    {
        var mergedText = string.IsNullOrWhiteSpace(current.Text)
            ? next.Text
            : string.IsNullOrWhiteSpace(next.Text)
                ? current.Text
                : $"{current.Text} {next.Text}";

        mergedText = NormalizeText(mergedText);

        return new TranscriptSegmentCandidate
        {
            ClientSegmentId = current.ClientSegmentId,
            StartMs = Math.Min(current.StartMs, next.StartMs),
            EndMs = Math.Max(current.EndMs, next.EndMs),
            Text = mergedText,
            Confidence = null,
            ConfidenceSum = current.ConfidenceSum + next.ConfidenceSum,
            ConfidenceCount = current.ConfidenceCount + next.ConfidenceCount
        };
    }

    private static string NormalizeText(string text)
    {
        var trimmed = (text ?? string.Empty).Trim();
        return Regex.Replace(trimmed, "\\s+", " ");
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

public class TranscriptSegmentCandidate
{
    public Guid ClientSegmentId { get; set; }
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public string Text { get; set; } = string.Empty;
    public double? Confidence { get; set; }
    public double ConfidenceSum { get; set; }
    public int ConfidenceCount { get; set; }

    public static TranscriptSegmentCandidate From(TranscriptSegmentCandidate source)
    {
        return new TranscriptSegmentCandidate
        {
            ClientSegmentId = source.ClientSegmentId,
            StartMs = source.StartMs,
            EndMs = source.EndMs,
            Text = source.Text,
            Confidence = source.Confidence,
            ConfidenceSum = source.ConfidenceSum,
            ConfidenceCount = source.ConfidenceCount
        };
    }
}
