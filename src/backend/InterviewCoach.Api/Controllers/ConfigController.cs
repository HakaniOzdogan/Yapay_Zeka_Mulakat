using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using InterviewCoach.Application;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly ApplicationOptions _options;

    public ConfigController(IOptions<ApplicationOptions> options)
    {
        _options = options.Value;
    }

    [HttpGet]
    public ActionResult GetConfig()
    {
        return Ok(new
        {
            scoring = _options.ScoringConfig
        });
    }
}
