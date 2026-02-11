namespace InterviewCoach.Domain;

public class Session
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    /// <summary>
    /// Status: Created, InProgress, Completed
    /// </summary>
    public string Status { get; set; } = "Created";
    
    /// <summary>
    /// Role: e.g., SoftwareEngineer, ProductManager, etc.
    /// </summary>
    public string SelectedRole { get; set; } = string.Empty;
    
    /// <summary>
    /// Language code: e.g., tr, en
    /// </summary>
    public string Language { get; set; } = "tr";
    
    /// <summary>
    /// JSON settings: store with raw media storage preference, etc.
    /// </summary>
    public string SettingsJson { get; set; } = "{}";
    
    /// <summary>
    /// JSON stats: store speech/metrics statistics (WPM, filler count, etc.)
    /// </summary>
    public string StatsJson { get; set; } = "{}";
    
    // Navigation properties
    public ICollection<Question> Questions { get; set; } = [];
    public ICollection<TranscriptSegment> TranscriptSegments { get; set; } = [];
    public ICollection<MetricEvent> MetricEvents { get; set; } = [];
    public ICollection<FeedbackItem> FeedbackItems { get; set; } = [];
    public ScoreCard? ScoreCard { get; set; }
}
