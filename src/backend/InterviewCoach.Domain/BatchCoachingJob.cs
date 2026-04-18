namespace InterviewCoach.Domain;

public class BatchCoachingJob
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public DateTime CreatedAtUtc { get; set; } = DateTime.UtcNow;
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Status { get; set; } = BatchCoachingJobStatus.Queued;
    public Guid? CreatedByUserId { get; set; }
    public string FiltersJson { get; set; } = "{}";
    public string OptionsJson { get; set; } = "{}";
    public int TotalSessions { get; set; }
    public int ProcessedSessions { get; set; }
    public int SucceededSessions { get; set; }
    public int FailedSessions { get; set; }
    public int SkippedSessions { get; set; }
    public string? LastError { get; set; }
    public double? ProgressPercent { get; set; }

    public ICollection<BatchCoachingJobItem> Items { get; set; } = [];
}

public static class BatchCoachingJobStatus
{
    public const string Queued = "Queued";
    public const string Running = "Running";
    public const string Completed = "Completed";
    public const string Failed = "Failed";
    public const string Canceled = "Canceled";
}