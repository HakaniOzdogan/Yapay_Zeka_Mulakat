using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public sealed class LlmOptimizationOptionsValidator : IValidateOptions<LlmOptimizationOptions>
{
    public ValidateOptionsResult Validate(string? name, LlmOptimizationOptions options)
    {
        if (options == null)
        {
            return ValidateOptionsResult.Fail("LlmOptimization config is required.");
        }

        if (!IsTier(options.DefaultTier))
        {
            return ValidateOptionsResult.Fail("LlmOptimization:DefaultTier must be one of small|medium|full.");
        }

        if (!ValidateTierConfig(options.MaxPromptChars, out var promptError))
        {
            return ValidateOptionsResult.Fail($"LlmOptimization:MaxPromptChars invalid: {promptError}");
        }

        if (!ValidateTierConfig(options.MaxTranscriptSliceChars, out var transcriptError))
        {
            return ValidateOptionsResult.Fail($"LlmOptimization:MaxTranscriptSliceChars invalid: {transcriptError}");
        }

        if (!ValidateTierConfig(options.MaxWorstWindows, out var worstError))
        {
            return ValidateOptionsResult.Fail($"LlmOptimization:MaxWorstWindows invalid: {worstError}");
        }

        if (!ValidateTierConfig(options.MaxPatterns, out var patternsError))
        {
            return ValidateOptionsResult.Fail($"LlmOptimization:MaxPatterns invalid: {patternsError}");
        }

        var low = options.ModelRouting?.ComplexityThresholds?.Low ?? 30;
        var high = options.ModelRouting?.ComplexityThresholds?.High ?? 70;
        if (low < 0 || low > 100 || high < 0 || high > 100 || low > high)
        {
            return ValidateOptionsResult.Fail("LlmOptimization:ModelRouting:ComplexityThresholds must satisfy 0<=Low<=High<=100.");
        }

        return ValidateOptionsResult.Success;
    }

    private static bool ValidateTierConfig(LlmTierIntConfig cfg, out string error)
    {
        if (cfg.Small <= 0 || cfg.Medium <= 0 || cfg.Full <= 0)
        {
            error = "values must be > 0";
            return false;
        }

        error = string.Empty;
        return true;
    }

    private static bool IsTier(string value)
    {
        var v = value.Trim().ToLowerInvariant();
        return v is "small" or "medium" or "full";
    }
}