namespace InterviewCoach.Domain;

public class FeedbackItem
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid SessionId { get; set; }
    
    /// <summary>
    /// Category: EyeContact, Pace, Filler, Posture, Overall, etc.
    /// </summary>
    public string Category { get; set; } = string.Empty;
    
    /// <summary>
    /// Severity: 1-5 (1=info, 5=critical)
    /// </summary>
    public int Severity { get; set; }
    
    public string Title { get; set; } = string.Empty;
    
    public string Details { get; set; } = string.Empty;
    
    /// <summary>
    /// Actionable suggestion
    /// </summary>
    public string Suggestion { get; set; } = string.Empty;
    
    /// <summary>
    /// Example text or context
    /// </summary>
    public string ExampleText { get; set; } = string.Empty;
    
    /// <summary>
    /// Start millisecond (optional)
    /// </summary>
    public long? StartMs { get; set; }
    
    /// <summary>
    /// End millisecond (optional)
    /// </summary>
    public long? EndMs { get; set; }
    
    // Navigation
    public Session? Session { get; set; }
}
