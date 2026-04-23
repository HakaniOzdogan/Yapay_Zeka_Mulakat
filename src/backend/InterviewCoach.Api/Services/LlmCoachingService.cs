using System.Text.Json;
using System.Text.Json.Serialization;

namespace InterviewCoach.Api.Services;

public interface ILlmCoachingService
{
    Task<LlmCoachingResult> GenerateAsync(EvidenceSummaryDto evidenceSummary, CancellationToken cancellationToken = default);
    Task<LlmCoachingResult> GenerateWithModelAsync(EvidenceSummaryDto evidenceSummary, string model, CancellationToken cancellationToken = default);

    bool TryParseAndValidate(string json, out LlmCoachingResponse? response, out List<string> errors);
}

public class LlmCoachingService : ILlmCoachingService
{
    private static readonly JsonElement CoachingSchema = JsonSerializer.SerializeToElement(new
    {
        type = "object",
        additionalProperties = false,
        properties = new
        {
            rubric = new
            {
                type = "object",
                additionalProperties = false,
                properties = new
                {
                    technical_correctness = new { type = "integer", minimum = 0, maximum = 5 },
                    depth = new { type = "integer", minimum = 0, maximum = 5 },
                    structure = new { type = "integer", minimum = 0, maximum = 5 },
                    clarity = new { type = "integer", minimum = 0, maximum = 5 },
                    confidence = new { type = "integer", minimum = 0, maximum = 5 }
                },
                required = new[] { "technical_correctness", "depth", "structure", "clarity", "confidence" }
            },
            overall = new { type = "integer", minimum = 0, maximum = 100 },
            feedback = new
            {
                type = "array",
                minItems = 5,
                maxItems = 10,
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        category = new { type = "string" },
                        severity = new { type = "integer", minimum = 1, maximum = 5 },
                        title = new { type = "string" },
                        evidence = new { type = "string" },
                        time_range_ms = new
                        {
                            type = "array",
                            minItems = 2,
                            maxItems = 2,
                            items = new { type = "integer" }
                        },
                        suggestion = new { type = "string" },
                        example_phrase = new { type = "string" }
                    },
                    required = new[] { "category", "severity", "title", "evidence", "time_range_ms", "suggestion", "example_phrase" }
                }
            },
            drills = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    additionalProperties = false,
                    properties = new
                    {
                        title = new { type = "string" },
                        steps = new
                        {
                            type = "array",
                            items = new { type = "string" }
                        },
                        duration_min = new { type = "number" }
                    },
                    required = new[] { "title", "steps", "duration_min" }
                }
            }
        },
        required = new[] { "rubric", "overall", "feedback", "drills" }
    });

    private readonly ILlmClient _llmClient;

    private static readonly JsonSerializerOptions StrictOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
    };

    public LlmCoachingService(ILlmClient llmClient)
    {
        _llmClient = llmClient;
    }

    public async Task<LlmCoachingResult> GenerateAsync(EvidenceSummaryDto evidenceSummary, CancellationToken cancellationToken = default)
    {
        return await GenerateWithModelAsync(evidenceSummary, string.Empty, cancellationToken);
    }

    public async Task<LlmCoachingResult> GenerateWithModelAsync(EvidenceSummaryDto evidenceSummary, string model, CancellationToken cancellationToken = default)
    {
        var systemPrompt = BuildSystemPrompt(evidenceSummary.Language);
        var userPrompt = BuildUserPrompt(evidenceSummary);

        var raw = await _llmClient.GenerateJsonAsync(new LlmJsonRequest
        {
            Model = model,
            SystemPrompt = systemPrompt,
            UserPrompt = userPrompt,
            SchemaName = "coaching_report",
            Schema = CoachingSchema,
            SourcePath = "/responses"
        }, cancellationToken);

        if (TryParseAndValidate(raw.Content, out var output, out var errors))
        {
            var minified = JsonSerializer.Serialize(output);
            return LlmCoachingResult.Success(output!, minified, raw.Provider, raw.Model, raw.ReasoningEffort, raw.SourcePath);
        }

        var fixPrompt = BuildFixPrompt(raw.Content, errors);
        var retriedRaw = await _llmClient.GenerateJsonAsync(new LlmJsonRequest
        {
            Model = model,
            SystemPrompt = systemPrompt,
            UserPrompt = fixPrompt,
            SchemaName = "coaching_report",
            Schema = CoachingSchema,
            SourcePath = "/responses"
        }, cancellationToken);

        if (TryParseAndValidate(retriedRaw.Content, out output, out errors))
        {
            var minified = JsonSerializer.Serialize(output);
            return LlmCoachingResult.Success(output!, minified, retriedRaw.Provider, retriedRaw.Model, retriedRaw.ReasoningEffort, retriedRaw.SourcePath);
        }

        return LlmCoachingResult.Failure(errors, retriedRaw.Provider, retriedRaw.Model, retriedRaw.ReasoningEffort, retriedRaw.SourcePath);
    }

    public bool TryParseAndValidate(string json, out LlmCoachingResponse? response, out List<string> errors)
    {
        errors = [];
        response = null;

        try
        {
            var normalized = RemoveMeta(json);
            response = JsonSerializer.Deserialize<LlmCoachingResponse>(normalized, StrictOptions);
        }
        catch (Exception ex)
        {
            errors.Add($"JSON deserialization failed: {ex.Message}");
            return false;
        }

        if (response == null)
        {
            errors.Add("Response is null.");
            return false;
        }

        Validate(response, errors);
        return errors.Count == 0;
    }

    private static string RemoveMeta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return json;
        }

        if (!doc.RootElement.TryGetProperty("_meta", out _))
        {
            return json;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "_meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void Validate(LlmCoachingResponse response, List<string> errors)
    {
        ValidateRange(response.Rubric.TechnicalCorrectness, 0, 5, "rubric.technical_correctness", errors);
        ValidateRange(response.Rubric.Depth, 0, 5, "rubric.depth", errors);
        ValidateRange(response.Rubric.Structure, 0, 5, "rubric.structure", errors);
        ValidateRange(response.Rubric.Clarity, 0, 5, "rubric.clarity", errors);
        ValidateRange(response.Rubric.Confidence, 0, 5, "rubric.confidence", errors);
        ValidateRange(response.Overall, 0, 100, "overall", errors);

        if (response.Feedback.Count < 5 || response.Feedback.Count > 10)
            errors.Add("feedback count must be between 5 and 10.");

        for (var i = 0; i < response.Feedback.Count; i++)
        {
            var f = response.Feedback[i];
            var prefix = $"feedback[{i}]";

            if (f.Category is not ("vision" or "audio" or "content" or "structure"))
                errors.Add($"{prefix}.category must be one of: vision|audio|content|structure.");

            ValidateRange(f.Severity, 1, 5, $"{prefix}.severity", errors);

            if (f.TimeRangeMs == null || f.TimeRangeMs.Length != 2)
            {
                errors.Add($"{prefix}.time_range_ms must have exactly 2 values.");
            }
            else if (f.TimeRangeMs[0] > f.TimeRangeMs[1])
            {
                errors.Add($"{prefix}.time_range_ms start must be <= end.");
            }
        }
    }

    private static void ValidateRange(int value, int min, int max, string field, List<string> errors)
    {
        if (value < min || value > max)
            errors.Add($"{field} must be between {min} and {max}.");
    }

    private static string BuildSystemPrompt(string language)
    {
        var lang = string.IsNullOrWhiteSpace(language) || language == "unknown" ? "en" : language;

        return $"""
You are a strict interview coaching engine.
Output MUST be valid JSON only. No markdown. No prose.
Return exactly these top-level keys and no extras: rubric, overall, feedback, drills.
Use language: {lang}.
Numeric constraints:
- rubric.* integers between 0 and 5
- overall integer between 0 and 100
- feedback[].severity integer 1..5
- feedback[].time_range_ms array with exactly 2 integers [startMs,endMs], startMs<=endMs
Categories allowed only: vision, audio, content, structure.
Transcript may be absent in this build. When transcript slices are missing, rely on pattern windows, top issues, and worst-window evidence instead.
If evidence is insufficient, keep schema and say evidence is insufficient in evidence field.
""";
    }

    private static string BuildUserPrompt(EvidenceSummaryDto summary)
    {
        var compact = JsonSerializer.Serialize(summary);

        return $"""
Create coaching JSON from this evidence summary.
Constraints:
- feedback count must be between 5 and 10.
- every feedback item must reference evidence and a time_range_ms from available ranges.
- do not treat missing transcript as an error.
- do not invent facts outside provided evidence.
- drills should be practical.
EvidenceSummary JSON:
{compact}
""";
    }

    private static string BuildFixPrompt(string invalidJson, List<string> errors)
    {
        var errorText = string.Join("; ", errors);

        return $"""
FIX_JSON
Your previous output was invalid.
Errors: {errorText}
Return corrected JSON only, same schema, no extra keys.
Invalid JSON:
{invalidJson}
""";
    }
}

