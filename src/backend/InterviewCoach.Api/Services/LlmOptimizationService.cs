using System.Text.Json;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public interface ILlmOptimizationService
{
    LlmOptimizationPlan BuildPlan(EvidenceSummaryDto summary, string? requestedTier = null);
}

public class LlmOptimizationService : ILlmOptimizationService
{
    private const int SystemPromptEstimateChars = 700;
    private const int InstructionEstimateChars = 600;

    private readonly LlmOptimizationOptions _options;
    private readonly LlmOptions _llmOptions;

    public LlmOptimizationService(IOptions<LlmOptimizationOptions> options, IOptions<LlmOptions> llmOptions)
    {
        _options = options.Value ?? new LlmOptimizationOptions();
        _llmOptions = llmOptions.Value ?? new LlmOptions();
    }

    public LlmOptimizationPlan BuildPlan(EvidenceSummaryDto summary, string? requestedTier = null)
    {
        var originalJson = JsonSerializer.Serialize(summary);
        var complexity = BuildComplexity(summary);

        var requested = ParseTier(string.IsNullOrWhiteSpace(requestedTier) ? _options.DefaultTier : requestedTier);
        if (_options.ForceFullForDebug)
        {
            requested = LlmEvidenceTier.Full;
        }

        var selected = SelectTierByComplexity(requested, complexity.Band);
        var warnings = new List<string>();

        if (!_options.Enabled)
        {
            var disabledBudget = GetPromptBudget(selected);
            var disabledPromptChars = EstimatePromptChars(originalJson.Length);
            var disabledModel = _llmOptions.PrimaryModel;

            return new LlmOptimizationPlan
            {
                Enabled = false,
                TierRequested = requested,
                TierUsed = selected,
                ComplexityScore = complexity.Score,
                ComplexityBand = complexity.Band,
                CompactedSummary = summary,
                OriginalEvidenceChars = originalJson.Length,
                CompactedEvidenceChars = originalJson.Length,
                PromptEstimatedChars = disabledPromptChars,
                PromptBudgetChars = disabledBudget,
                ModelRoutedFromBand = "Disabled",
                ModelChosen = disabledModel,
                TruncationApplied = false,
                Warnings = warnings,
                Dropped = new LlmOptimizationDroppedCounts(),
                CompactedEvidenceJson = originalJson
            };
        }

        var truncationApplied = false;
        var dropped = new LlmOptimizationDroppedCounts();
        var compacted = CompactByTier(summary, selected, dropped, ref truncationApplied, warnings);
        var compactedJson = JsonSerializer.Serialize(compacted);

        while (EstimatePromptChars(compactedJson.Length) > GetPromptBudget(selected) && selected != LlmEvidenceTier.Small)
        {
            selected = selected == LlmEvidenceTier.Full ? LlmEvidenceTier.Medium : LlmEvidenceTier.Small;
            warnings.Add("Prompt budget exceeded; tier downgraded.");
            compacted = CompactByTier(summary, selected, dropped, ref truncationApplied, warnings);
            compactedJson = JsonSerializer.Serialize(compacted);
        }

        if (EstimatePromptChars(compactedJson.Length) > GetPromptBudget(selected))
        {
            compacted = HardTrimTranscriptToBudget(compacted, selected, warnings, ref truncationApplied, dropped);
            compactedJson = JsonSerializer.Serialize(compacted);
        }

        var promptEstimated = EstimatePromptChars(compactedJson.Length);
        var budget = GetPromptBudget(selected);

        var routedModel = ResolveModelForBand(complexity.Band);

        return new LlmOptimizationPlan
        {
            Enabled = true,
            TierRequested = requested,
            TierUsed = selected,
            ComplexityScore = complexity.Score,
            ComplexityBand = complexity.Band,
            CompactedSummary = compacted,
            OriginalEvidenceChars = originalJson.Length,
            CompactedEvidenceChars = compactedJson.Length,
            PromptEstimatedChars = promptEstimated,
            PromptBudgetChars = budget,
            ModelRoutedFromBand = complexity.Band,
            ModelChosen = routedModel,
            TruncationApplied = truncationApplied,
            Warnings = warnings,
            Dropped = dropped,
            CompactedEvidenceJson = compactedJson
        };
    }

