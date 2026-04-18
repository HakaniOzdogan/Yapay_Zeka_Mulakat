namespace InterviewCoach.Api.Services;

public class PrivacyOptions
{
    public bool RedactTranscripts { get; set; } = true;
    public bool RedactOnIngest { get; set; } = true;
    public bool StoreOriginalTranscripts { get; set; } = false;
}