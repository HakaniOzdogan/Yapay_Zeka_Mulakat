using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using System.Text.Json;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/sessions/{sessionId}/[controller]")]
public class MetricsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public MetricsController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpPost]
    public async Task<ActionResult> PostMetrics(Guid sessionId, [FromBody] MetricsRequest request)
    {
        var session = await _db.Sessions.FindAsync(sessionId);
        if (session == null)
            return NotFound();

        var events = new List<MetricEvent>();
        foreach (var evt in request.Events)
        {
            events.Add(new MetricEvent
            {
                SessionId = sessionId,
                TimestampMs = evt.TimestampMs,
                Type = evt.Type,
                ValueJson = JsonSerializer.Serialize(evt.Value)
            });
        }

        _db.MetricEvents.AddRange(events);
        await _db.SaveChangesAsync();

        return Ok(new { count = events.Count });
    }
}

public class MetricsRequest
{
    public List<MetricEventData> Events { get; set; } = [];
}

public class MetricEventData
{
    public long TimestampMs { get; set; }
    public string Type { get; set; } = string.Empty;
    public object Value { get; set; } = new { };
}
