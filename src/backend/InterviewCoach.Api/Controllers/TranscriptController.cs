using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/sessions/{sessionId}/[controller]")]
public class TranscriptController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public TranscriptController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpPost]
    public async Task<ActionResult> PostTranscript(Guid sessionId, [FromBody] TranscriptRequest request)
    {
        var session = await _db.Sessions.FindAsync(sessionId);
        if (session == null)
            return NotFound();

        // Store transcript segments
        var segments = new List<TranscriptSegment>();
        foreach (var seg in request.Segments)
        {
            segments.Add(new TranscriptSegment
            {
                SessionId = sessionId,
                StartMs = seg.StartMs,
                EndMs = seg.EndMs,
                Text = seg.Text
            });
        }

        _db.TranscriptSegments.AddRange(segments);

        // Store stats in Session if provided
        if (request.Stats != null)
        {
            var statsDict = new Dictionary<string, object>
            {
                ["wpm"] = request.Stats.Wpm ?? 0,
                ["filler_count"] = request.Stats.FillerCount ?? 0,
                ["pause_count"] = request.Stats.PauseCount ?? 0,
                ["duration_ms"] = request.Stats.DurationMs ?? 0,
                ["word_count"] = request.Stats.WordCount ?? 0
            };

            session.StatsJson = JsonSerializer.Serialize(statsDict);
        }

        await _db.SaveChangesAsync();

        return Ok(new { count = segments.Count });
    }
}

public class TranscriptRequest
{
    public List<TranscriptSegmentData> Segments { get; set; } = [];
    public TranscriptStatsData? Stats { get; set; }
}

public class TranscriptSegmentData
{
    public int StartMs { get; set; }
    public int EndMs { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class TranscriptStatsData
{
    public long? DurationMs { get; set; }
    public int? WordCount { get; set; }
    public float? Wpm { get; set; }
    public int? FillerCount { get; set; }
    public int? PauseCount { get; set; }
}
