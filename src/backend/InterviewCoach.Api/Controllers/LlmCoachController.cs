using InterviewCoach.Api.Services;
using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using System.Text.Json;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/sessions/{sessionId:guid}/llm")]
[Authorize]
[SessionOwnership]
public class LlmCoachController : ControllerBase
{
    private const string CoachKind = "coach";

    private readonly ApplicationDbContext _db;
    private readonly ILlmCoachingOrchestrator _orchestrator;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<LlmCoachController> _logger;

    public LlmCoachController(
        ApplicationDbContext db,
        ILlmCoachingOrchestrator orchestrator,
        IServiceScopeFactory scopeFactory,
        ILogger<LlmCoachController> logger)
    {
        _db = db;
        _orchestrator = orchestrator;
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    /// <summary>
    /// Generates or returns cached AI coaching JSON for a session.
    /// </summary>
    /// <remarks>
    /// May return cached same-input output. If primary model fails, retry/fallback model/cache fallback paths can be used.
    /// </remarks>
    /// <response code="502">ProblemDetails payload. Failure summary: { title, status, detail, traceId, sourcePath, attempts, modelUsed, validationFailures, guardrailFailures, attemptedModels[] }.</response>
    [EnableRateLimiting("llm-coach")]
    [RequestSizeLimit(5 * 1024 * 1024)]
    [HttpPost("coach")]
    [ProducesResponseType(typeof(LlmCoachingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status502BadGateway)]
    public IActionResult Coach(Guid sessionId, [FromQuery] bool force = false)
    {
        var capturedId = sessionId;
        var capturedForce = force;
        var capturedScopeFactory = _scopeFactory;
        var capturedLogger = _logger;

        _ = Task.Run(async () =>
        {
            await Task.Delay(200);
            using var scope = capturedScopeFactory.CreateScope();
            var orchestrator = scope.ServiceProvider.GetRequiredService<ILlmCoachingOrchestrator>();
            try
            {
                var result = await orchestrator.ExecuteAsync(capturedId, capturedForce);
                capturedLogger.LogInformation(
                    "Background coaching completed for {sessionId}: success={success}, source={source}",
                    capturedId, result.Success, result.Metadata.SourcePath);
            }
            catch (Exception ex)
            {
                capturedLogger.LogWarning(ex, "Background coaching failed for session {sessionId}", capturedId);
            }
        });

        return Accepted(new { message = "Coaching generation started. Poll GET /llm/coach for result." });
    }

    /// <summary>
    /// Returns cached coaching result if it exists, without triggering generation.
    /// Returns 204 if not yet generated.
    /// </summary>
    [HttpGet("coach")]
    [ProducesResponseType(typeof(LlmCoachingResponse), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LlmCoachingResponse>> GetCachedCoach(Guid sessionId, CancellationToken cancellationToken)
    {
        var sessionExists = await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId, cancellationToken);
        if (!sessionExists)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        var latestRun = await _db.LlmRuns
            .AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.Kind == CoachKind)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (latestRun == null || string.IsNullOrWhiteSpace(latestRun.OutputJson))
            return NoContent();

        try
        {
            var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
            var response = JsonSerializer.Deserialize<LlmCoachingResponse>(latestRun.OutputJson, options);
            return response != null ? Ok(response) : NoContent();
        }
        catch
        {
            return NoContent();
        }
    }

    [HttpGet("coach/history")]
    [ProducesResponseType(typeof(List<LlmCoachHistoryItemDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<List<LlmCoachHistoryItemDto>>> GetHistory(Guid sessionId, [FromQuery] int take = 10, CancellationToken cancellationToken = default)
    {
        var sessionExists = await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId, cancellationToken);
        if (!sessionExists)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        take = Math.Clamp(take, 1, 100);

        var runs = await _db.LlmRuns
            .AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.Kind == CoachKind)
            .OrderByDescending(r => r.CreatedAt)
            .Take(take)
            .Select(r => new
            {
                r.Id,
                r.CreatedAt,
                r.Model,
                r.PromptVersion,
                r.InputHash,
                r.OutputJson
            })
            .ToListAsync(cancellationToken);

        var response = runs.Select(r => new LlmCoachHistoryItemDto
        {
            LlmRunId = r.Id,
            CreatedAtUtc = r.CreatedAt,
            Model = r.Model,
            PromptVersion = r.PromptVersion,
            InputHash = r.InputHash,
            Overall = TryReadOverall(r.OutputJson)
        }).ToList();

        return Ok(response);
    }

    [HttpGet("coach/optimization-preview")]
    [ProducesResponseType(typeof(LlmOptimizationPreviewDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    public async Task<ActionResult<LlmOptimizationPreviewDto>> GetOptimizationPreview(
        Guid sessionId,
        CancellationToken cancellationToken = default)
    {
        var preview = await _orchestrator.PreviewOptimizationAsync(sessionId, cancellationToken);
        if (preview == null)
        {
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");
        }

        return Ok(preview);
    }

    private static int? TryReadOverall(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("overall", out var overallElement) &&
                overallElement.ValueKind == JsonValueKind.Number &&
                overallElement.TryGetInt32(out var overall))
            {
                return overall;
            }
        }
        catch
        {
            return null;
        }

        return null;
    }
}

public class LlmCoachHistoryItemDto
{
    public Guid LlmRunId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public string Model { get; set; } = string.Empty;
    public string PromptVersion { get; set; } = string.Empty;
    public string InputHash { get; set; } = string.Empty;
    public int? Overall { get; set; }
}
