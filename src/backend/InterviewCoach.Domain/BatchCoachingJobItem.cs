namespace InterviewCoach.Domain;

public class BatchCoachingJobItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public Guid JobId { get; set; }
    public Guid SessionId { get; set; }
    public string Status { get; set; } = BatchCoachingJobItemStatus.Pending;
    public int Attempts { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ResultSource { get; set; }
    public Guid? LlmRunId { get; set; }
    public string? Error { get; set; }

    public BatchCoachingJob? Job { get; set; }
}

public static class BatchCoachingJobItemStatus
{
    public const string Pending = "Pending";
    public const string Running = "Running";
    public const string Succeeded = "Succeeded";
    public const string Failed = "Failed";
    public const string Skipped = "Skipped";
}