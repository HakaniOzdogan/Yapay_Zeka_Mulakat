using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public interface ILlmCoachingGuardrailsService
{
    LlmCoachingGuardrailsOutcome Apply(LlmCoachingResponse response);
}

public class LlmCoachingGuardrailsService : ILlmCoachingGuardrailsService
{
    private static readonly string[] ProfanityTerms =
    [
        "stupid", "idiot", "moron", "dumb", "aptal", "salak", "gerizekali", "beceriksiz"
    ];

    private static readonly string[] DiscriminatoryTerms =
    [
        "racist", "sexist", "homophobic", "ayrimci", "irkci", "cinsiyetci"
    ];

    private static readonly string[] MedicalOverreachTerms =
    [
        "adhd", "depression", "anxiety disorder", "panic disorder", "bipolar",
        "depresyon", "anksiyete bozuklugu", "panik bozukluk", "tanin var", "tanin olabilir"
    ];

    private static readonly string[] HarmfulAbsoluteTerms =
    [
        "you are terrible", "you will fail", "hopeless", "you are bad",
        "berbatsin", "basarisiz olacaksin", "asla basaramazsin"
    ];

    private static readonly string[] UncertaintyTerms =
    [
        "i think", "maybe", "probably", "sanirim", "belki", "muhtemelen"
    ];