public sealed class LlmCoachingResult
{
    private LlmCoachingResult() { }

    public bool IsValid { get; private set; }
    public LlmCoachingResponse? Response { get; private set; }
    public string? MinifiedJson { get; private set; }
    public List<string> Errors { get; private set; } = [];
    public string Provider { get; private set; } = "OpenAI";
    public string Model { get; private set; } = string.Empty;
    public string? ReasoningEffort { get; private set; }
    public string? RequestSourcePath { get; private set; }

    public static LlmCoachingResult Success(
        LlmCoachingResponse response,
        string minifiedJson,
        string provider,
        string model,
        string? reasoningEffort,
        string? requestSourcePath)
    {
        return new LlmCoachingResult
        {
            IsValid = true,
            Response = response,
            MinifiedJson = minifiedJson,
            Errors = [],
            Provider = provider,
            Model = model,
            ReasoningEffort = reasoningEffort,
            RequestSourcePath = requestSourcePath
        };
    }

    public static LlmCoachingResult Failure(
        List<string> errors,
        string provider = "OpenAI",
        string model = "",
        string? reasoningEffort = null,
        string? requestSourcePath = null)
    {
        return new LlmCoachingResult
        {
            IsValid = false,
            Errors = errors,
            Provider = provider,
            Model = model,
            ReasoningEffort = reasoningEffort,
            RequestSourcePath = requestSourcePath
        };
    }
}

