using InterviewCoach.Api.Services;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Linq;
using Xunit;

namespace InterviewCoach.Api.Tests;

public class LlmCoachingGuardrailsServiceTests
{
    private static LlmCoachingGuardrailsService CreateService(Action<LlmGuardrailsOptions>? configure = null)
    {
        var options = new LlmGuardrailsOptions();
        configure?.Invoke(options);
        return new LlmCoachingGuardrailsService(Options.Create(options));
    }

    [Fact]
    public void Apply_ProfanityContent_SanitizesAndWarns()
    {
        var service = CreateService();
        var input = CreateValidResponse();
        input = input with
        {
            Feedback =
            [
                input.Feedback[0] with { Suggestion = "Do not sound like an idiot in answers." },
                .. input.Feedback.Skip(1)
            ]
        };

        var result = service.Apply(input);

        Assert.True(result.Metadata.Passed);
        Assert.Contains("improvable", result.Response.Feedback[0].Suggestion, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(result.Metadata.SanitizationsApplied, s => s == "profanity_sanitized");
    }

    [Fact]
    public void Apply_PiiInFeedback_RedactsSensitiveTokens()
    {
        var service = CreateService();
        var input = CreateValidResponse();
        input = input with
        {
            Feedback =
            [
                input.Feedback[0] with
                {
                    Evidence = "Candidate said my mail hakan@example.com and phone +905551112233 and id 12345678901",
                    Suggestion = "Do not repeat 12345678901"
                },
                .. input.Feedback.Skip(1)
            ]
        };

        var result = service.Apply(input);

        Assert.True(result.Metadata.Passed);
        Assert.Contains("[REDACTED_EMAIL]", result.Response.Feedback[0].Evidence);
        Assert.Contains("[REDACTED_PHONE]", result.Response.Feedback[0].Evidence);
        Assert.Contains("[REDACTED_ID]", result.Response.Feedback[0].Evidence);
        Assert.Contains(result.Metadata.SanitizationsApplied, s => s == "pii_redacted");
    }

    [Fact]
    public void Apply_DuplicateFeedback_Deduplicates()
    {
        var service = CreateService();
        var baseResponse = CreateValidResponse();

        var duplicate = baseResponse.Feedback[0] with
        {
            TimeRangeMs = [4500, 6500]
        };

        var input = baseResponse with
        {
            Feedback =
            [
                .. baseResponse.Feedback,
                duplicate
            ]
        };

        var result = service.Apply(input);

        Assert.True(result.Metadata.Passed);
        Assert.Equal(5, result.Response.Feedback.Count);
        Assert.Contains(result.Metadata.Warnings, w => w.Contains("Duplicate", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Apply_AllFeedbackUnusable_HardRejects()
    {
        var service = CreateService();

        var unusableFeedback = Enumerable.Range(0, 5)
            .Select(i => new LlmFeedbackItem(
                "vision",
                3,
                "",
                "",
                [0, 0],
                "",
                ""))
            .ToList();

        var input = new LlmCoachingResponse(
            new LlmRubric(3, 3, 3, 3, 3),
            60,
            unusableFeedback,
            [new LlmDrill("Breathing", ["Step"], 5)]);

        var result = service.Apply(input);

        Assert.False(result.Metadata.Passed);
        Assert.NotEmpty(result.Metadata.Violations);
    }

    [Fact]
    public void Apply_MinorWarnings_PassesWithQualityScore()
    {
        var service = CreateService();
        var input = CreateValidResponse();
        input = input with
        {
            Feedback =
            [
                input.Feedback[0] with { Evidence = "I think your pace drifted around 5000 ms" },
                .. input.Feedback.Skip(1)
            ]
        };

        var result = service.Apply(input);

        Assert.True(result.Metadata.Passed);
        Assert.InRange(result.Metadata.QualityScore, 50, 100);
        Assert.NotEmpty(result.Response.Feedback);
    }

    private static LlmCoachingResponse CreateValidResponse()
    {
        var feedback = new List<LlmFeedbackItem>
        {
            new("vision", 3, "Eye contact dips", "Eye contact dropped in window 1000-3000", [1000, 3000], "Keep your gaze near camera for 2-3 sentence chunks.", "I will keep steady eye contact while explaining."),
            new("audio", 2, "Filler burst", "Filler count increased around 4000-5200", [4000, 5200], "Pause silently before complex points.", "Let me pause for a moment and structure this."),
            new("structure", 3, "Answer structure weak", "Response lacked clear opening between 7000-11000", [7000, 11000], "Use Situation-Action-Result ordering.", "Situation was X, action was Y, result was Z."),
            new("content", 2, "Depth can improve", "Technical detail was brief in 12000-15000", [12000, 15000], "Add one concrete metric and tradeoff.", "We cut latency by 30% with this tradeoff."),
            new("audio", 2, "Long pauses", "Pause duration rose in 16000-19000", [16000, 19000], "Keep transition phrases ready to avoid dead air.", "Next, I will cover implementation details.")
        };

        var drills = new List<LlmDrill>
        {
            new("Structured response drill", ["Pick one prompt", "Answer with STAR", "Review timing"], 8)
        };

        return new LlmCoachingResponse(new LlmRubric(3, 3, 3, 3, 3), 68, feedback, drills);
    }
}
