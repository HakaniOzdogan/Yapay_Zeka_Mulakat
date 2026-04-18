using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using InterviewCoach.Application;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly ScoringProfilesOptions _options;

    public ConfigController(IOptions<ScoringProfilesOptions> options)
    {
        _options = options.Value;
    }

    [HttpGet]
    public ActionResult GetConfig()
    {
        return Ok(new
        {
            scoringProfiles = _options
        });
    }
}
