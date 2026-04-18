using System.Text.Json;
using InterviewCoach.Application;
using InterviewCoach.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public interface IScoringService
{
    ScoreCard ComputeScoreCard(Session session, List<MetricEvent> metrics, Dictionary<string, object>? stats = null);
    List<FeedbackItem> GenerateFeedback(Session session, ScoreCard scoreCard, List<MetricEvent> metrics, Dictionary<string, object>? stats = null);
}

public class ScoringService : IScoringService
{
    private readonly ScoringProfilesOptions _profiles;
    private readonly ILogger<ScoringService> _logger;

    public ScoringService(IOptions<ScoringProfilesOptions> options, ILogger<ScoringService> logger)
    {
        _profiles = options.Value;
        _logger = logger;
    }

    // Backward-compatible constructor for tests that create service directly.
    public ScoringService()
    {
        _profiles = new ScoringProfilesOptions
        {
            DefaultProfile = "general",
            Profiles = new Dictionary<string, ScoringProfile>(StringComparer.OrdinalIgnoreCase)
            {
                ["general"] = new()
                {
                    Weights = new ScoringWeights
                    {
                        EyeContact = 0.25,
                        Posture = 0.15,
                        Fidget = 0.10,
                        SpeakingRate = 0.25,
                        FillerWords = 0.25
                    },
                    Thresholds = new ScoringThresholds
                    {
                        SpeakingRateIdealMinWpm = 120,
                        SpeakingRateIdealMaxWpm = 170,
                        FillerPerMinMax = 6,
                        EyeContactMin = 0.55,
                        HeadJitterMax = 0.35,
                        FidgetMax = 0.40,
                        PostureMin = 0.60
                    }
                }
            }
        };

        _logger = NullLogger<ScoringService>.Instance;
    }

    public ScoreCard ComputeScoreCard(Session session, List<MetricEvent> metrics, Dictionary<string, object>? stats = null)
    {
        var profile = ResolveProfile(session.ScoringProfile, out _);
        var thresholds = profile.Thresholds;
        var weights = profile.Weights;

        var eyeContact = GetAverageUnit(metrics, "eyeContact");
        var posture = GetAverageUnit(metrics, "posture");
        var fidget = GetAverageUnit(metrics, "fidget");

        var eyeContactScore = ComputePositiveThresholdScore(eyeContact, thresholds.EyeContactMin);
        var postureScore = ComputePositiveThresholdScore(posture, thresholds.PostureMin);
        var fidgetScore = ComputeInverseThresholdScore(fidget, thresholds.FidgetMax);

        var speakingRateScore = ComputeSpeakingRateScore(stats, thresholds.SpeakingRateIdealMinWpm, thresholds.SpeakingRateIdealMaxWpm);
        var fillerScore = ComputeFillerScore(stats, thresholds.FillerPerMinMax);

        var overallScore = (int)Math.Round(
            (eyeContactScore * weights.EyeContact)
            + (postureScore * weights.Posture)
            + (fidgetScore * weights.Fidget)
            + (speakingRateScore * weights.SpeakingRate)
            + (fillerScore * weights.FillerWords));

        return new ScoreCard
        {
            SessionId = session.Id,
            EyeContactScore = eyeContactScore,
            SpeakingRateScore = speakingRateScore,
            FillerScore = fillerScore,
            PostureScore = postureScore,
            OverallScore = ClampScore(overallScore)
        };
    }

