using System.Text.Json;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public interface ILlmAnalysisService
{
    Task<LiveAnalysisResult> AnalyzeLiveWindowAsync(LiveAnalysisInput input, CancellationToken cancellationToken = default);
}

public class OpenAiLlmAnalysisService : ILlmAnalysisService
{
    private static readonly JsonElement LiveAnalysisSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            summary = new { type = "string" },
            risks = new
            {
                type = "array",
                items = new { type = "string" }
            },
            suggestions = new
            {
                type = "array",
                items = new { type = "string" }
            },
            confidence = new
            {
                type = "number",
                minimum = 0,
                maximum = 1
            }
        },
        required = new[] { "summary", "risks", "suggestions", "confidence" }
    });

    private readonly ILlmClient _llmClient;
    private readonly ILogger<OpenAiLlmAnalysisService> _logger;
    private readonly LlmOptions _options;

    public OpenAiLlmAnalysisService(
        ILlmClient llmClient,
        ILogger<OpenAiLlmAnalysisService> logger,
        IOptions<LlmOptions> llmOptions)
    {
        _llmClient = llmClient;
        _logger = logger;
        _options = llmOptions.Value;
    }

    public async Task<LiveAnalysisResult> AnalyzeLiveWindowAsync(LiveAnalysisInput input, CancellationToken cancellationToken = default)
    {
        try
        {
            var raw = await _llmClient.GenerateJsonAsync(new LlmJsonRequest
            {
                Model = string.IsNullOrWhiteSpace(_options.LiveAnalysisModel) ? _options.PrimaryModel : _options.LiveAnalysisModel,
                SystemPrompt = BuildSystemPrompt(),
                UserPrompt = BuildUserPrompt(input),
                SchemaName = "live_analysis",
                Schema = LiveAnalysisSchema,
                ReasoningEffort = "medium",
                SourcePath = "/responses"
            }, cancellationToken);

            var parsed = ParseModelResult(raw.Content);

            if (parsed == null)
            {
                return LiveAnalysisResult.Fallback("LLM yaniti islenemedi.");
            }

            parsed.Model ??= raw.Model;
            parsed.Timestamp = DateTime.UtcNow;
            parsed.Normalize();
            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to generate live OpenAI analysis.");
            return LiveAnalysisResult.Fallback("LLM yaniti parse edilemedi.");
        }
    }

    private static string BuildSystemPrompt()
    {
        return """
You are an interview coach assistant.
Always respond in Turkish.
Focus only on the provided visual and event-based evidence.
Transcript is disabled in this build, so do not mention missing transcript as a problem.
Keep risks and suggestions concise.
""";
    }

    private static string BuildUserPrompt(LiveAnalysisInput input)
    {
        return JsonSerializer.Serialize(new
        {
            task = "Canli mulakat penceresi icin kisa davranis analizi yap.",
            windowSec = input.WindowSec,
            role = input.Role,
            questionPrompt = input.QuestionPrompt,
            metrics = input
        });
    }

    private LiveAnalysisResult? ParseModelResult(string jsonText)
    {
        try
        {
            var strict = JsonSerializer.Deserialize<LiveAnalysisResult>(jsonText, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });
            if (strict != null && !string.IsNullOrWhiteSpace(strict.Summary))
            {
                return strict;
            }
        }
        catch
        {
            // Continue with tolerant parser.
        }

        try
        {
            using var doc = JsonDocument.Parse(jsonText);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            var summary = GetStringByHints(root, "summary", "ozet");
            var risks = GetArrayByHints(root, "risk");
            var suggestions = GetArrayByHints(root, "suggest", "oner");
            var confidence = GetNumberByHints(root, "conf", "guven");

            return new LiveAnalysisResult
            {
                Summary = summary ?? "Canli analiz olusturuldu.",
                Risks = risks,
                Suggestions = suggestions,
                Confidence = confidence ?? 0.6f
            };
        }
        catch
        {
            return null;
        }
    }

    private static string? GetStringByHints(JsonElement root, params string[] hints)
    {
        foreach (var prop in root.EnumerateObject())
        {
            var name = prop.Name.ToLowerInvariant();
            if (!hints.Any(h => name.Contains(h))) continue;
            if (prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString();
        }
        return null;
    }

    private static List<string> GetArrayByHints(JsonElement root, params string[] hints)
    {
        foreach (var prop in root.EnumerateObject())
        {
            var name = prop.Name.ToLowerInvariant();
            if (!hints.Any(h => name.Contains(h))) continue;
            if (prop.Value.ValueKind != JsonValueKind.Array) continue;
            return prop.Value.EnumerateArray()
                .Where(x => x.ValueKind == JsonValueKind.String)
                .Select(x => x.GetString() ?? string.Empty)
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .ToList();
        }
        return [];
    }

    private static float? GetNumberByHints(JsonElement root, params string[] hints)
    {
        foreach (var prop in root.EnumerateObject())
        {
            var name = prop.Name.ToLowerInvariant();
            if (!hints.Any(h => name.Contains(h))) continue;
            if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetSingle(out var num))
                return num;
        }
        return null;
    }
}

public class LiveAnalysisInput
{
    public string SessionId { get; set; } = string.Empty;
    public int WindowSec { get; set; } = 15;
    public string Role { get; set; } = string.Empty;
    public string QuestionPrompt { get; set; } = string.Empty;
    public float EyeContactAvg { get; set; }
    public float HeadStabilityAvg { get; set; }
    public float PostureAvg { get; set; }
    public float FidgetAvg { get; set; }
    public float EyeOpennessAvg { get; set; }
    public int BlinkCountWindow { get; set; }
    public Dictionary<string, float> EmotionDistribution { get; set; } = [];
}

public class LiveAnalysisResult
{
    public string Summary { get; set; } = string.Empty;
    public List<string> Risks { get; set; } = [];
    public List<string> Suggestions { get; set; } = [];
    public float Confidence { get; set; }
    public string? Model { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;

    public void Normalize()
    {
        Summary = string.IsNullOrWhiteSpace(Summary) ? "Canli analiz hazir degil." : Summary.Trim();
        Risks = Risks.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Take(3).ToList();
        Suggestions = Suggestions.Where(x => !string.IsNullOrWhiteSpace(x)).Select(x => x.Trim()).Take(3).ToList();
        Confidence = Math.Clamp(Confidence, 0f, 1f);
    }

    public static LiveAnalysisResult Fallback(string summary)
    {
        return new LiveAnalysisResult
        {
            Summary = summary,
            Risks = [],
            Suggestions = [],
            Confidence = 0.1f,
            Model = "fallback",
            Timestamp = DateTime.UtcNow
        };
    }
}
