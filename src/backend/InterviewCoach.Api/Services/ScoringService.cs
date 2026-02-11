using System.Text.Json;
using InterviewCoach.Domain;

namespace InterviewCoach.Api.Services;

public interface IScoringService
{
    ScoreCard ComputeScoreCard(Session session, List<MetricEvent> metrics, Dictionary<string, object>? stats = null);
    List<FeedbackItem> GenerateFeedback(Session session, ScoreCard scoreCard, List<MetricEvent> metrics, Dictionary<string, object>? stats = null);
}

public class ScoringService : IScoringService
{
    // Configuration thresholds
    private const float EyeContactThresholdGood = 0.7f; // 70% eye contact considered good
    private const float EyeContactThresholdFair = 0.4f; // 40% eye contact considered fair
    
    private const float HeadStabilityThresholdGood = 10f; // Max 10° variance
    private const float HeadStabilityThresholdFair = 20f; // Max 20° variance
    
    private const int WpmIdealMin = 120;
    private const int WpmIdealMax = 160;
    
    private const float FillerPerMinGood = 2f;
    private const float FillerPerMinFair = 4f;
    
    private const float PostureThresholdGood = 0.7f;
    private const float PostureThresholdFair = 0.4f;

    public ScoreCard ComputeScoreCard(Session session, List<MetricEvent> metrics, Dictionary<string, object>? stats = null)
    {
        var eyeContactScore = ComputeEyeContactScore(metrics);
        var speakingRateScore = ComputeSpeakingRateScore(stats);
        var fillerScore = ComputeFillerScore(stats);
        var postureScore = ComputePostureScore(metrics);

        // Weighted average: 25% eye contact, 25% speaking rate, 20% filler, 30% posture
        var overallScore = (int)((eyeContactScore * 0.25f) + 
                                 (speakingRateScore * 0.25f) + 
                                 (fillerScore * 0.20f) + 
                                 (postureScore * 0.30f));

        return new ScoreCard
        {
            SessionId = session.Id,
            EyeContactScore = eyeContactScore,
            SpeakingRateScore = speakingRateScore,
            FillerScore = fillerScore,
            PostureScore = postureScore,
            OverallScore = Math.Max(0, Math.Min(100, overallScore))
        };
    }