    private static readonly Regex EmailRegex = new(
        @"\b[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    private static readonly Regex PhoneRegex = new(
        @"(?<!\d)(?:\+?90\s*)?(?:0\s*)?(?:5\d{2}|[2-4]\d{2})[\s\-\)]*\d{3}[\s\-]*\d{2}[\s\-]*\d{2}(?!\d)|(?<!\d)\+?\d{1,3}[\s\-]?(?:\(?\d{2,4}\)?[\s\-]?)\d{3,4}[\s\-]?\d{2,4}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IdRegex = new(
        @"(?<!\d)\d{11}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly LlmGuardrailsOptions _options;

    public LlmCoachingGuardrailsService(IOptions<LlmGuardrailsOptions> options)
    {
        _options = options.Value ?? new LlmGuardrailsOptions();
    }

    public LlmCoachingGuardrailsOutcome Apply(LlmCoachingResponse response)
    {
        if (!_options.Enabled)
        {
            return new LlmCoachingGuardrailsOutcome(response, new LlmGuardrailsMetadata
            {
                Passed = true,
                QualityScore = 100
            });
        }

        var warnings = new List<string>();
        var violations = new List<string>();
        var sanitizations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var cleanedFeedback = new List<LlmFeedbackItem>();
        var titleSuggestionKeys = new HashSet<string>(StringComparer.Ordinal);
        var duplicateCount = 0;
        var actionableCount = 0;
        var groundedCount = 0;
        var usableCount = 0;

        foreach (var item in response.Feedback)
        {
            var sanitized = SanitizeFeedback(item, warnings, violations, sanitizations);

            var titleNorm = NormalizeForDuplicate(sanitized.Title);
            var suggestionNorm = NormalizeForDuplicate(sanitized.Suggestion);
            var dupKey = $"{titleNorm}|{suggestionNorm}";
            if (!string.IsNullOrWhiteSpace(titleNorm) && titleSuggestionKeys.Contains(dupKey))
            {
                duplicateCount++;
                warnings.Add("Duplicate feedback item removed.");
                continue;
            }

            titleSuggestionKeys.Add(dupKey);
            cleanedFeedback.Add(sanitized);

            if (HasActionableSuggestion(sanitized.Suggestion))
            {
                actionableCount++;
            }

            if (IsGrounded(sanitized))
            {
                groundedCount++;
            }

            if (IsUsable(sanitized))
            {
                usableCount++;
            }
        }

        var cleanedDrills = new List<LlmDrill>();
        foreach (var drill in response.Drills)
        {
            cleanedDrills.Add(SanitizeDrill(drill, warnings, violations, sanitizations));
        }

        if (cleanedFeedback.Count < 5 || cleanedFeedback.Count > 10)
        {
            violations.Add("feedback count must be between 5 and 10 after guardrails.");
        }

        if (actionableCount == 0)
        {
            violations.Add("No actionable suggestions found.");
        }

        if (usableCount == 0)
        {
            violations.Add("All feedback items are empty or unusable.");
        }

        if (_options.StrictGrounding)
        {
            if (groundedCount == 0)
            {
                violations.Add("No grounded feedback items found.");
            }
            else if (groundedCount * 2 < cleanedFeedback.Count)
            {
                violations.Add("Most feedback items are not grounded in evidence/time range.");
            }
        }

        if (cleanedFeedback.Count > 0)
        {
            var duplicateRatio = duplicateCount / (double)(duplicateCount + cleanedFeedback.Count);
            if (duplicateRatio > _options.MaxDuplicateFeedbackRatio)
            {
                warnings.Add("High duplicate feedback ratio detected.");
            }
        }

        var qualityScore = ComputeQualityScore(cleanedFeedback, cleanedDrills, warnings, violations);
        if (qualityScore < _options.MinQualityScore)
        {
            violations.Add($"Quality score {qualityScore} is below minimum {_options.MinQualityScore}.");
        }

        var hasSevereUnsafe = violations.Any(v => v.Contains("unsafe", StringComparison.OrdinalIgnoreCase));
        var shouldReject =
            cleanedFeedback.Count == 0 ||
            actionableCount == 0 ||
            usableCount == 0 ||
            (_options.StrictGrounding && groundedCount * 2 < Math.Max(cleanedFeedback.Count, 1)) ||
            hasSevereUnsafe ||
            qualityScore < _options.MinQualityScore;

        var sanitizedResponse = response with
        {
            Feedback = cleanedFeedback,
            Drills = cleanedDrills
        };

        var metadata = new LlmGuardrailsMetadata
        {
            Passed = !shouldReject,
            QualityScore = qualityScore,
            Warnings = warnings.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            Violations = violations.Distinct(StringComparer.OrdinalIgnoreCase).ToList(),
            SanitizationsApplied = sanitizations.OrderBy(x => x, StringComparer.Ordinal).ToList()
        };

        return new LlmCoachingGuardrailsOutcome(sanitizedResponse, metadata);
    }

    private LlmFeedbackItem SanitizeFeedback(
        LlmFeedbackItem item,
        List<string> warnings,
        List<string> violations,
        HashSet<string> sanitizations)
    {
        var category = (item.Category ?? string.Empty).Trim().ToLowerInvariant();
        var title = CleanText(item.Title);
        var evidence = CleanText(item.Evidence);
        var suggestion = CleanText(item.Suggestion);
        var examplePhrase = CleanText(item.ExamplePhrase);

        if (category is not ("vision" or "audio" or "content" or "structure"))
        {
            warnings.Add("Invalid category normalized to 'content'.");
            category = "content";
            sanitizations.Add("category_normalized");
        }

        var timeRange = item.TimeRangeMs?.Length == 2 ? item.TimeRangeMs : new long[] { 0, 0 };
        if (timeRange[0] < 0 || timeRange[1] < 0 || timeRange[0] > timeRange[1])
        {
            warnings.Add("Invalid time range normalized to [0,0].");
            timeRange = [0, 0];
            sanitizations.Add("time_range_normalized");
        }

        var joined = string.Join(" ", title, evidence, suggestion, examplePhrase).ToLowerInvariant();

        if (ContainsAny(joined, DiscriminatoryTerms))
        {
            violations.Add("Detected unsafe discriminatory language.");
        }

        if (_options.EnableProfanityFilter && ContainsAny(joined, ProfanityTerms))
        {
            title = ReplaceUnsafePhrases(title);
            evidence = ReplaceUnsafePhrases(evidence);
            suggestion = ReplaceUnsafePhrases(suggestion);
            examplePhrase = ReplaceUnsafePhrases(examplePhrase);
            warnings.Add("Profanity/insult wording sanitized.");
            sanitizations.Add("profanity_sanitized");
        }

        if (_options.EnableMedicalOverreachFilter && ContainsAny(joined, MedicalOverreachTerms))
        {
            evidence = ReplaceMedicalOverreach(evidence);
            suggestion = ReplaceMedicalOverreach(suggestion);
            warnings.Add("Medical/legal diagnostic overreach sanitized.");
            sanitizations.Add("medical_overreach_sanitized");
        }

        if (ContainsAny(joined, HarmfulAbsoluteTerms))
        {
            title = ReplaceAbsoluteHarmfulPhrases(title);
            evidence = ReplaceAbsoluteHarmfulPhrases(evidence);
            suggestion = ReplaceAbsoluteHarmfulPhrases(suggestion);
            examplePhrase = ReplaceAbsoluteHarmfulPhrases(examplePhrase);
            warnings.Add("Absolute/harmful phrasing softened.");
            sanitizations.Add("harmful_tone_softened");
        }

        if (_options.EnablePiiRedaction)
        {
            var before = string.Join("|", evidence, suggestion, examplePhrase);
            evidence = RedactPii(evidence);
            suggestion = RedactPii(suggestion);
            examplePhrase = RedactPii(examplePhrase);
            var after = string.Join("|", evidence, suggestion, examplePhrase);
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                warnings.Add("PII redacted in coaching content.");
                sanitizations.Add("pii_redacted");
            }
        }

        if (string.IsNullOrWhiteSpace(evidence))
        {
            warnings.Add("Feedback item had empty evidence.");
        }
        else if (ContainsAny(evidence.ToLowerInvariant(), UncertaintyTerms) && !ContainsConcreteEvidenceSignal(evidence))
        {
            warnings.Add("Uncertain evidence wording detected without grounding.");
        }

        return item with
        {
            Category = category,
            Title = title,
            Evidence = evidence,
            Suggestion = suggestion,
            ExamplePhrase = examplePhrase,
            TimeRangeMs = timeRange
        };
    }

