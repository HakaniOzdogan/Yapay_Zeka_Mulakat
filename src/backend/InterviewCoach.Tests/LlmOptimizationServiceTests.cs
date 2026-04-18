using FluentAssertions;
using InterviewCoach.Api.Services;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Tests;

public class LlmOptimizationServiceTests
{
    [Fact]
    public void ComplexityBand_IsDeterministic_ByInputSize()
    {
        var service = CreateService();

        var low = service.BuildPlan(BuildSummary(durationMs: 15_000, patterns: 1, windows: 1, transcriptChars: 200));
        var high = service.BuildPlan(BuildSummary(durationMs: 600_000, patterns: 10, windows: 10, transcriptChars: 8000));

        low.ComplexityScore.Should().BeLessThan(high.ComplexityScore);
        low.ComplexityBand.Should().Be("Low");
        high.ComplexityBand.Should().Be("High");
    }

    [Fact]
    public void Compaction_IsDeterministic_AndRespectsSmallCaps()
    {
        var service = CreateService();
        var summary = BuildSummary(durationMs: 25_000, patterns: 2, windows: 2, transcriptChars: 5000);

        var first = service.BuildPlan(summary, "small");
        var second = service.BuildPlan(summary, "small");

        first.CompactedEvidenceJson.Should().Be(second.CompactedEvidenceJson);
        var maxWorst = first.TierUsed == LlmEvidenceTier.Small ? 3 : first.TierUsed == LlmEvidenceTier.Medium ? 5 : 8;
        var maxPatterns = first.TierUsed == LlmEvidenceTier.Small ? 3 : first.TierUsed == LlmEvidenceTier.Medium ? 6 : 10;
        var maxTranscriptChars = first.TierUsed == LlmEvidenceTier.Small ? 1200 : first.TierUsed == LlmEvidenceTier.Medium ? 2400 : 4000;

        first.CompactedSummary.WorstWindows.Count.Should().BeLessOrEqualTo(maxWorst);
        first.CompactedSummary.Patterns.Count.Should().BeLessOrEqualTo(maxPatterns);
        first.CompactedSummary.TranscriptSlices.Sum(s => (s.Text ?? string.Empty).Length).Should().BeLessOrEqualTo(maxTranscriptChars);
    }

    [Fact]
    public void Tier_Downgrades_WhenBudgetExceeded()
    {
        var options = new LlmOptimizationOptions
        {
            Enabled = true,
            DefaultTier = "full",
            MaxPromptChars = new LlmTierIntConfig { Small = 2000, Medium = 3000, Full = 4000 },
            MaxTranscriptSliceChars = new LlmTierIntConfig { Small = 1000, Medium = 2000, Full = 3000 },
            MaxWorstWindows = new LlmTierIntConfig { Small = 3, Medium = 5, Full = 8 },
            MaxPatterns = new LlmTierIntConfig { Small = 3, Medium = 6, Full = 10 }
        };

        var service = new LlmOptimizationService(Options.Create(options), Options.Create(new LlmOptions()));
        var summary = BuildSummary(durationMs: 600_000, patterns: 20, windows: 20, transcriptChars: 12_000);

        var plan = service.BuildPlan(summary, "full");

        plan.TierUsed.Should().Be(LlmEvidenceTier.Small);
        plan.PromptEstimatedChars.Should().BeGreaterThan(0);
        plan.Warnings.Should().NotBeEmpty();
    }

    [Fact]
    public void ModelRouting_SelectsByComplexityBand()
    {
        var service = CreateService();

        var low = service.BuildPlan(BuildSummary(durationMs: 20_000, patterns: 1, windows: 1, transcriptChars: 100));
        var medium = service.BuildPlan(BuildSummary(durationMs: 120_000, patterns: 4, windows: 4, transcriptChars: 1000));
        var high = service.BuildPlan(BuildSummary(durationMs: 600_000, patterns: 10, windows: 10, transcriptChars: 9000));

        low.ModelChosen.Should().Be("qwen2.5:3b-instruct");
        medium.ModelChosen.Should().Be("qwen2.5:7b-instruct");
        high.ModelChosen.Should().Be("qwen2.5:7b-instruct");
    }

    private static LlmOptimizationService CreateService()
    {
        return new LlmOptimizationService(
            Options.Create(new LlmOptimizationOptions()),
            Options.Create(new LlmOptions()));
    }

    private static EvidenceSummaryDto BuildSummary(int durationMs, int patterns, int windows, int transcriptChars)
    {
        var patternList = Enumerable.Range(0, patterns)
            .Select(i => new PatternSummaryDto
            {
                Type = i % 2 == 0 ? "audio" : "structure",
                StartMs = i * 1000,
                EndMs = i * 1000 + 800,
                Severity = (i % 5) + 1,
                Evidence = $"Pattern evidence {i}"
            })
            .ToList();

        var windowList = Enumerable.Range(0, windows)
            .Select(i => new WorstWindowDto
            {
                StartMs = i * 2000,
                EndMs = i * 2000 + 1000,
                Reason = $"Window {i}",
                Metrics = new WindowMetricsDto
                {
                    EyeContact = 0.4,
                    Posture = 0.6,
                    Fidget = 0.4,
                    HeadJitter = 0.3,
                    Wpm = 130,
                    Filler = 2,
                    PauseMs = 800
                }
            })
            .ToList();

        var text = new string('a', Math.Max(1, transcriptChars));
        var slices = new List<TranscriptSliceDto>
        {
            new() { StartMs = 0, EndMs = 5_000, Text = text }
        };

        return new EvidenceSummaryDto
        {
            SessionId = Guid.NewGuid(),
            Language = "en",
            HighLevel = new HighLevelDto
            {
                DurationMs = durationMs,
                TopIssues =
                [
                    new TopIssueDto { Issue = "eye_contact", Evidence = "low", TimeRangeMs = [1000, 2000] },
                    new TopIssueDto { Issue = "filler", Evidence = "high", TimeRangeMs = [3000, 4000] }
                ]
            },
            Signals = new SignalsDto
            {
                Vision = new VisionSignalsDto { EyeContactAvg = 0.5, PostureAvg = 0.6, FidgetAvg = 0.4, HeadJitterAvg = 0.3 },
                Audio = new AudioSignalsDto { WpmMedian = 140, FillerPerMin = 3, PauseMsPerMin = 800 }
            },
            Patterns = patternList,
            WorstWindows = windowList,
            TranscriptSlices = slices
        };
    }
}
