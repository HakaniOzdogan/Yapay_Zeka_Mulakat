using System.Text.Json;
using InterviewCoach.Application;
using InterviewCoach.Api.Services;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api")]
[Authorize]
public class ScoringController : ControllerBase
{
    private readonly ApplicationDbContext _db;
    private readonly IScoringService _scoringService;
    private readonly ScoringProfilesOptions _scoringProfiles;

    public ScoringController(
        ApplicationDbContext db,
        IScoringService scoringService,
        IOptions<ScoringProfilesOptions> scoringProfiles)
    {
        _db = db;
        _scoringService = scoringService;
        _scoringProfiles = scoringProfiles.Value;
    }

    /// <summary>
    /// Returns configured scoring profiles.
    /// </summary>
    [HttpGet("scoring/profiles")]
    [ProducesResponseType(typeof(ScoringProfilesResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public ActionResult<ScoringProfilesResponse> GetProfiles()
    {
        return Ok(new ScoringProfilesResponse
        {
            DefaultProfile = _scoringProfiles.DefaultProfile,
            Profiles = _scoringProfiles.Profiles
        });
    }

    /// <summary>
    /// Computes scorecard preview using a requested scoring profile without modifying stored scorecards.
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/scoring/preview")]
    [SessionOwnership]
    [ProducesResponseType(typeof(ScoringPreviewResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<ScoringPreviewResponse>> Preview(Guid sessionId, [FromBody] ScoringProfileRequest request)
    {
        var profileName = (request.ProfileName ?? string.Empty).Trim();
        if (!_scoringProfiles.TryGetProfile(profileName, out _))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Status = StatusCodes.Status400BadRequest,
                Detail = $"Unknown profile '{profileName}'. Available profiles: {string.Join(", ", _scoringProfiles.GetProfileNames())}"
            });
        }

        var session = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new Session
            {
                Id = s.Id,
                CreatedAt = s.CreatedAt,
                Status = s.Status,
                SelectedRole = s.SelectedRole,
                Language = s.Language,
                SettingsJson = s.SettingsJson,
                StatsJson = s.StatsJson,
                ScoringProfile = profileName
            })
            .FirstOrDefaultAsync();

        if (session == null)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        var metrics = await _db.MetricEvents
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .ToListAsync();

        var stats = ParseStats(session.StatsJson);
        var preview = _scoringService.ComputeScoreCard(session, metrics, stats);

        var current = await _db.ScoreCards
            .AsNoTracking()
            .Where(sc => sc.SessionId == sessionId)
            .OrderByDescending(sc => sc.Id)
            .Select(sc => new LegacyScoreCardDto
            {
                EyeContactScore = sc.EyeContactScore,
                SpeakingRateScore = sc.SpeakingRateScore,
                FillerScore = sc.FillerScore,
                PostureScore = sc.PostureScore,
                OverallScore = sc.OverallScore
            })
            .FirstOrDefaultAsync();

        return Ok(new ScoringPreviewResponse
        {
            SessionId = sessionId,
            ProfileName = profileName,
            ScoreCardPreview = new LegacyScoreCardDto(preview),
            CurrentStoredScoreCard = current
        });
    }

    /// <summary>
    /// Updates session scoring profile without finalizing.
    /// </summary>
    [HttpPost("sessions/{sessionId:guid}/scoring/profile")]
    [SessionOwnership]
    [ProducesResponseType(typeof(SessionScoringProfileResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<SessionScoringProfileResponse>> SetSessionProfile(Guid sessionId, [FromBody] ScoringProfileRequest request)
    {
        var profileName = (request.ProfileName ?? string.Empty).Trim();
        if (!_scoringProfiles.TryGetProfile(profileName, out _))
        {
            return BadRequest(new ProblemDetails
            {
                Title = "Validation failed",
                Status = StatusCodes.Status400BadRequest,
                Detail = $"Unknown profile '{profileName}'. Available profiles: {string.Join(", ", _scoringProfiles.GetProfileNames())}"
            });
        }

        var session = await _db.Sessions.FirstOrDefaultAsync(s => s.Id == sessionId);
        if (session == null)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        session.ScoringProfile = profileName;
        await _db.SaveChangesAsync();

        return Ok(new SessionScoringProfileResponse
        {
            SessionId = sessionId,
            ScoringProfile = profileName
        });
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

public class ScoringProfilesResponse
{
    public string DefaultProfile { get; set; } = "general";
    public Dictionary<string, ScoringProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

public class ScoringProfileRequest
{
    public string ProfileName { get; set; } = string.Empty;
}

public class ScoringPreviewResponse
{
    public Guid SessionId { get; set; }
    public string ProfileName { get; set; } = string.Empty;
    public LegacyScoreCardDto ScoreCardPreview { get; set; } = new();
    public LegacyScoreCardDto? CurrentStoredScoreCard { get; set; }
}

public class SessionScoringProfileResponse
{
    public Guid SessionId { get; set; }
    public string ScoringProfile { get; set; } = string.Empty;
}
