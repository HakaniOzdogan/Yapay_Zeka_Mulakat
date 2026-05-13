using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

/// <summary>
/// ILlmClient implementation for Anthropic Claude API (/v1/messages).
/// Follows the spec in md/04-LLM-ENTEGRASYONU.md.
/// </summary>
public sealed class AnthropicClient : ILlmClient
{
    private readonly HttpClient _httpClient;
    private readonly LlmOptions _options;

    private const string AnthropicVersion = "2023-06-01";

    public AnthropicClient(HttpClient httpClient, IOptions<LlmOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;

        // Anthropic uses x-api-key header instead of Bearer token
        if (!string.IsNullOrWhiteSpace(_options.ApiKey))
        {
            _httpClient.DefaultRequestHeaders.Add("x-api-key", _options.ApiKey);
        }
        _httpClient.DefaultRequestHeaders.Add("anthropic-version", AnthropicVersion);
    }

    public async Task<LlmJsonResponse> GenerateJsonAsync(LlmJsonRequest request, CancellationToken cancellationToken = default)
    {
        var model = string.IsNullOrWhiteSpace(request.Model) ? _options.PrimaryModel : request.Model.Trim();
        var effort = string.IsNullOrWhiteSpace(request.ReasoningEffort) ? _options.ReasoningEffort : request.ReasoningEffort.Trim();

        // Anthropic Messages API format
        var payload = new
        {
            model,
            max_tokens = 4096,
            system = request.SystemPrompt,
            messages = new[]
            {
                new { role = "user", content = request.UserPrompt }
            },
            temperature = _options.Temperature > 0 ? (double?)_options.Temperature : null
        };

        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/v1/messages", content, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Anthropic API request failed ({(int)response.StatusCode}): {ReadErrorMessage(responseText)}");
        }

        using var doc = JsonDocument.Parse(responseText);
        var outputText = ReadOutputText(doc.RootElement);
        if (string.IsNullOrWhiteSpace(outputText))
        {
            throw new InvalidOperationException("Anthropic response did not contain text content.");
        }

        return new LlmJsonResponse
        {
            Provider = "Anthropic",
            Model = ReadString(doc.RootElement, "model") ?? model,
            ReasoningEffort = effort,
            SourcePath = "/v1/messages",
            Content = outputText.Trim()
        };
    }

    /// <summary>
    /// Extracts text from Anthropic response: content[0].text
    /// </summary>
    private static string? ReadOutputText(JsonElement root)
    {
        if (!root.TryGetProperty("content", out var contentArray) || contentArray.ValueKind != JsonValueKind.Array)
            return null;

        foreach (var block in contentArray.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var typeEl) &&
                typeEl.ValueKind == JsonValueKind.String &&
                typeEl.GetString() == "text" &&
                block.TryGetProperty("text", out var textEl) &&
                textEl.ValueKind == JsonValueKind.String)
            {
                return textEl.GetString();
            }
        }

        return null;
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
}
