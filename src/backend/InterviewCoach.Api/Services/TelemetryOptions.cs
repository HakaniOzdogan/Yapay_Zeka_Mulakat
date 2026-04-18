namespace InterviewCoach.Api.Services;

public class TelemetryOptions
{
    public bool Enabled { get; set; } = true;
    public string? OtlpEndpoint { get; set; }
    public string ServiceName { get; set; } = "InterviewCoach";
    public string? ServiceVersion { get; set; }
}
