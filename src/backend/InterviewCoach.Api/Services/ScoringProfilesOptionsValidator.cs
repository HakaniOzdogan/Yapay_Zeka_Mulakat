using InterviewCoach.Application;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public class ScoringProfilesOptionsValidator : IValidateOptions<ScoringProfilesOptions>
{
    public ValidateOptionsResult Validate(string? name, ScoringProfilesOptions options)
    {
        var errors = new List<string>();

        if (string.IsNullOrWhiteSpace(options.DefaultProfile))
            errors.Add("ScoringProfiles:DefaultProfile is required.");

        if (options.Profiles == null || options.Profiles.Count == 0)
        {
            errors.Add("ScoringProfiles:Profiles must include at least one profile.");
            return ValidateOptionsResult.Fail(errors);
        }

        if (!options.Profiles.ContainsKey(options.DefaultProfile))
            errors.Add($"Default profile '{options.DefaultProfile}' was not found under ScoringProfiles:Profiles.");

        foreach (var kvp in options.Profiles)
        {
            var profileName = kvp.Key;
            var profile = kvp.Value ?? new ScoringProfile();
            var thresholds = profile.Thresholds ?? new ScoringThresholds();
            var weights = profile.Weights ?? new ScoringWeights();

            var sum = weights.Sum;
            if (Math.Abs(sum - 1.0d) > 0.05d)
                errors.Add($"Profile '{profileName}' weights must sum approximately to 1.0 (actual={sum:0.###}).");

            if (thresholds.SpeakingRateIdealMinWpm <= 0 || thresholds.SpeakingRateIdealMaxWpm <= 0)
                errors.Add($"Profile '{profileName}' speaking rate thresholds must be positive.");

            if (thresholds.SpeakingRateIdealMinWpm >= thresholds.SpeakingRateIdealMaxWpm)
                errors.Add($"Profile '{profileName}' speakingRateIdealMinWpm must be less than speakingRateIdealMaxWpm.");

            if (thresholds.FillerPerMinMax <= 0)
                errors.Add($"Profile '{profileName}' fillerPerMinMax must be positive.");

            if (thresholds.EyeContactMin < 0 || thresholds.EyeContactMin > 1)
                errors.Add($"Profile '{profileName}' eyeContactMin must be in [0,1].");

            if (thresholds.HeadJitterMax <= 0 || thresholds.HeadJitterMax > 1)
                errors.Add($"Profile '{profileName}' headJitterMax must be in (0,1].");

            if (thresholds.FidgetMax <= 0 || thresholds.FidgetMax > 1)
                errors.Add($"Profile '{profileName}' fidgetMax must be in (0,1].");

            if (thresholds.PostureMin < 0 || thresholds.PostureMin > 1)
                errors.Add($"Profile '{profileName}' postureMin must be in [0,1].");
        }

        return errors.Count == 0 ? ValidateOptionsResult.Success : ValidateOptionsResult.Fail(errors);
    }
}