    public List<FeedbackItem> GenerateFeedback(Session session, ScoreCard scoreCard, List<MetricEvent> metrics, Dictionary<string, object>? stats = null)
    {
        var profile = ResolveProfile(session.ScoringProfile, out _);
        var thresholds = profile.Thresholds;

        var feedback = new List<FeedbackItem>();

        var eyeContact = GetAverageUnit(metrics, "eyeContact");
        var posture = GetAverageUnit(metrics, "posture");
        var fidget = GetAverageUnit(metrics, "fidget");
        var headJitter = GetAverageUnit(metrics, "headJitter");

        var wpm = TryGetInt(stats, "wpm");
        var fillerPerMin = TryGetFillerPerMinute(stats);

        if (eyeContact.HasValue && eyeContact.Value < thresholds.EyeContactMin)
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "EyeContact",
                Severity = SeverityFromGap(thresholds.EyeContactMin - eyeContact.Value, 0.45),
                Title = "Low eye contact",
                Details = $"Average eye contact ({eyeContact.Value:0.##}) is below threshold ({thresholds.EyeContactMin:0.##}).",
                Suggestion = "Maintain steadier gaze toward the camera/interviewer.",
                StartMs = null,
                EndMs = null
            });
        }

        if (posture.HasValue && posture.Value < thresholds.PostureMin)
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "Posture",
                Severity = SeverityFromGap(thresholds.PostureMin - posture.Value, 0.45),
                Title = "Posture needs improvement",
                Details = $"Average posture ({posture.Value:0.##}) is below threshold ({thresholds.PostureMin:0.##}).",
                Suggestion = "Keep shoulders aligned and maintain a stable upright posture.",
                StartMs = null,
                EndMs = null
            });
        }

        if (fidget.HasValue && fidget.Value > thresholds.FidgetMax)
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "Fidget",
                Severity = SeverityFromGap(fidget.Value - thresholds.FidgetMax, 0.6),
                Title = "High fidget level",
                Details = $"Average fidget ({fidget.Value:0.##}) is above threshold ({thresholds.FidgetMax:0.##}).",
                Suggestion = "Reduce unnecessary hand/body movement and pause before gestures.",
                StartMs = null,
                EndMs = null
            });
        }

        if (headJitter.HasValue && headJitter.Value > thresholds.HeadJitterMax)
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "HeadJitter",
                Severity = SeverityFromGap(headJitter.Value - thresholds.HeadJitterMax, 0.6),
                Title = "High head movement jitter",
                Details = $"Average head jitter ({headJitter.Value:0.##}) is above threshold ({thresholds.HeadJitterMax:0.##}).",
                Suggestion = "Keep your head more stable while speaking.",
                StartMs = null,
                EndMs = null
            });
        }

        if (wpm.HasValue && (wpm.Value < thresholds.SpeakingRateIdealMinWpm || wpm.Value > thresholds.SpeakingRateIdealMaxWpm))
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "SpeakingRate",
                Severity = 3,
                Title = "Speaking rate out of target range",
                Details = $"Measured WPM is {wpm.Value}; expected range is {thresholds.SpeakingRateIdealMinWpm}-{thresholds.SpeakingRateIdealMaxWpm}.",
                Suggestion = "Aim for a steadier pace and leave short pauses between ideas.",
                StartMs = null,
                EndMs = null
            });
        }

        if (fillerPerMin.HasValue && fillerPerMin.Value > thresholds.FillerPerMinMax)
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "FillerWords",
                Severity = SeverityFromGap(fillerPerMin.Value - thresholds.FillerPerMinMax, thresholds.FillerPerMinMax),
                Title = "Too many filler words",
                Details = $"Filler rate is {fillerPerMin.Value:0.##} per minute; threshold is {thresholds.FillerPerMinMax}.",
                Suggestion = "Pause briefly instead of using fillers while thinking.",
                StartMs = null,
                EndMs = null
            });
        }

        if (scoreCard.OverallScore >= 80)
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "Overall",
                Severity = 1,
                Title = "Strong overall performance",
                Details = "Current profile evaluation indicates strong interview performance.",
                Suggestion = "Keep this structure and consistency in future interviews.",
                StartMs = null,
                EndMs = null
            });
        }

        return feedback;
    }

    private ScoringProfile ResolveProfile(string? profileName, out string resolvedProfileName)
    {
        var requested = string.IsNullOrWhiteSpace(profileName)
            ? _profiles.DefaultProfile
            : profileName.Trim();

        if (_profiles.Profiles.TryGetValue(requested, out var profile))
        {
            resolvedProfileName = requested;
            return profile;
        }

        resolvedProfileName = _profiles.DefaultProfile;
        _logger.LogWarning(
            "Unknown scoring profile '{profileName}' for session. Falling back to default profile '{defaultProfile}'.",
            profileName,
            _profiles.DefaultProfile);

        return _profiles.GetDefaultProfile();
    }

    private static int ComputePositiveThresholdScore(double? value, double threshold)
    {
        if (!value.HasValue)
            return 50;

        var v = Clamp01(value.Value);
        var t = Math.Max(0.01, Math.Min(0.99, threshold));

        if (v >= t)
        {
            var ratio = (v - t) / (1 - t);
            return ClampScore((int)Math.Round(70 + (ratio * 30)));
        }

        var lowRatio = v / t;
        return ClampScore((int)Math.Round(lowRatio * 70));
    }

    private static int ComputeInverseThresholdScore(double? value, double maxThreshold)
    {
        if (!value.HasValue)
            return 50;

        var v = Clamp01(value.Value);
        var t = Math.Max(0.01, Math.Min(0.99, maxThreshold));

        if (v <= t)
        {
            var ratio = (t - v) / t;
            return ClampScore((int)Math.Round(70 + (ratio * 30)));
        }

        var overRatio = (v - t) / (1 - t);
        return ClampScore((int)Math.Round(70 - (overRatio * 70)));
    }

    private static int ComputeSpeakingRateScore(Dictionary<string, object>? stats, int idealMin, int idealMax)
    {
        var wpm = TryGetInt(stats, "wpm");
        if (!wpm.HasValue)
            return 50;

        if (wpm.Value >= idealMin && wpm.Value <= idealMax)
            return 100;

        if (wpm.Value < idealMin)
        {
            var delta = idealMin - wpm.Value;
            var score = 100 - ((delta / (double)idealMin) * 80);
            return ClampScore((int)Math.Round(score));
        }

        var highDelta = wpm.Value - idealMax;
        var highScore = 100 - ((highDelta / (double)idealMax) * 80);
        return ClampScore((int)Math.Round(highScore));
    }

    private static int ComputeFillerScore(Dictionary<string, object>? stats, int fillerPerMinMax)
    {
        var fillerPerMin = TryGetFillerPerMinute(stats);
        if (!fillerPerMin.HasValue)
            return 50;

        if (fillerPerMin.Value <= 0)
            return 100;

        var max = Math.Max(0.1, fillerPerMinMax);
        if (fillerPerMin.Value <= max)
        {
            var ratio = fillerPerMin.Value / max;
            return ClampScore((int)Math.Round(100 - (ratio * 25)));
        }

        var overflow = (fillerPerMin.Value - max) / max;
        return ClampScore((int)Math.Round(75 - (overflow * 75)));
    }

    private static double? TryGetFillerPerMinute(Dictionary<string, object>? stats)
    {
        var fillerCount = TryGetInt(stats, "filler_count");
        var durationMs = TryGetLong(stats, "duration_ms");

        if (!fillerCount.HasValue || !durationMs.HasValue || durationMs.Value <= 0)
            return null;

        var durationMin = durationMs.Value / 60000d;
        if (durationMin <= 0)
            return null;

        return fillerCount.Value / durationMin;
    }

    private static int? TryGetInt(Dictionary<string, object>? stats, string key)
    {
        if (stats == null || !stats.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is int i)
            return i;

        if (value is long l && l <= int.MaxValue && l >= int.MinValue)
            return (int)l;

        if (int.TryParse(value.ToString(), out var parsed))
            return parsed;

        return null;
    }

    private static long? TryGetLong(Dictionary<string, object>? stats, string key)
    {
        if (stats == null || !stats.TryGetValue(key, out var value) || value == null)
            return null;

        if (value is long l)
            return l;

        if (value is int i)
            return i;

        if (long.TryParse(value.ToString(), out var parsed))
            return parsed;

        return null;
    }

    private static double? GetAverageUnit(List<MetricEvent> metrics, string key)
    {
        var values = new List<double>();

        foreach (var metric in metrics)
        {
            if (TryExtractMetricValue(metric, key, out var raw))
            {
                values.Add(NormalizeUnitMetric(raw));
            }
        }

        if (values.Count == 0)
            return null;

        return values.Average();
    }

    private static bool TryExtractMetricValue(MetricEvent metric, string requestedKey, out double value)
    {
        value = 0;

        try
        {
            using var doc = JsonDocument.Parse(metric.PayloadJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in root.EnumerateObject())
                {
                    if (!string.Equals(NormalizeMetricKey(prop.Name), NormalizeMetricKey(requestedKey), StringComparison.Ordinal))
                        continue;

                    if (prop.Value.ValueKind == JsonValueKind.Number && prop.Value.TryGetDouble(out var propValue))
                    {
                        value = propValue;
                        return true;
                    }
                }
            }

            if (root.ValueKind == JsonValueKind.Number
                && root.TryGetDouble(out var numeric)
                && string.Equals(NormalizeMetricKey(metric.Type), NormalizeMetricKey(requestedKey), StringComparison.Ordinal))
            {
                value = numeric;
                return true;
            }
        }
        catch
        {
            return false;
        }

        return false;
    }

    private static string NormalizeMetricKey(string raw)
    {
        return (raw ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Replace("_", string.Empty)
            .Replace("-", string.Empty)
            .Replace(" ", string.Empty);
    }

    private static double NormalizeUnitMetric(double value)
    {
        if (double.IsNaN(value) || double.IsInfinity(value))
            return 0;

        // Accept both 0..1 and 0..100 payloads.
        if (value > 1.5)
            value /= 100d;

        return Clamp01(value);
    }

    private static int SeverityFromGap(double delta, double scale)
    {
        if (scale <= 0)
            return 3;

        var ratio = delta / scale;
        if (ratio >= 0.8) return 5;
        if (ratio >= 0.5) return 4;
        if (ratio >= 0.25) return 3;
        if (ratio > 0) return 2;
        return 1;
    }

    private static int ClampScore(int score) => Math.Clamp(score, 0, 100);

    private static double Clamp01(double value) => Math.Clamp(value, 0d, 1d);
}
