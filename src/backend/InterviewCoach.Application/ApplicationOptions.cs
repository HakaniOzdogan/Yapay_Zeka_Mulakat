namespace InterviewCoach.Application;

public class ScoringProfilesOptions
{
    public string DefaultProfile { get; set; } = "general";

    public Dictionary<string, ScoringProfile> Profiles { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public bool TryGetProfile(string? profileName, out ScoringProfile profile)
    {
        var requested = string.IsNullOrWhiteSpace(profileName) ? DefaultProfile : profileName.Trim();
        return Profiles.TryGetValue(requested, out profile!);
    }

    public string[] GetProfileNames()
    {
        return Profiles.Keys.OrderBy(x => x, StringComparer.OrdinalIgnoreCase).ToArray();
    }

    public ScoringProfile GetDefaultProfile()
    {
        if (Profiles.TryGetValue(DefaultProfile, out var profile))
            return profile;

        throw new InvalidOperationException($"Default scoring profile '{DefaultProfile}' is missing.");
    }
}

public class ScoringProfile
{
    public ScoringWeights Weights { get; set; } = new();
    public ScoringThresholds Thresholds { get; set; } = new();
}

public class ScoringWeights
{
    public double EyeContact { get; set; }
    public double Posture { get; set; }
    public double Fidget { get; set; }
    public double SpeakingRate { get; set; }
    public double FillerWords { get; set; }

    public double Sum => EyeContact + Posture + Fidget + SpeakingRate + FillerWords;
}

public class ScoringThresholds
{
    public int SpeakingRateIdealMinWpm { get; set; } = 120;
    public int SpeakingRateIdealMaxWpm { get; set; } = 170;
    public int FillerPerMinMax { get; set; } = 6;
    public double EyeContactMin { get; set; } = 0.55;
    public double HeadJitterMax { get; set; } = 0.35;
    public double FidgetMax { get; set; } = 0.40;
    public double PostureMin { get; set; } = 0.60;
}

public class AuthOptions
{
    public string JwtIssuer { get; set; } = "InterviewCoach";
    public string JwtAudience { get; set; } = "InterviewCoach.Client";
    public string JwtKey { get; set; } = "dev-only-change-this-key-to-a-long-random-secret";
    public int AccessTokenMinutes { get; set; } = 120;
    public string? SeedAdminEmail { get; set; }
    public string? SeedAdminPassword { get; set; }
}
