using InterviewCoach.Api.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/admin/retention")]
[Authorize(Policy = "AdminOnly")]
public class RetentionAdminController : ControllerBase
{
    private readonly IRetentionCleanupService _cleanupService;
    private readonly IRetentionRunState _runState;
    private readonly IOptionsMonitor<RetentionOptions> _optionsMonitor;

    public RetentionAdminController(
        IRetentionCleanupService cleanupService,
        IRetentionRunState runState,
        IOptionsMonitor<RetentionOptions> optionsMonitor)
    {
        _cleanupService = cleanupService;
        _runState = runState;
        _optionsMonitor = optionsMonitor;
    }

    [HttpPost("run")]
    [ProducesResponseType(typeof(RetentionRunSummary), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public async Task<ActionResult<RetentionRunSummary>> Run(CancellationToken cancellationToken)
    {
        var summary = await _cleanupService.RunOnceAsync(respectEnabled: false, cancellationToken);
        return Ok(summary);
    }

    [HttpGet("status")]
    [ProducesResponseType(typeof(RetentionStatusDto), StatusCodes.Status200OK)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status401Unauthorized)]
    [ProducesResponseType(typeof(ProblemDetails), StatusCodes.Status403Forbidden)]
    public ActionResult<RetentionStatusDto> Status()
    {
        var options = _optionsMonitor.CurrentValue;
        var lastRun = _runState.LastRun;

        return Ok(new RetentionStatusDto
        {
            Enabled = options.Enabled,
            DeleteAfterDays = options.DeleteAfterDays,
            KeepSummariesOnlyAfterDays = options.KeepSummariesOnlyAfterDays,
            RunHourUtc = options.RunHourUtc,
            LastRun = lastRun
        });
    }
}

public class RetentionStatusDto
{
    public bool Enabled { get; set; }
    public int DeleteAfterDays { get; set; }
    public int? KeepSummariesOnlyAfterDays { get; set; }
    public int RunHourUtc { get; set; }
    public RetentionRunSummary? LastRun { get; set; }
}
