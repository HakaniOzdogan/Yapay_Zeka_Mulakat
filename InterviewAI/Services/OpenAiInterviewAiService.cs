using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace InterviewAI.Services;

public class OpenAiInterviewAiService : IInterviewAiService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly InterviewAiOptions _options;
    private readonly ILogger<OpenAiInterviewAiService> _logger;

    public OpenAiInterviewAiService(
        HttpClient httpClient,
        IOptions<InterviewAiOptions> options,
        ILogger<OpenAiInterviewAiService> logger)
    {
        _httpClient = httpClient;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<AiQuestionResult?> GenerateNextQuestionAsync(AiQuestionRequest request, CancellationToken cancellationToken = default)
    {
        if (!CanCallAi())
        {
            return null;
        }

        var previous = request.PreviousQuestions.Count == 0
            ? "None"
            : string.Join(" | ", request.PreviousQuestions);

        var systemPrompt = """
            You are an interview bot.
            Return only valid JSON with fields:
            - question (string)
            - followUpHint (string)
            Keep question concise and role-relevant.
            """;

        var userPrompt = $"""
            Role: {request.Role}
            Difficulty: {request.Difficulty}
            QuestionNumber: {request.QuestionNumber}/{request.TotalQuestions}
            PreviousQuestions: {previous}
            Generate the next interview question in Turkish.
            """;

        var output = await SendPromptAsync(systemPrompt, userPrompt, cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var json = NormalizeJson(output);
        try
        {
            var dto = JsonSerializer.Deserialize<QuestionDto>(json, JsonOptions);
            if (dto == null || string.IsNullOrWhiteSpace(dto.Question))
            {
                return null;
            }

            return new AiQuestionResult(dto.Question.Trim(), (dto.FollowUpHint ?? string.Empty).Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI question parse failed.");
            return null;
        }
    }

    public async Task<AiEvaluationResult?> EvaluateAnswerAsync(AiEvaluationRequest request, CancellationToken cancellationToken = default)
    {
        if (!CanCallAi())
        {
            return null;
        }

        var systemPrompt = """
            You are an interview evaluator.
            Return only valid JSON with fields:
            - score (number 0..100)
            - coachingTip (string)
            """;

        var userPrompt = $"""
            Role: {request.Role}
            Difficulty: {request.Difficulty}
            Question: {request.Question}
            CandidateAnswer: {request.Answer}
            EyeContactScore: {request.EyeContactScore}
            PostureScore: {request.PostureScore}
            ConfidenceScore: {request.ConfidenceScore}
            Evaluate answer quality and communication in Turkish.
            """;

        var output = await SendPromptAsync(systemPrompt, userPrompt, cancellationToken);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }

        var json = NormalizeJson(output);
        try
        {
            var dto = JsonSerializer.Deserialize<EvaluationDto>(json, JsonOptions);
            if (dto == null)
            {
                return null;
            }

            var score = Math.Clamp(dto.Score, 0, 100);
            return new AiEvaluationResult(score, (dto.CoachingTip ?? string.Empty).Trim());
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI evaluation parse failed.");
            return null;
        }
    }

    private bool CanCallAi()
    {
        return _options.Enabled
            && !string.IsNullOrWhiteSpace(_options.ApiKey)
            && !string.IsNullOrWhiteSpace(_options.Endpoint)
            && !string.IsNullOrWhiteSpace(_options.Model);
    }

    private async Task<string?> SendPromptAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken)
    {
        try
        {
            using var request = BuildRequest(systemPrompt, userPrompt);
            using var response = await _httpClient.SendAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorText = await response.Content.ReadAsStringAsync(cancellationToken);
                _logger.LogWarning("AI call failed with status {StatusCode}: {Error}", (int)response.StatusCode, errorText);
                return null;
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            return ExtractModelText(body);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "AI call failed.");
            return null;
        }
    }

    private HttpRequestMessage BuildRequest(string systemPrompt, string userPrompt)
    {
        var request = new HttpRequestMessage(HttpMethod.Post, _options.Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);

        object payload;
        if (_options.Endpoint.Contains("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            payload = new
            {
                model = _options.Model,
                temperature = 0.3,
                messages = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };
        }
        else
        {
            payload = new
            {
                model = _options.Model,
                temperature = 0.3,
                input = new object[]
                {
                    new { role = "system", content = systemPrompt },
                    new { role = "user", content = userPrompt }
                }
            };
        }

        request.Content = new StringContent(JsonSerializer.Serialize(payload, JsonOptions), Encoding.UTF8, "application/json");
        return request;
    }

    private static string? ExtractModelText(string rawBody)
    {
        using var doc = JsonDocument.Parse(rawBody);
        var root = doc.RootElement;

        if (root.TryGetProperty("choices", out var choices) &&
            choices.ValueKind == JsonValueKind.Array &&
            choices.GetArrayLength() > 0 &&
            choices[0].TryGetProperty("message", out var message) &&
            message.TryGetProperty("content", out var content))
        {
            return content.GetString();
        }

        if (root.TryGetProperty("output_text", out var outputText) && outputText.ValueKind == JsonValueKind.String)
        {
            return outputText.GetString();
        }

        if (root.TryGetProperty("output", out var output) &&
            output.ValueKind == JsonValueKind.Array &&
            output.GetArrayLength() > 0)
        {
            foreach (var item in output.EnumerateArray())
            {
                if (!item.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var part in contentArray.EnumerateArray())
                {
                    if (part.TryGetProperty("text", out var text) && text.ValueKind == JsonValueKind.String)
                    {
                        return text.GetString();
                    }
                }
            }
        }

        return null;
    }

    private static string NormalizeJson(string text)
    {
        var trimmed = text.Trim();
        if (trimmed.StartsWith("```", StringComparison.Ordinal))
        {
            var firstBrace = trimmed.IndexOf('{');
            var lastBrace = trimmed.LastIndexOf('}');
            if (firstBrace >= 0 && lastBrace > firstBrace)
            {
                return trimmed[firstBrace..(lastBrace + 1)];
            }
        }

        return trimmed;
    }

    private sealed class QuestionDto
    {
        public string? Question { get; set; }
        public string? FollowUpHint { get; set; }
    }

    private sealed class EvaluationDto
    {
        public double Score { get; set; }
        public string? CoachingTip { get; set; }
    }
}
