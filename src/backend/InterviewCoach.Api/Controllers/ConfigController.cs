using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using InterviewCoach.Application;
using InterviewCoach.Api.Services;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class ConfigController : ControllerBase
{
    private readonly ScoringProfilesOptions _options;
    private readonly LlmOptions _llmOptions;

    public ConfigController(IOptions<ScoringProfilesOptions> options, IOptions<LlmOptions> llmOptions)
    {
        _options = options.Value;
        _llmOptions = llmOptions.Value;
    }

    [HttpGet]
    public ActionResult GetConfig()
    {
        return Ok(new
        {
            scoringProfiles = _options,
            llm = new
            {
                provider = _llmOptions.Provider,
                baseUrl = _llmOptions.BaseUrl,
                primaryModel = _llmOptions.PrimaryModel,
                liveAnalysisModel = _llmOptions.LiveAnalysisModel,
                reasoningEffort = _llmOptions.ReasoningEffort
            }
        });
    }
}