    private LlmDrill SanitizeDrill(
        LlmDrill drill,
        List<string> warnings,
        List<string> violations,
        HashSet<string> sanitizations)
    {
        var title = CleanText(drill.Title);
        var steps = (drill.Steps ?? [])
            .Select(CleanText)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var duration = drill.DurationMin;
        if (duration <= 0)
        {
            duration = 5;
            warnings.Add("Drill duration was non-positive and normalized to 5.");
            sanitizations.Add("drill_duration_normalized");
        }

        var joined = (title + " " + string.Join(" ", steps)).ToLowerInvariant();
        if (ContainsAny(joined, DiscriminatoryTerms))
        {
            violations.Add("Detected unsafe discriminatory language in drill content.");
        }

        if (_options.EnableProfanityFilter && ContainsAny(joined, ProfanityTerms))
        {
            title = ReplaceUnsafePhrases(title);
            steps = steps.Select(ReplaceUnsafePhrases).ToList();
            warnings.Add("Profanity/insult wording sanitized in drills.");
            sanitizations.Add("drill_profanity_sanitized");
        }

        if (_options.EnablePiiRedaction)
        {
            var before = title + "|" + string.Join("|", steps);
            title = RedactPii(title);
            steps = steps.Select(RedactPii).ToList();
            var after = title + "|" + string.Join("|", steps);
            if (!string.Equals(before, after, StringComparison.Ordinal))
            {
                warnings.Add("PII redacted in drill content.");
                sanitizations.Add("drill_pii_redacted");
            }
        }

        return drill with
        {
            Title = title,
            Steps = steps,
            DurationMin = duration
        };
    }

    private static int ComputeQualityScore(
        IReadOnlyCollection<LlmFeedbackItem> feedback,
        IReadOnlyCollection<LlmDrill> drills,
        IReadOnlyCollection<string> warnings,
        IReadOnlyCollection<string> violations)
    {
        var score = 100;

        if (feedback.Count < 5 || feedback.Count > 10)
        {
            score -= 20;
        }

        var grounded = feedback.Count(IsGrounded);
        var actionable = feedback.Count(f => HasActionableSuggestion(f.Suggestion));

        if (feedback.Count > 0)
        {
            var groundedRatioPenalty = (int)Math.Round((1 - (grounded / (double)feedback.Count)) * 25);
            var actionableRatioPenalty = (int)Math.Round((1 - (actionable / (double)feedback.Count)) * 20);
            score -= groundedRatioPenalty + actionableRatioPenalty;
        }

        if (drills.Count == 0)
        {
            score -= 10;
        }

        score -= warnings.Count;
        score -= violations.Count * 8;

        return Math.Clamp(score, 0, 100);
    }