    private static int ComputeComplexityScore(EvidenceSummaryDto summary)
    {
        var durationScore = Math.Min(30, (int)Math.Round(summary.HighLevel.DurationMs / 240000d * 30d));
        var patternScore = Math.Min(20, summary.Patterns.Count * 4);
        var worstScore = Math.Min(20, summary.WorstWindows.Count * 4);
        var transcriptChars = summary.TranscriptSlices.Sum(s => (s.Text ?? string.Empty).Length);
        var transcriptScore = Math.Min(15, transcriptChars / 300);
        var issuesScore = Math.Min(9, summary.HighLevel.TopIssues.Count * 3);

        var hasVision = (summary.Signals.Vision.EyeContactAvg ?? 0) > 0 ||
                        (summary.Signals.Vision.PostureAvg ?? 0) > 0 ||
                        (summary.Signals.Vision.FidgetAvg ?? 0) > 0 ||
                        (summary.Signals.Vision.HeadJitterAvg ?? 0) > 0;

        var hasAudio = summary.Signals.Audio.WpmMedian.HasValue ||
                       summary.Signals.Audio.FillerPerMin.HasValue ||
                       summary.Signals.Audio.PauseMsPerMin.HasValue;

        var hasStructure = summary.Patterns.Any(p => p.Type.Contains("structure", StringComparison.OrdinalIgnoreCase) ||
                                                     p.Type.Contains("content", StringComparison.OrdinalIgnoreCase));

        var diversityScore = (hasVision && hasAudio ? 3 : 0) + (hasStructure ? 3 : 0);

        return Math.Clamp(durationScore + patternScore + worstScore + transcriptScore + issuesScore + diversityScore, 0, 100);
    }

    private LlmEvidenceTier SelectTierByComplexity(LlmEvidenceTier requested, string band)
    {
        if (_options.ForceFullForDebug)
        {
            return LlmEvidenceTier.Full;
        }

        var complexityTier = band switch
        {
            "Low" => LlmEvidenceTier.Small,
            "High" => LlmEvidenceTier.Full,
            _ => LlmEvidenceTier.Medium
        };

        return complexityTier;
    }

    private EvidenceSummaryDto CompactByTier(
        EvidenceSummaryDto summary,
        LlmEvidenceTier tier,
        LlmOptimizationDroppedCounts dropped,
        ref bool truncationApplied,
        List<string> warnings)
    {
        var maxWorst = GetTierCount(_options.MaxWorstWindows, tier, 5);
        var maxPatterns = GetTierCount(_options.MaxPatterns, tier, 6);
        var maxTranscriptChars = GetTierCount(_options.MaxTranscriptSliceChars, tier, 2400);

        var worstWindows = summary.WorstWindows
            .OrderByDescending(w => ComputeWindowPriority(w))
            .ThenBy(w => w.StartMs)
            .Take(maxWorst)
            .ToList();

        dropped.WorstWindowsDropped = Math.Max(0, summary.WorstWindows.Count - worstWindows.Count);

        var patterns = summary.Patterns
            .OrderByDescending(p => p.Severity)
            .ThenByDescending(p => (p.EndMs ?? p.StartMs ?? 0) - (p.StartMs ?? p.EndMs ?? 0))
            .ThenBy(p => p.StartMs ?? long.MaxValue)
            .Take(maxPatterns)
            .ToList();

        dropped.PatternsDropped = Math.Max(0, summary.Patterns.Count - patterns.Count);

        var transcriptSlices = BuildTranscriptSlicesForTier(summary.TranscriptSlices, tier, maxTranscriptChars, ref truncationApplied, warnings, dropped);

        return new EvidenceSummaryDto
        {
            SessionId = summary.SessionId,
            Language = summary.Language,
            HighLevel = summary.HighLevel,
            Signals = summary.Signals,
            WorstWindows = worstWindows,
            Patterns = patterns,
            TranscriptSlices = transcriptSlices
        };
    }

    private List<TranscriptSliceDto> BuildTranscriptSlicesForTier(
        List<TranscriptSliceDto> source,
        LlmEvidenceTier tier,
        int maxChars,
        ref bool truncationApplied,
        List<string> warnings,
        LlmOptimizationDroppedCounts dropped)
    {
        if (tier == LlmEvidenceTier.Small && !_options.IncludeTranscriptSlicesInSmallTier)
        {
            dropped.TranscriptSlicesDropped = source.Count;
            return [];
        }

        var ordered = source.OrderBy(s => s.StartMs).ToList();
        var result = new List<TranscriptSliceDto>();
        var remaining = Math.Max(0, maxChars);

        foreach (var slice in ordered)
        {
            if (remaining <= 0)
            {
                break;
            }

            var text = (slice.Text ?? string.Empty).Trim();
            if (string.IsNullOrEmpty(text))
            {
                continue;
            }

            if (text.Length > remaining)
            {
                text = text[..remaining];
                truncationApplied = true;
                warnings.Add("Transcript slices truncated to fit tier transcript budget.");
            }

            result.Add(new TranscriptSliceDto
            {
                StartMs = slice.StartMs,
                EndMs = slice.EndMs,
                Text = text
            });

            remaining -= text.Length;
        }

        dropped.TranscriptSlicesDropped = Math.Max(0, source.Count - result.Count);
        return result;
    }

