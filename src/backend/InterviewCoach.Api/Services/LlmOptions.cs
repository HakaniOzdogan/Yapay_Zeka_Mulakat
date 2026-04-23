using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public class LlmOptions
{
    public string Provider { get; set; } = "OpenAI";
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string PrimaryModel { get; set; } = "gpt-5.4";
    public string LiveAnalysisModel { get; set; } = "gpt-5.4-mini";
    public string ReasoningEffort { get; set; } = "high";
    public List<string> FallbackModels { get; set; } = [];
    public int TimeoutSeconds { get; set; } = 120;
    public float Temperature { get; set; } = 0.2f;
    public string PromptVersionCoach { get; set; } = "coach_v1";
    public LlmRetryOptions Retry { get; set; } = new();
    public LlmFallbackOptions Fallback { get; set; } = new();

    // Compatibility shim for older code/tests that still reference "Model".
    public string Model
    {
        get => string.IsNullOrWhiteSpace(PrimaryModel) ? "gpt-5.4" : PrimaryModel;
        set => PrimaryModel = value;
    }
}

public class LlmRetryOptions
{
    public int MaxAttemptsPrimary { get; set; } = 2;
    public bool RetryOnInvalidJson { get; set; } = true;
    public bool RetryOnTimeout { get; set; } = true;
    public bool RetryOnHttp5xx { get; set; } = true;
    public List<int> BackoffMs { get; set; } = [500, 1000];
}

public class LlmFallbackOptions
{
    public bool Enabled { get; set; } = true;
    public bool TryFallbackModelsOnFailure { get; set; } = true;
    public bool UseCachedSameInputHashIfAllFail { get; set; } = true;
    public bool UseCachedAnyPreviousForSessionIfSameInputMissing { get; set; } = false;
    public int CacheFallbackMaxAgeHours { get; set; } = 168;
}

public sealed class LlmOptionsValidator : IValidateOptions<LlmOptions>
{
    public ValidateOptionsResult Validate(string? name, LlmOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var provider = (options.Provider ?? string.Empty).Trim();

        if (!string.Equals(provider, "OpenAI", StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("Llm:Provider must be 'OpenAI' in this build.");
        }

        if (string.IsNullOrWhiteSpace(options.ApiKey))
        {
            failures.Add("Llm:ApiKey is required when Llm:Provider is OpenAI.");
        }

        if (string.IsNullOrWhiteSpace(options.PrimaryModel))
        {
            failures.Add("Llm:PrimaryModel is required.");
        }

        if (string.IsNullOrWhiteSpace(options.LiveAnalysisModel))
        {
            failures.Add("Llm:LiveAnalysisModel is required.");
        }

        if (!Uri.TryCreate(options.BaseUrl, UriKind.Absolute, out _))
        {
            failures.Add("Llm:BaseUrl must be a valid absolute URI.");
        }

        if (options.TimeoutSeconds <= 0)
        {
            failures.Add("Llm:TimeoutSeconds must be greater than zero.");
        }

        return failures.Count > 0
            ? ValidateOptionsResult.Fail(failures)
            : ValidateOptionsResult.Success;
    }
}
