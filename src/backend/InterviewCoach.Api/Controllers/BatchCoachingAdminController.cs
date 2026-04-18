using InterviewCoach.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/admin/llm/batch-coach/jobs")]
[Authorize(Policy = "AdminOnly")]
public class BatchCoachingAdminController : ControllerBase
{
    private readonly IBatchCoachingJobService _jobService;

    public BatchCoachingAdminController(IBatchCoachingJobService jobService)
    {
        _jobService = jobService;
    }

    /// <summary>
    /// Creates a batch LLM coaching job.
    /// </summary>
    [HttpPost]
    [ProducesResponseType(typeof(BatchCoachingJobCreateAcceptedDto), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status500InternalServerError)]
    public async Task<ActionResult<BatchCoachingJobCreateAcceptedDto>> CreateJob(
        [FromBody] BatchCoachingJobCreateRequest request,
        CancellationToken cancellationToken)
    {
        if (request == null)
        {
            return this.ValidationProblem("Request body is required.", new Dictionary<string, string[]>
            {
                ["body"] = ["Request body is required."]
            });
        }

        User.TryGetCurrentUserId(out var currentUserId);
        Guid? creatorId = currentUserId == Guid.Empty ? null : currentUserId;

        var result = await _jobService.CreateJobAsync(request, creatorId, cancellationToken);
        if (!result.Success)
        {
            return this.ValidationProblem(result.Error ?? "Invalid batch request.", new Dictionary<string, string[]>
            {
                ["batch"] = [result.Error ?? "Invalid batch request."]
            });
        }

        return Accepted(new BatchCoachingJobCreateAcceptedDto
        {
            JobId = result.JobId,
            Status = result.Status,
            TotalSessions = result.TotalSessions
        });
    }

    /// <summary>
    /// Returns recent batch coaching jobs.
    /// </summary>
    [HttpGet]
    [ProducesResponseType(typeof(List<BatchCoachingJobSummaryDto>), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<List<BatchCoachingJobSummaryDto>>> GetJobs([FromQuery] int take = 20, CancellationToken cancellationToken = default)
    {
        var items = await _jobService.GetJobsAsync(take, cancellationToken);
        return Ok(items);
    }

    /// <summary>
    /// Returns a specific batch coaching job details.
    /// </summary>
    [HttpGet("{jobId:guid}")]
    [ProducesResponseType(typeof(BatchCoachingJobDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BatchCoachingJobDetailDto>> GetJob(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _jobService.GetJobAsync(jobId, cancellationToken);
        if (job == null)
        {
            return this.NotFoundProblem($"Batch coaching job '{jobId}' was not found.");
        }

        return Ok(job);
    }

    /// <summary>
    /// Returns paged items for a batch coaching job.
    /// </summary>
    [HttpGet("{jobId:guid}/items")]
    [ProducesResponseType(typeof(BatchCoachingJobItemsPageDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BatchCoachingJobItemsPageDto>> GetJobItems(
        Guid jobId,
        [FromQuery] string? status,
        [FromQuery] int take = 100,
        [FromQuery] int skip = 0,
        CancellationToken cancellationToken = default)
    {
        var page = await _jobService.GetJobItemsAsync(jobId, status, take, skip, cancellationToken);
        if (page == null)
        {
            return this.NotFoundProblem($"Batch coaching job '{jobId}' was not found.");
        }

        return Ok(page);
    }

    /// <summary>
    /// Cancels a queued or running batch coaching job.
    /// </summary>
    [HttpPost("{jobId:guid}/cancel")]
    [ProducesResponseType(typeof(BatchCoachingJobDetailDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status404NotFound)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<BatchCoachingJobDetailDto>> CancelJob(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _jobService.CancelJobAsync(jobId, cancellationToken);
        if (job == null)
        {
            return this.NotFoundProblem($"Batch coaching job '{jobId}' was not found.");
        }

        return Ok(job);
    }
}

public class BatchCoachingJobCreateAcceptedDto
{
    public Guid JobId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int TotalSessions { get; set; }
}