public record LlmCoachingResponse(
    [property: JsonPropertyName("rubric")] LlmRubric Rubric,
    [property: JsonPropertyName("overall")] int Overall,
    [property: JsonPropertyName("feedback")] List<LlmFeedbackItem> Feedback,
    [property: JsonPropertyName("drills")] List<LlmDrill> Drills
);

public record LlmRubric(
    [property: JsonPropertyName("technical_correctness")] int TechnicalCorrectness,
    [property: JsonPropertyName("depth")] int Depth,
    [property: JsonPropertyName("structure")] int Structure,
    [property: JsonPropertyName("clarity")] int Clarity,
    [property: JsonPropertyName("confidence")] int Confidence
);

public record LlmFeedbackItem(
    [property: JsonPropertyName("category")] string Category,
    [property: JsonPropertyName("severity")] int Severity,
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("evidence")] string Evidence,
    [property: JsonPropertyName("time_range_ms")] long[] TimeRangeMs,
    [property: JsonPropertyName("suggestion")] string Suggestion,
    [property: JsonPropertyName("example_phrase")] string ExamplePhrase
);

public record LlmDrill(
    [property: JsonPropertyName("title")] string Title,
    [property: JsonPropertyName("steps")] List<string> Steps,
    [property: JsonPropertyName("duration_min")] double DurationMin
);
