using InterviewCoach.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace InterviewCoach.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class AnalysisController : ControllerBase
{
    private readonly ILlmAnalysisService _llmAnalysisService;

    public AnalysisController(ILlmAnalysisService llmAnalysisService)
    {
        _llmAnalysisService = llmAnalysisService;
    }

    [HttpPost("live-window")]
    public async Task<ActionResult<LiveAnalysisResponse>> AnalyzeLiveWindow(
        [FromBody] LiveAnalysisRequest request,
        CancellationToken cancellationToken)
    {
        if (request.WindowSec <= 0)
            request.WindowSec = 15;

        var input = new LiveAnalysisInput
        {
            SessionId = request.SessionId,
            WindowSec = request.WindowSec,
            Role = request.Role,
            QuestionPrompt = request.QuestionPrompt,
            EyeContactAvg = request.VideoMetrics.EyeContactAvg,
            HeadStabilityAvg = request.VideoMetrics.HeadStabilityAvg,
            PostureAvg = request.VideoMetrics.PostureAvg,
            FidgetAvg = request.VideoMetrics.FidgetAvg,
            EyeOpennessAvg = request.VideoMetrics.EyeOpennessAvg,
            BlinkCountWindow = request.VideoMetrics.BlinkCountWindow,
            EmotionDistribution = request.VideoMetrics.EmotionDistribution
        };

        var result = await _llmAnalysisService.AnalyzeLiveWindowAsync(input, cancellationToken);

        return Ok(new LiveAnalysisResponse
        {
            Summary = result.Summary,
            Risks = result.Risks,
            Suggestions = result.Suggestions,
            Confidence = result.Confidence,
            Model = result.Model ?? "unknown",
            Timestamp = result.Timestamp
        });
    }
}

public class LiveAnalysisRequest
{
    public string SessionId { get; set; } = string.Empty;
    public int WindowSec { get; set; } = 15;
    public string Role { get; set; } = string.Empty;
    public string QuestionPrompt { get; set; } = string.Empty;
    public LiveVideoMetrics VideoMetrics { get; set; } = new();
}

public class LiveVideoMetrics
{
    public float EyeContactAvg { get; set; }
    public float HeadStabilityAvg { get; set; }
    public float PostureAvg { get; set; }
    public float FidgetAvg { get; set; }
    public float EyeOpennessAvg { get; set; }
    public int BlinkCountWindow { get; set; }
    public Dictionary<string, float> EmotionDistribution { get; set; } = [];
}

public class LiveAnalysisResponse
{
    public string Summary { get; set; } = string.Empty;
    public List<string> Risks { get; set; } = [];
    public List<string> Suggestions { get; set; } = [];
    public float Confidence { get; set; }
    public string Model { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}