    private EvidenceSummaryDto HardTrimTranscriptToBudget(
        EvidenceSummaryDto summary,
        LlmEvidenceTier tier,
        List<string> warnings,
        ref bool truncationApplied,
        LlmOptimizationDroppedCounts dropped)
    {
        var working = new EvidenceSummaryDto
        {
            SessionId = summary.SessionId,
            Language = summary.Language,
            HighLevel = summary.HighLevel,
            Signals = summary.Signals,
            WorstWindows = summary.WorstWindows,
            Patterns = summary.Patterns,
            TranscriptSlices = summary.TranscriptSlices
                .Select(s => new TranscriptSliceDto { StartMs = s.StartMs, EndMs = s.EndMs, Text = s.Text })
                .ToList()
        };

        var budget = GetPromptBudget(tier);
        var json = JsonSerializer.Serialize(working);

        while (EstimatePromptChars(json.Length) > budget && working.TranscriptSlices.Count > 0)
        {
            working.TranscriptSlices.RemoveAt(working.TranscriptSlices.Count - 1);
            dropped.TranscriptSlicesDropped++;
            truncationApplied = true;
            json = JsonSerializer.Serialize(working);
        }

        if (EstimatePromptChars(json.Length) > budget)
        {
            warnings.Add("Prompt still above budget after transcript trimming.");
        }
        else if (truncationApplied)
        {
            warnings.Add("Prompt budget overflow required transcript hard truncation.");
        }

        return working;
    }

    private string ResolveModelForBand(string band)
    {
        if (!_options.ModelRouting.Enabled)
        {
            return _llmOptions.PrimaryModel;
        }

        var routed = band switch
        {
            "Low" => _options.ModelRouting.Routes.Low,
            "High" => _options.ModelRouting.Routes.High,
            _ => _options.ModelRouting.Routes.Medium
        };

        return string.IsNullOrWhiteSpace(routed) ? _llmOptions.PrimaryModel : routed;
    }

    private int EstimatePromptChars(int compactedEvidenceChars)
    {
        return SystemPromptEstimateChars + InstructionEstimateChars + compactedEvidenceChars;
    }

    private int GetPromptBudget(LlmEvidenceTier tier)
    {
        return GetTierCount(_options.MaxPromptChars, tier, 12000);
    }

    private static int GetTierCount(LlmTierIntConfig config, LlmEvidenceTier tier, int fallback)
    {
        var raw = tier switch
        {
            LlmEvidenceTier.Small => config.Small,
            LlmEvidenceTier.Medium => config.Medium,
            _ => config.Full
        };

        return raw > 0 ? raw : fallback;
    }

    private LlmEvidenceTier ParseTier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return LlmEvidenceTier.Medium;
        }

        return value.Trim().ToLowerInvariant() switch
        {
            "small" => LlmEvidenceTier.Small,
            "full" => LlmEvidenceTier.Full,
            _ => LlmEvidenceTier.Medium
        };
    }

    private string ComputeBand(int score)
    {
        var low = Math.Clamp(_options.ModelRouting.ComplexityThresholds.Low, 0, 100);
        var high = Math.Clamp(_options.ModelRouting.ComplexityThresholds.High, 0, 100);

        if (score <= low)
        {
            return "Low";
        }

        if (score >= high)
        {
            return "High";
        }

        return "Medium";
    }

    private double ComputeWindowPriority(WorstWindowDto w)
    {
        var severity = (1 - (w.Metrics.EyeContact ?? 1)) + (1 - (w.Metrics.Posture ?? 1)) +
                       (w.Metrics.Fidget ?? 0) + (w.Metrics.HeadJitter ?? 0);
        var duration = Math.Max(0, w.EndMs - w.StartMs) / 1000d;
        return severity + duration * 0.01;
    }

    private sealed record LlmComplexityResult(int Score, string Band);

    private LlmComplexityResult BuildComplexity(EvidenceSummaryDto summary)
    {
        var score = ComputeComplexityScore(summary);
        var band = ComputeBand(score);
        return new LlmComplexityResult(score, band);
    }
}

