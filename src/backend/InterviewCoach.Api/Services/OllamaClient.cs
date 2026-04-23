using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public interface ILlmClient
{
    Task<LlmJsonResponse> GenerateJsonAsync(LlmJsonRequest request, CancellationToken cancellationToken = default);
}

public sealed class OpenAiResponsesClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly LlmOptions _options;

    public OpenAiResponsesClient(HttpClient httpClient, IOptions<LlmOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", _options.ApiKey);
        }
    }

    public async Task<LlmJsonResponse> GenerateJsonAsync(LlmJsonRequest request, CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? _options.PrimaryModel : request.Model.Trim();
        var effort = string.IsNullOrWhiteSpace(request.ReasoningEffort) ? _options.ReasoningEffort : request.ReasoningEffort.Trim();

        // OpenAI-compatible chat/completions format — works with both OpenAI and Ollama.
        var payload = new
        {
            model,
            messages = new[]
            {
                new { role = "system", content = request.SystemPrompt },
                new { role = "user",   content = request.UserPrompt }
            },
            response_format = new { type = "json_object" },
            temperature = _options.Temperature > 0 ? (double?)_options.Temperature : null,
            stream = false
        };

        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/v1/chat/completions", content, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"LLM chat/completions request failed ({(int)response.StatusCode}): {ReadErrorMessage(responseText)}");
        }

        using var doc = JsonDocument.Parse(responseText);
        var outputText = ReadOutputText(doc.RootElement);
        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new InvalidOperationException("LLM chat/completions response did not contain structured text.");
        }

        return new LlmJsonResponse
        {
            Provider = "OpenAI-compat",
            Model = ReadString(doc.RootElement, "model") ?? model,
            ReasoningEffort = effort,
            SourcePath = "/v1/chat/completions",
            Content = outputText.Trim()
        };
    }

    private static string? ReadString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
    }

    private static string ReadErrorMessage(string responseText)
    {
        try
        {
            using var doc = JsonDocument.Parse(responseText);
            if (doc.RootElement.TryGetProperty("error", out var errorElement) &&
                errorElement.ValueKind == JsonValueKind.Object &&
                errorElement.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String)
            {
                return messageElement.GetString() ?? responseText;
            }
        }
        catch
        {
            // Keep original payload text below.
        }

        return responseText;
    }

    // Parses choices[0].message.content from OpenAI-compat response.
    private static string? ReadOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("choices", out var choices) || choices.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var choice in choices.EnumerateArray())
        {
            if (!choice.TryGetProperty("message", out var message) || message.ValueKind != JsonValueKind.Object)
                continue;

            if (message.TryGetProperty("content", out var contentEl) && contentEl.ValueKind == JsonValueKind.String)
                return contentEl.GetString();
        }

        return null;
    }
}

public sealed class LlmJsonRequest
{
    public string Model { get; set; } = string.Empty;
    public string SystemPrompt { get; set; } = string.Empty;
    public string UserPrompt { get; set; } = string.Empty;
    public string SchemaName { get; set; } = "response";
    public JsonElement Schema { get; set; }
    public string? ReasoningEffort { get; set; }
    public string? SourcePath { get; set; }
}

public sealed class LlmJsonResponse
{
    public string Provider { get; set; } = "OpenAI";
    public string Model { get; set; } = string.Empty;
    public string? ReasoningEffort { get; set; }
    public string? SourcePath { get; set; }
    public string Content { get; set; } = string.Empty;
}
