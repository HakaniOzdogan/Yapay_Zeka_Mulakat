using System.Text.Json;
using InterviewCoach.Application;
using InterviewCoach.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public interface IEvidenceSummaryService
{
    Task<EvidenceSummaryDto?> BuildAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public class EvidenceSummaryService : IEvidenceSummaryService
{
    private const int WindowMs = 30000;

    private readonly ApplicationDbContext _db;
    private readonly ScoringProfilesOptions _profiles;

    public EvidenceSummaryService(ApplicationDbContext db, IOptions<ScoringProfilesOptions> options)
    {
        _db = db;
        _profiles = options.Value;
    }

    public async Task<EvidenceSummaryDto?> BuildAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var session = await _db.Sessions
            .AsNoTracking()
            .Where(s => s.Id == sessionId)
            .Select(s => new SessionRead
            {
                Id = s.Id,
                Language = s.Language
            })
            .FirstOrDefaultAsync(cancellationToken);

        if (session == null)
            return null;

        var overallScore = await _db.ScoreCards
            .AsNoTracking()
            .Where(sc => sc.SessionId == sessionId)
            .Select(sc => (int?)sc.OverallScore)
            .FirstOrDefaultAsync(cancellationToken);

        var patterns = await _db.FeedbackItems
            .AsNoTracking()
            .Where(p => p.SessionId == sessionId)
            .OrderByDescending(p => p.Severity)
            .Select(p => new PatternSummaryDto
            {
                Type = p.Category,
                StartMs = p.StartMs,
                EndMs = p.EndMs,
                Severity = p.Severity,
                Evidence = p.Details
            })
            .ToListAsync(cancellationToken);

        var transcript = await _db.TranscriptSegments
            .AsNoTracking()
            .Where(t => t.SessionId == sessionId)
            .OrderBy(t => t.StartMs)
            .Select(t => new TranscriptRead
            {
                StartMs = t.StartMs,
                EndMs = t.EndMs,
                Text = t.Text
            })
            .ToListAsync(cancellationToken);

        var events = await _db.MetricEvents
            .AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.TsMs)
            .Select(e => new MetricRead
            {
                TsMs = e.TsMs,
                Type = e.Type,
                PayloadJson = e.PayloadJson
            })
            .ToListAsync(cancellationToken);

        var windows = BuildWindows(events);

        var maxDerivedEnd = windows.Count == 0 ? 0 : windows.Max(w => w.EndMs);
        var maxTranscriptEnd = transcript.Count == 0 ? 0 : transcript.Max(t => t.EndMs);
        var maxMetricTs = events.Count == 0 ? 0 : events.Max(e => e.TsMs);

        var durationMs = maxDerivedEnd > 0
            ? maxDerivedEnd
            : (maxTranscriptEnd > 0 ? maxTranscriptEnd : Math.Max(0, maxMetricTs));

        var signals = ComputeSignals(windows, durationMs);

        var worstWindows = windows
            .Select(w => new { Window = w, Badness = ComputeBadness(w) })
            .OrderByDescending(x => x.Badness)
            .ThenBy(x => x.Window.StartMs)
            .Take(5)
            .Select(x => new WorstWindowDto
            {
                StartMs = x.Window.StartMs,
                EndMs = x.Window.EndMs,
                Metrics = new WindowMetricsDto
                {
                    EyeContact = x.Window.EyeContact,
                    Posture = x.Window.Posture,
                    Fidget = x.Window.Fidget,
                    HeadJitter = x.Window.HeadJitter,
                    Wpm = x.Window.Wpm,
                    Filler = x.Window.Filler,
                    PauseMs = x.Window.PauseMs
                },
                Reason = BuildReason(x.Window)
            })
            .ToList();

        var topIssues = patterns
            .OrderByDescending(p => p.Severity)
            .Take(3)
            .Select(p => new TopIssueDto
            {
                Issue = p.Type,
                Evidence = p.Evidence,
                TimeRangeMs = [p.StartMs ?? 0, p.EndMs ?? (p.StartMs ?? 0)]
            })
            .ToList();

        var slices = BuildTranscriptSlices(transcript, worstWindows, patterns)
            .Take(10)
            .ToList();

        return new EvidenceSummaryDto
        {
            SessionId = session.Id,
            Language = string.IsNullOrWhiteSpace(session.Language) ? "unknown" : session.Language,
            HighLevel = new HighLevelDto
            {
                DurationMs = durationMs,
                OverallScore = overallScore,
                TopIssues = topIssues
            },
            Signals = signals,
            WorstWindows = worstWindows,
            TranscriptSlices = slices,
            Patterns = patterns
        };
    }

    private SignalsDto ComputeSignals(List<MetricWindow> windows, long durationMs)
    {
        var durationMinutes = Math.Max(durationMs / 60000d, 0.0001d);

        var vision = new VisionSignalsDto
        {
            EyeContactAvg = Mean(windows.Select(w => w.EyeContact)),
            PostureAvg = Mean(windows.Select(w => w.Posture)),
            FidgetAvg = Mean(windows.Select(w => w.Fidget)),
            HeadJitterAvg = Mean(windows.Select(w => w.HeadJitter))
        };

        var wpmValues = windows.Select(w => w.Wpm).Where(v => v.HasValue).Select(v => v!.Value).OrderBy(v => v).ToList();
        var wpmMedian = wpmValues.Count == 0
            ? (double?)null
            : (wpmValues.Count % 2 == 1
                ? wpmValues[wpmValues.Count / 2]
                : (wpmValues[(wpmValues.Count / 2) - 1] + wpmValues[wpmValues.Count / 2]) / 2d);

        var fillerTotal = windows.Select(w => w.Filler).Where(v => v.HasValue).Select(v => v!.Value).Sum();
        var pauseMsTotal = windows.Select(w => w.PauseMs).Where(v => v.HasValue).Select(v => v!.Value).Sum();

        var audio = new AudioSignalsDto
        {
            WpmMedian = wpmMedian,
            FillerPerMin = windows.Any(w => w.Filler.HasValue) ? fillerTotal / durationMinutes : null,
            PauseMsPerMin = windows.Any(w => w.PauseMs.HasValue) ? pauseMsTotal / durationMinutes : null
        };

        return new SignalsDto
        {
            Vision = vision,
            Audio = audio
        };
    }

    private List<TranscriptSliceDto> BuildTranscriptSlices(
        List<TranscriptRead> transcript,
        List<WorstWindowDto> worstWindows,
        List<PatternSummaryDto> patterns)
    {
        var ranges = new List<(long Start, long End)>();
        ranges.AddRange(worstWindows.Select(w => (w.StartMs, w.EndMs)));
        ranges.AddRange(patterns
            .Where(p => p.StartMs.HasValue || p.EndMs.HasValue)
            .Select(p => (p.StartMs ?? p.EndMs ?? 0, p.EndMs ?? p.StartMs ?? 0)));

        var result = new List<TranscriptSliceDto>();

        foreach (var range in ranges.OrderBy(r => r.Start))
        {
            var overlapped = transcript
                .Where(t => t.EndMs >= range.Start && t.StartMs <= range.End)
                .ToList();

            if (overlapped.Count == 0)
                continue;

            var start = overlapped.Min(x => x.StartMs);
            var end = overlapped.Max(x => x.EndMs);

            if (result.Any(r => !(end < r.StartMs || start > r.EndMs)))
                continue;

            var text = string.Join(" ", overlapped.Select(x => x.Text).Where(x => !string.IsNullOrWhiteSpace(x))).Trim();
            if (text.Length > 800)
                text = text[..800];

            result.Add(new TranscriptSliceDto
            {
                StartMs = start,
                EndMs = end,
                Text = text
            });

            if (result.Count >= 10)
                break;
        }

        return result;
    }

    private List<MetricWindow> BuildWindows(List<MetricRead> events)
    {
        var windows = new Dictionary<long, MetricWindow>();

        foreach (var evt in events)
        {
            var winStart = (evt.TsMs / WindowMs) * WindowMs;
            var winEnd = winStart + WindowMs;

            if (!windows.TryGetValue(winStart, out var window))
            {
                window = new MetricWindow
                {
                    StartMs = winStart,
                    EndMs = winEnd
                };
                windows[winStart] = window;
            }

            foreach (var metric in ExtractMetrics(evt))
            {
                window.Add(metric.Key, metric.Value);
            }
        }

        return windows.Values
            .OrderBy(w => w.StartMs)
            .ToList();
    }

    private double ComputeBadness(MetricWindow w)
    {
        var thresholds = _profiles.GetDefaultProfile().Thresholds;

        double wpmPenalty = 0;
        if (w.Wpm.HasValue)
        {
            var wpm = w.Wpm.Value;
            var idealMin = Math.Max(1, thresholds.SpeakingRateIdealMinWpm);
            var idealMax = Math.Max(1, thresholds.SpeakingRateIdealMaxWpm);
            wpmPenalty = Math.Max(0, (wpm - idealMax) / idealMax) + Math.Max(0, (idealMin - wpm) / (double)idealMin);
        }

        var fillerPenalty = w.Filler.HasValue && w.Filler.Value >= Math.Max(1, thresholds.FillerPerMinMax) ? 0.5 : 0;
        var pausePenalty = w.PauseMs.HasValue && w.PauseMs.Value >= 1500 ? 0.5 : 0;

        return (1 - (w.EyeContact ?? 1))
             + (1 - (w.Posture ?? 1))
             + (w.Fidget ?? 0)
             + (w.HeadJitter ?? 0)
             + wpmPenalty
             + fillerPenalty
             + pausePenalty;
    }

    private static string BuildReason(MetricWindow w)
    {
        var reasons = new List<string>();

        if (w.EyeContact.HasValue && w.EyeContact.Value < 0.5) reasons.Add("low eye contact");
        if (w.Posture.HasValue && w.Posture.Value < 0.5) reasons.Add("weak posture");
        if (w.Fidget.HasValue && w.Fidget.Value > 0.5) reasons.Add("high fidget");
        if (w.HeadJitter.HasValue && w.HeadJitter.Value > 0.5) reasons.Add("high head jitter");
        if (w.Wpm.HasValue && (w.Wpm.Value < 120 || w.Wpm.Value > 160)) reasons.Add("off-range speaking rate");
        if (w.Filler.HasValue && w.Filler.Value >= 2) reasons.Add("filler spike");
        if (w.PauseMs.HasValue && w.PauseMs.Value >= 1500) reasons.Add("long pauses");

        return reasons.Count == 0 ? "composite metric decline" : string.Join(", ", reasons);
    }

    private static double? Mean(IEnumerable<double?> values)
    {
        var list = values.Where(v => v.HasValue).Select(v => v!.Value).ToList();
        return list.Count == 0 ? null : list.Average();
    }

    private static Dictionary<string, double> ExtractMetrics(MetricRead evt)
    {
        var result = new Dictionary<string, double>();
        var typeKey = NormalizeMetricKey(evt.Type);

        try
        {
            using var doc = JsonDocument.Parse(evt.PayloadJson);
            var root = doc.RootElement;

            if (root.ValueKind == JsonValueKind.Number && root.TryGetDouble(out var numeric))
            {
                if (!string.IsNullOrEmpty(typeKey))
                    result[typeKey] = numeric;
                return result;
            }

            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (property.Value.ValueKind == JsonValueKind.Number && property.Value.TryGetDouble(out var val))
                    {
                        var key = NormalizeMetricKey(property.Name);
                        if (!string.IsNullOrEmpty(key))
                            result[key] = val;
                    }
                }
            }
        }
        catch
        {
            return result;
        }

        if (result.Count == 0 && !string.IsNullOrEmpty(typeKey))
        {
            result[typeKey] = 0;
        }

        return result;
    }

    private static string NormalizeMetricKey(string raw)
    {
        var k = (raw ?? string.Empty).Trim().ToLowerInvariant();
        k = k.Replace("_", string.Empty).Replace("-", string.Empty).Replace(" ", string.Empty);

        return k switch
        {
            "eyecontact" => "eyeContact",
            "posture" => "posture",
            "fidget" => "fidget",
            "headjitter" => "headJitter",
            "headstability" => "headJitter",
            "wpm" => "wpm",
            "speechrate" => "wpm",
            "filler" => "filler",
            "fillercount" => "filler",
            "fillerwords" => "filler",
            "pausems" => "pauseMs",
            "pause" => "pauseMs",
            "pausecount" => "pauseMs",
            _ => string.Empty
        };
    }

    private sealed class SessionRead
    {
        public Guid Id { get; set; }
        public string Language { get; set; } = "unknown";
    }

    private sealed class MetricRead
    {
        public long TsMs { get; set; }
        public string Type { get; set; } = string.Empty;
        public string PayloadJson { get; set; } = "{}";
    }

    private sealed class TranscriptRead
    {
        public long StartMs { get; set; }
        public long EndMs { get; set; }
        public string Text { get; set; } = string.Empty;
    }

    private sealed class MetricWindow
    {
        private readonly Dictionary<string, List<double>> _values = new();

        public long StartMs { get; set; }
        public long EndMs { get; set; }

        public double? EyeContact => GetAvg("eyeContact");
        public double? Posture => GetAvg("posture");
        public double? Fidget => GetAvg("fidget");
        public double? HeadJitter => GetAvg("headJitter");
        public double? Wpm => GetAvg("wpm");
        public double? Filler => GetAvg("filler");
        public double? PauseMs => GetAvg("pauseMs");

        public void Add(string key, double value)
        {
            if (string.IsNullOrEmpty(key))
                return;

            if (!_values.TryGetValue(key, out var list))
            {
                list = [];
                _values[key] = list;
            }

            list.Add(value);
        }

        private double? GetAvg(string key)
        {
            if (!_values.TryGetValue(key, out var values) || values.Count == 0)
                return null;

            return values.Average();
        }
    }
}

