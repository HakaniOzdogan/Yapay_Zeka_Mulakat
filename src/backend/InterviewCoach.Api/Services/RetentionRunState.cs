namespace InterviewCoach.Api.Services;

public class RetentionRunSummary
{
    public DateTime RanAtUtc { get; set; } = DateTime.UtcNow;
    public int SessionsDeleted { get; set; }
    public int SessionsPruned { get; set; }
    public Dictionary<string, int> RowsDeleted { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public void AddRows(string table, int count)
    {
        if (count <= 0)
            return;

        RowsDeleted.TryGetValue(table, out var existing);
        RowsDeleted[table] = existing + count;
    }
}

public interface IRetentionRunState
{
    RetentionRunSummary? LastRun { get; }
    void SetLastRun(RetentionRunSummary summary);
}

public class RetentionRunState : IRetentionRunState
{
    private readonly object _lock = new();
    private RetentionRunSummary? _lastRun;

    public RetentionRunSummary? LastRun
    {
        get
        {
            lock (_lock)
            {
                return _lastRun;
            }
        }
    }

    public void SetLastRun(RetentionRunSummary summary)
    {
        lock (_lock)
        {
            _lastRun = summary;
        }
    }
}