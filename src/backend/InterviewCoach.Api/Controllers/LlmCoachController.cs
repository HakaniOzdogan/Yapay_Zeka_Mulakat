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
    private readonly ILogger<LlmCoachController> _logger;

    public LlmCoachController(
        ApplicationDbContext db,
        ILlmCoachingOrchestrator orchestrator,
        ILogger<LlmCoachController> logger)
    {
        _db = db;
        _orchestrator = orchestrator;
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
    public async Task<ActionResult<LlmCoachingResponse>> Coach(Guid sessionId, [FromQuery] bool force = false, CancellationToken cancellationToken = default)
    {
        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["sessionId"] = sessionId,
            ["route"] = "/api/sessions/{sessionId}/llm/coach",
            ["requestId"] = HttpContext.TraceIdentifier
        });

        var result = await _orchestrator.ExecuteAsync(sessionId, force, cancellationToken);

        if (result.NotFound)
        {
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");
        }

        if (!result.Success || result.Response == null)
        {
            var problem = new ProblemDetails
            {
                Title = "LLM orchestration failed",
                Status = StatusCodes.Status502BadGateway,
                Detail = result.ErrorMessage ?? "LLM orchestration failed.",
                Type = "https://datatracker.ietf.org/doc/html/rfc7807"
            };
            problem.Extensions["traceId"] = HttpContext.TraceIdentifier;
            problem.Extensions["sourcePath"] = result.Metadata.SourcePath;
            problem.Extensions["providerUsed"] = result.Metadata.ProviderUsed;
            problem.Extensions["attempts"] = result.Metadata.Attempts;
            problem.Extensions["modelUsed"] = result.Metadata.ModelUsed;
            problem.Extensions["reasoningEffort"] = result.Metadata.ReasoningEffort;
            problem.Extensions["requestSourcePath"] = result.Metadata.RequestSourcePath;
            problem.Extensions["validationFailures"] = result.Metadata.ValidationFailures;
            problem.Extensions["guardrailFailures"] = result.Metadata.GuardrailFailures;
            problem.Extensions["attemptedModels"] = result.Metadata.AttemptedModels;

            return StatusCode(StatusCodes.Status502BadGateway, problem);
        }

        _logger.LogInformation(
            "LLM coach summary: sourcePath={sourcePath}, modelUsed={modelUsed}, attempts={attempts}, fallbackUsed={fallbackUsed}",
            result.Metadata.SourcePath,
            result.Metadata.ModelUsed,
            result.Metadata.Attempts,
            result.Metadata.FallbackUsed);

        return Ok(result.Response);
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
