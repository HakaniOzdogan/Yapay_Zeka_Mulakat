using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class SessionsController : ControllerBase
{
    private readonly ApplicationDbContext _db;

    public SessionsController(ApplicationDbContext db)
    {
        _db = db;
    }

    [HttpPost]
    public async Task<ActionResult<SessionDto>> CreateSession([FromBody] CreateSessionRequest request)
    {
        var session = new Session
        {
            SelectedRole = request.Role ?? "Software Engineer",
            Language = request.Language ?? "tr"
        };

        _db.Sessions.Add(session);
        await _db.SaveChangesAsync();

        return CreatedAtAction(nameof(GetSession), new { id = session.Id }, ToDto(session));
    }

    [HttpGet("{id}")]
    public async Task<ActionResult<SessionDto>> GetSession(Guid id)
    {
        var session = await _db.Sessions
            .Include(s => s.Questions)
            .Include(s => s.ScoreCard)
            .FirstOrDefaultAsync(s => s.Id == id);

        if (session == null)
            return NotFound();

        return Ok(ToDto(session));
    }

    [HttpGet]
    public async Task<ActionResult<List<SessionDto>>> GetRecentSessions([FromQuery] int limit = 30)
    {
        limit = Math.Clamp(limit, 1, 200);

        var sessions = await _db.Sessions
            .Include(s => s.ScoreCard)
            .OrderByDescending(s => s.CreatedAt)
            .Take(limit)
            .ToListAsync();

        return Ok(sessions.Select(ToDto).ToList());
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
            OverallScore = session.ScoreCard?.OverallScore
        };
    }
}

public class CreateSessionRequest
{
    public string? Role { get; set; }
    public string? Language { get; set; }
}

public class SessionDto
{
    public Guid Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public string Status { get; set; } = string.Empty;
    public string SelectedRole { get; set; } = string.Empty;
    public string Language { get; set; } = string.Empty;
    public int? OverallScore { get; set; }
}
