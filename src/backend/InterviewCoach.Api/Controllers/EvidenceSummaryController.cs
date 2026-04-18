using InterviewCoach.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/sessions/{sessionId:guid}")]
[Authorize]
[SessionOwnership]
public class EvidenceSummaryController : ControllerBase
{
    private readonly IEvidenceSummaryService _evidenceSummaryService;

    public EvidenceSummaryController(IEvidenceSummaryService evidenceSummaryService)
    {
        _evidenceSummaryService = evidenceSummaryService;
    }

    /// <summary>
    /// Returns deterministic evidence summary for a session.
    /// </summary>
    [HttpGet("evidence-summary")]
    [ProducesResponseType(typeof(EvidenceSummaryDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status413PayloadTooLarge)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status429TooManyRequests)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<EvidenceSummaryDto>> GetEvidenceSummary(Guid sessionId, CancellationToken cancellationToken)
    {
        var summary = await _evidenceSummaryService.BuildAsync(sessionId, cancellationToken);
        if (summary == null)
            return this.NotFoundProblem($"Session '{sessionId}' was not found.");

        return Ok(summary);
    }
}
