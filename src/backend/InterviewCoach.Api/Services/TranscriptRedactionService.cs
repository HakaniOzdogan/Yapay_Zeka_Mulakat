using System.Text.RegularExpressions;

namespace InterviewCoach.Api.Services;

public interface ITranscriptRedactionService
{
    string Redact(string text);
    string RedactEmails(string text);
    string RedactPhones(string text);
    string RedactIDs(string text);
    string RedactAddresses(string text);
}

public partial class TranscriptRedactionService : ITranscriptRedactionService
{
    private static readonly Regex EmailRegex = new(
        @"\b[a-zA-Z0-9._%+-]+@[a-zA-Z0-9.-]+\.[a-zA-Z]{2,}\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex PhoneRegex = new(
        @"(?<!\d)(?:\+?90\s*)?(?:0\s*)?(?:5\d{2}|[2-4]\d{2})[\s\-\)]*\d{3}[\s\-]*\d{2}[\s\-]*\d{2}(?!\d)|(?<!\d)\+?\d{1,3}[\s\-]?(?:\(?\d{2,4}\)?[\s\-]?)\d{3,4}[\s\-]?\d{2,4}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex IdRegex = new(
        @"(?<!\d)\d{11}(?!\d)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex AddressRegex = new(
        @"\b[\p{L}0-9'’.-]+(?:\s+[\p{L}0-9'’.-]+){0,8}\s+(?:mahallesi|mahalle|cadde|caddesi|sokak|sokağı|sk\.?|bulvar|bulvarı|apt\.?|apartmanı|apartman|no\s*:?\s*\d+[^\n,.;]*)\b",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public string Redact(string text)
    {
        var value = text ?? string.Empty;
        value = RedactEmails(value);
        value = RedactPhones(value);
        value = RedactIDs(value);
        value = RedactAddresses(value);
        return value;
    }

    public string RedactEmails(string text)
    {
        return EmailRegex.Replace(text ?? string.Empty, "[REDACTED_EMAIL]");
    }

    public string RedactPhones(string text)
    {
        return PhoneRegex.Replace(text ?? string.Empty, "[REDACTED_PHONE]");
    }

    public string RedactIDs(string text)
    {
        return IdRegex.Replace(text ?? string.Empty, "[REDACTED_ID]");
    }

    public string RedactAddresses(string text)
    {
        return AddressRegex.Replace(text ?? string.Empty, "[REDACTED_ADDRESS]");
    }
}
