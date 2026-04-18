using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public interface IOllamaClient
{
    Task<string> ChatJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);

    Task<string> ChatJsonAsync(string model, string systemPrompt, string userPrompt, CancellationToken cancellationToken = default);
}

public class OllamaClient : IOllamaClient
{
    private readonly HttpClient _httpClient;
    private readonly LlmOptions _options;

    public OllamaClient(HttpClient httpClient, IOptions<LlmOptions> options)
    {
        _httpClient = httpClient;
        _options = options.Value;
    }

    public async Task<string> ChatJsonAsync(string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        return await ChatJsonAsync(_options.Model, systemPrompt, userPrompt, cancellationToken);
    }

    public async Task<string> ChatJsonAsync(string model, string systemPrompt, string userPrompt, CancellationToken cancellationToken = default)
    {
        var payload = new
        {
            model = string.IsNullOrWhiteSpace(model) ? _options.Model : model,
            messages = new object[]
            {
                new { role = "system", content = systemPrompt },
                new { role = "user", content = userPrompt }
            },
            stream = false,
            format = "json"
        };

        var body = JsonSerializer.Serialize(payload);
        using var content = new StringContent(body, Encoding.UTF8, "application/json");
        using var response = await _httpClient.PostAsync("/api/chat", content, cancellationToken);
        var responseText = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Ollama request failed ({(int)response.StatusCode}): {responseText}");

        using var doc = JsonDocument.Parse(responseText);
        if (doc.RootElement.TryGetProperty("message", out var message) &&
            message.ValueKind == JsonValueKind.Object &&
            message.TryGetProperty("content", out var contentElement) &&
            contentElement.ValueKind == JsonValueKind.String)
        {
            var contentText = contentElement.GetString();
            if (!string.IsNullOrWhiteSpace(contentText))
                return contentText;
        }

        if (doc.RootElement.TryGetProperty("response", out var responseElement) && responseElement.ValueKind == JsonValueKind.String)
        {
            var contentText = responseElement.GetString();
            if (!string.IsNullOrWhiteSpace(contentText))
                return contentText;
        }

        throw new InvalidOperationException("Ollama response did not contain assistant message content.");
    }
}
