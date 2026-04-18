namespace InterviewCoach.Api.Services;

public class RetentionOptions
{
    public bool Enabled { get; set; } = true;
    public int DeleteAfterDays { get; set; } = 30;
    public int? KeepSummariesOnlyAfterDays { get; set; } = 7;
    public int RunHourUtc { get; set; } = 3;
}