public class EvidenceSummaryDto
{
    public Guid SessionId { get; set; }
    public string Language { get; set; } = "unknown";
    public HighLevelDto HighLevel { get; set; } = new();
    public SignalsDto Signals { get; set; } = new();
    public List<WorstWindowDto> WorstWindows { get; set; } = [];
    public List<TranscriptSliceDto> TranscriptSlices { get; set; } = [];
    public List<PatternSummaryDto> Patterns { get; set; } = [];
}

public class HighLevelDto
{
    public long DurationMs { get; set; }
    public int? OverallScore { get; set; }
    public List<TopIssueDto> TopIssues { get; set; } = [];
}

public class TopIssueDto
{
    public string Issue { get; set; } = string.Empty;
    public string Evidence { get; set; } = string.Empty;
    public long[] TimeRangeMs { get; set; } = [0, 0];
}

public class SignalsDto
{
    public VisionSignalsDto Vision { get; set; } = new();
    public AudioSignalsDto Audio { get; set; } = new();
}

public class VisionSignalsDto
{
    public double? EyeContactAvg { get; set; }
    public double? PostureAvg { get; set; }
    public double? FidgetAvg { get; set; }
    public double? HeadJitterAvg { get; set; }
}

public class AudioSignalsDto
{
    public double? WpmMedian { get; set; }
    public double? FillerPerMin { get; set; }
    public double? PauseMsPerMin { get; set; }
}

public class WorstWindowDto
{
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public WindowMetricsDto Metrics { get; set; } = new();
    public string Reason { get; set; } = string.Empty;
}

public class WindowMetricsDto
{
    public double? EyeContact { get; set; }
    public double? Posture { get; set; }
    public double? Fidget { get; set; }
    public double? HeadJitter { get; set; }
    public double? Wpm { get; set; }
    public double? Filler { get; set; }
    public double? PauseMs { get; set; }
}

public class TranscriptSliceDto
{
    public long StartMs { get; set; }
    public long EndMs { get; set; }
    public string Text { get; set; } = string.Empty;
}

public class PatternSummaryDto
{
    public string Type { get; set; } = string.Empty;
    public long? StartMs { get; set; }
    public long? EndMs { get; set; }
    public int Severity { get; set; }
    public string Evidence { get; set; } = string.Empty;
}