    public List<FeedbackItem> GenerateFeedback(Session session, ScoreCard scoreCard, List<MetricEvent> metrics, Dictionary<string, object>? stats = null)
    {
        var feedback = new List<FeedbackItem>();

        // Eye contact feedback
        if (scoreCard.EyeContactScore < 60)
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "Eye Contact",
                Severity = scoreCard.EyeContactScore < 40 ? 5 : 3,
                Title = "Göz teması geliştirilebilir",
                Details = scoreCard.EyeContactScore < 40 
                    ? "Görüşmeci ile yeterli göz teması kurmadığınız gözlendi." 
                    : "Göz teması tutarlılığını artırmalısınız.",
                Suggestion = "Görüşmecinin gözlerine doğru bakın, ara sıra bakışlarını değiştirin ama düşüncelerinizi ifade ederken göz temasını korumuşsunuz."
            });
        }
        else if (scoreCard.EyeContactScore >= 80)
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "Eye Contact",
                Severity = 1,
                Title = "Harika göz teması",
                Details = "Göz teması konusunda çok iyi bir performans gösterdiniz.",
                Suggestion = "Bu özelliğinizi devam ettirin - bu çok profesyonel görünüyor."
            });
        }

        // Speaking rate feedback
        if (scoreCard.SpeakingRateScore < 60)
        {
            int wpm = 0;
            var hasWpm = stats?.TryGetValue("wpm", out var wpmObj) == true &&
                         int.TryParse(wpmObj?.ToString(), out wpm);
            var isSlow = hasWpm && wpm < WpmIdealMin;
            var wpmText = hasWpm ? wpm.ToString() : "bilinmiyor";
            
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "Konuşma Hızı",
                Severity = scoreCard.SpeakingRateScore < 40 ? 4 : 2,
                Title = isSlow ? "Konuşma hızı çok düşük" : "Konuşma hızı çok yüksek",
                Details = isSlow 
                    ? $"Konuşma hızınız dakikada {wpmText} kelime - ideal aralık {WpmIdealMin}-{WpmIdealMax}." 
                    : $"Konuşma hızınız dakikada {wpmText} kelime - ideal aralık {WpmIdealMin}-{WpmIdealMax}.",
                Suggestion = isSlow 
                    ? "Cümlelerinizi daha net ve dinamik bir hızda kurmaya çalışın." 
                    : "Daha yavaş ve düşünceli konuşmaya çalışın, dinleyicinin takip etmesine izin verin."
            });
        }

        // Filler words feedback
        if (scoreCard.FillerScore < 60)
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "Dolgu Sözcükler",
                Severity = scoreCard.FillerScore < 40 ? 4 : 2,
                Title = "Dolgu sözcükleri azaltılabilir",
                Details = scoreCard.FillerScore < 40 
                    ? "Konuşmanızda çok sayıda dolgu sözcüğü (eee, şey, yani vb.) kullandığınız gözlendi." 
                    : "Dolgu sözcükleri zaman zaman kullanıldığını fark ettim.",
                Suggestion = "Sessiz kalmaktan korkmayın. Düşünmek için ses çıkarmak yerine bir saniye bekleyin - daha profesyonel görünecek."
            });
        }

        // Posture feedback
        if (scoreCard.PostureScore < 60)
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "Duruş",
                Severity = scoreCard.PostureScore < 40 ? 4 : 2,
                Title = "Duruş ve vücut dili iyileştirilebilir",
                Details = scoreCard.PostureScore < 40 
                    ? "Özellikle oturuş pozisyonunuzda ve vücut hareketlerinde tutarsızlık fark ettim." 
                    : "Duruş ve vücut dili biraz daha sabit tutulabilir.",
                Suggestion = "Doğru oturun, gereksiz hareket etmekten kaçının. Hareketler amaca yönelik (vurgu, açıklama) olmalıdır."
            });
        }
        else if (scoreCard.PostureScore >= 80)
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "Duruş",
                Severity = 1,
                Title = "İyi vücut dili",
                Details = "Duruşunuz ve vücut diliniz çok profesyonel görünüyor.",
                Suggestion = "Bu güven ve stabiliteyi devam ettirin."
            });
        }

        // Overall performance
        if (scoreCard.OverallScore >= 80)
        {
            feedback.Add(new FeedbackItem
            {
                SessionId = session.Id,
                Category = "Genel",
                Severity = 1,
                Title = "Mükemmel performans",
                Details = "Genel olarak çok iyi bir mülakat performansı gösterdiniz.",
                Suggestion = "Bu kendine güvenin ve profesyonelliği devam ettirin."
            });
        }

        return feedback;
    }

    private int ComputeEyeContactScore(List<MetricEvent> metrics)
    {
        var eyeContactValues = metrics
            .Where(m => m.Type == "combined")
            .Select(m => ExtractValue(m.ValueJson, "eyeContact"))
            .Where(v => v.HasValue)
            .ToList();

        if (eyeContactValues.Count == 0)
            return 50; // Default if no data

        var averageEyeContact = eyeContactValues.Average(v => v!.Value);
        var normalizedScore = (averageEyeContact / 100f) * 100; // Already 0-100 from MetricsComputer

        return Math.Max(0, Math.Min(100, (int)normalizedScore));
    }

    private int ComputeSpeakingRateScore(Dictionary<string, object>? stats)
    {
        if (stats == null || !stats.TryGetValue("wpm", out var wpmObj))
            return 50; // Default if no data

        if (!int.TryParse(wpmObj?.ToString(), out var wpm))
            return 50;

        if (wpm >= WpmIdealMin && wpm <= WpmIdealMax)
            return 100; // Perfect

        if (wpm < WpmIdealMin)
        {
            // Penalize for being too slow
            var deviation = WpmIdealMin - wpm;
            var score = 100 - (deviation / 2); // Lose ~0.5 points per WPM below minimum
            return Math.Max(20, (int)score);
        }
        else
        {
            // Penalize for being too fast
            var deviation = wpm - WpmIdealMax;
            var score = 100 - (deviation / 3); // Lose ~0.33 points per WPM above maximum
            return Math.Max(20, (int)score);
        }
    }

    private int ComputeFillerScore(Dictionary<string, object>? stats)
    {
        if (stats == null)
            return 50;

        if (!stats.TryGetValue("filler_count", out var fillerCountObj) || 
            !stats.TryGetValue("duration_ms", out var durationObj))
            return 50;

        if (!int.TryParse(fillerCountObj?.ToString(), out var fillerCount) ||
            !long.TryParse(durationObj?.ToString(), out var durationMs))
            return 50;

        if (durationMs == 0 || fillerCount == 0)
            return 100; // No fillers is perfect

        var durationMin = durationMs / 60000f;
        var fillerPerMin = fillerCount / Math.Max(durationMin, 0.1f);

        if (fillerPerMin <= FillerPerMinGood)
            return 100; // Excellent

        if (fillerPerMin <= FillerPerMinFair)
        {
            var score = 100 - ((fillerPerMin - FillerPerMinGood) / (FillerPerMinFair - FillerPerMinGood)) * 40;
            return Math.Max(50, (int)score);
        }

        // More than fair threshold
        var penaltyScore = 50 - ((fillerPerMin - FillerPerMinFair) / 2);
        return Math.Max(20, (int)penaltyScore);
    }

    private int ComputePostureScore(List<MetricEvent> metrics)
    {
        var postureValues = metrics
            .Where(m => m.Type == "combined")
            .Select(m => ExtractValue(m.ValueJson, "posture"))
            .Where(v => v.HasValue)
            .ToList();

        var fidgetValues = metrics
            .Where(m => m.Type == "combined")
            .Select(m => ExtractValue(m.ValueJson, "fidget"))
            .Where(v => v.HasValue)
            .ToList();

        if (postureValues.Count == 0 && fidgetValues.Count == 0)
            return 50; // Default if no data

        var avgPosture = postureValues.Count > 0 ? postureValues.Average(v => v!.Value) : 50;
        var avgFidget = fidgetValues.Count > 0 ? fidgetValues.Average(v => v!.Value) : 50;

        // Average of both normalized scores
        var normalizedScore = (avgPosture + avgFidget) / 2;

        return Math.Max(0, Math.Min(100, (int)normalizedScore));
    }

    private float? ExtractValue(string valueJson, string key)
    {
        try
        {
            using var doc = JsonDocument.Parse(valueJson);
            if (doc.RootElement.TryGetProperty(key, out var element))
            {
                if (element.TryGetSingle(out var value))
                    return value;
                if (element.TryGetDouble(out var doubleValue))
                    return (float)doubleValue;
            }
        }
        catch { }

        return null;
    }
}