public enum LlmEvidenceTier
{
    Small,
    Medium,
    Full
}

public class LlmOptimizationPlan
{
    public bool Enabled { get; set; }
    public LlmEvidenceTier TierRequested { get; set; }
    public LlmEvidenceTier TierUsed { get; set; }
    public int ComplexityScore { get; set; }
    public string ComplexityBand { get; set; } = "Medium";
    public EvidenceSummaryDto CompactedSummary { get; set; } = new();
    public int OriginalEvidenceChars { get; set; }
    public int CompactedEvidenceChars { get; set; }
    public int PromptEstimatedChars { get; set; }
    public int PromptBudgetChars { get; set; }
    public string ModelRoutedFromBand { get; set; } = "Medium";
    public string ModelChosen { get; set; } = string.Empty;
    public bool TruncationApplied { get; set; }
    public List<string> Warnings { get; set; } = [];
    public LlmOptimizationDroppedCounts Dropped { get; set; } = new();
    public string CompactedEvidenceJson { get; set; } = "{}";
}

public class LlmOptimizationDroppedCounts
{
    public int TranscriptSlicesDropped { get; set; }
    public int PatternsDropped { get; set; }
    public int WorstWindowsDropped { get; set; }
}

public class LlmOptimizationMetadata
{
    public bool Enabled { get; set; }
    public int ComplexityScore { get; set; }
    public string ComplexityBand { get; set; } = "Medium";
    public string TierRequested { get; set; } = "medium";
    public string TierUsed { get; set; } = "medium";
    public int OriginalEvidenceChars { get; set; }
    public int CompactedEvidenceChars { get; set; }
    public int PromptEstimatedChars { get; set; }
    public int PromptBudgetChars { get; set; }
    public string ModelRoutedFromBand { get; set; } = "Medium";
    public string ModelChosen { get; set; } = string.Empty;
    public bool TruncationApplied { get; set; }
    public List<string> Warnings { get; set; } = [];
    public LlmOptimizationDroppedCounts Dropped { get; set; } = new();
}

public class LlmOptimizationOptions
{
    public bool Enabled { get; set; } = true;
    public string DefaultTier { get; set; } = "medium";
    public LlmTierIntConfig MaxPromptChars { get; set; } = new() { Small = 6000, Medium = 12000, Full = 22000 };
    public LlmTierIntConfig MaxTranscriptSliceChars { get; set; } = new() { Small = 1200, Medium = 2400, Full = 4000 };
    public LlmTierIntConfig MaxWorstWindows { get; set; } = new() { Small = 3, Medium = 5, Full = 8 };
    public LlmTierIntConfig MaxPatterns { get; set; } = new() { Small = 3, Medium = 6, Full = 10 };
    public LlmOptimizationModelRoutingOptions ModelRouting { get; set; } = new();
    public bool ForceFullForDebug { get; set; }
    public bool IncludeTranscriptSlicesInSmallTier { get; set; } = true;
}

public class LlmTierIntConfig
{
    public int Small { get; set; }
    public int Medium { get; set; }
    public int Full { get; set; }
}

public class LlmOptimizationModelRoutingOptions
{
    public bool Enabled { get; set; } = true;
    public LlmOptimizationComplexityThresholds ComplexityThresholds { get; set; } = new();
    public LlmOptimizationRoutes Routes { get; set; } = new();
}

public class LlmOptimizationComplexityThresholds
{
    public int Low { get; set; } = 30;
    public int High { get; set; } = 70;
}

public class LlmOptimizationRoutes
{
    public string Low { get; set; } = "gpt-5.4-mini";
    public string Medium { get; set; } = "gpt-5.4";
    public string High { get; set; } = "gpt-5.4";
}

public sealed class LlmOptimizationPreviewDto
{
    public Guid SessionId { get; set; }
    public int ComplexityScore { get; set; }
    public string ComplexityBand { get; set; } = "Medium";
    public string TierSelected { get; set; } = "medium";
    public string ModelSelected { get; set; } = string.Empty;
    public LlmOptimizationEstimateDto Estimates { get; set; } = new();
    public LlmOptimizationDroppedCounts Dropped { get; set; } = new();
    public List<string> Warnings { get; set; } = [];
}

public sealed class LlmOptimizationEstimateDto
{
    public int OriginalEvidenceChars { get; set; }
    public int CompactedEvidenceChars { get; set; }
    public int PromptEstimatedChars { get; set; }
    public int BudgetChars { get; set; }
}
