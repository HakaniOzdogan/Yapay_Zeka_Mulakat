using InterviewCoach.Infrastructure;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("")]
public class HealthController : ControllerBase
{
    [HttpGet("health")]
    public ActionResult<object> Health()
    {
        return Ok(new { status = "ok" });
    }

    [HttpGet("health/ready")]
    public async Task<ActionResult<object>> Ready([FromServices] ApplicationDbContext db, CancellationToken cancellationToken)
    {
        try
        {
            await db.Database.ExecuteSqlRawAsync("SELECT 1", cancellationToken);
            return Ok(new { status = "ok" });
        }
        catch
        {
            return StatusCode(StatusCodes.Status503ServiceUnavailable, new { status = "unavailable" });
        }
    }
}