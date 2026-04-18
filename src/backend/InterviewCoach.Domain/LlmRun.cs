namespace InterviewCoach.Domain;

public class LlmRun
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    public string Kind { get; set; } = "coach";

    public string PromptVersion { get; set; } = "coach_v1";

    public string Model { get; set; } = string.Empty;

    public string InputHash { get; set; } = string.Empty;

    public string OutputJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    public Session? Session { get; set; }
}