    private static bool IsGrounded(LlmFeedbackItem item)
    {
        if (string.IsNullOrWhiteSpace(item.Evidence))
        {
            return false;
        }

        if (item.TimeRangeMs == null || item.TimeRangeMs.Length != 2)
        {
            return false;
        }

        return item.TimeRangeMs[0] >= 0 && item.TimeRangeMs[1] >= item.TimeRangeMs[0];
    }

    private static bool IsUsable(LlmFeedbackItem item)
    {
        return !string.IsNullOrWhiteSpace(item.Title)
               && !string.IsNullOrWhiteSpace(item.Evidence)
               && !string.IsNullOrWhiteSpace(item.Suggestion)
               && (item.Title.Length + item.Evidence.Length + item.Suggestion.Length) >= 30;
    }

    private static bool HasActionableSuggestion(string suggestion)
    {
        return !string.IsNullOrWhiteSpace(suggestion) && suggestion.Trim().Length >= 12;
    }

    private static bool ContainsAny(string value, IReadOnlyCollection<string> terms)
    {
        foreach (var term in terms)
        {
            if (value.Contains(term, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static string NormalizeForDuplicate(string value)
    {
        var cleaned = Regex.Replace((value ?? string.Empty).ToLowerInvariant(), @"\s+", " ").Trim();
        return Regex.Replace(cleaned, @"[^a-z0-9\p{L}\s]", string.Empty);
    }

    private static string CleanText(string value)
    {
        return Regex.Replace((value ?? string.Empty).Trim(), @"\s+", " ");
    }

    private static bool ContainsConcreteEvidenceSignal(string evidence)
    {
        return Regex.IsMatch(evidence, @"\d") ||
               evidence.Contains("ms", StringComparison.OrdinalIgnoreCase) ||
               evidence.Contains("window", StringComparison.OrdinalIgnoreCase) ||
               evidence.Contains("range", StringComparison.OrdinalIgnoreCase);
    }

    private static string ReplaceUnsafePhrases(string text)
    {
        var result = text;
        foreach (var term in ProfanityTerms)
        {
            result = Regex.Replace(result, Regex.Escape(term), "improvable", RegexOptions.IgnoreCase);
        }

        return result;
    }

    private static string ReplaceMedicalOverreach(string text)
    {
        var result = text;
        foreach (var term in MedicalOverreachTerms)
        {
            result = Regex.Replace(result, Regex.Escape(term), "stress-related signal", RegexOptions.IgnoreCase);
        }

        return result;
    }

    private static string ReplaceAbsoluteHarmfulPhrases(string text)
    {
        var result = text;
        foreach (var term in HarmfulAbsoluteTerms)
        {
            result = Regex.Replace(result, Regex.Escape(term), "this area needs targeted improvement", RegexOptions.IgnoreCase);
        }

        return result;
    }

    private static string RedactPii(string text)
    {
        var value = EmailRegex.Replace(text ?? string.Empty, "[REDACTED_EMAIL]");
        value = PhoneRegex.Replace(value, "[REDACTED_PHONE]");
        value = IdRegex.Replace(value, "[REDACTED_ID]");
        return value;
    }
}

public class LlmGuardrailsOptions
{
    public bool Enabled { get; set; } = true;
    public int MinQualityScore { get; set; } = 50;
    public double MaxDuplicateFeedbackRatio { get; set; } = 0.4;
    public bool EnableProfanityFilter { get; set; } = true;
    public bool EnableMedicalOverreachFilter { get; set; } = true;
    public bool EnablePiiRedaction { get; set; } = true;
    public bool StrictGrounding { get; set; } = true;
}

public sealed class LlmCoachingGuardrailsOutcome
{
    public LlmCoachingGuardrailsOutcome(LlmCoachingResponse response, LlmGuardrailsMetadata metadata)
    {
        Response = response;
        Metadata = metadata;
    }

    public LlmCoachingResponse Response { get; }
    public LlmGuardrailsMetadata Metadata { get; }
}

public sealed class LlmGuardrailsMetadata
{
    public bool Passed { get; set; }
    public List<string> Warnings { get; set; } = [];
    public List<string> Violations { get; set; } = [];
    public List<string> SanitizationsApplied { get; set; } = [];
    public int QualityScore { get; set; }
}
