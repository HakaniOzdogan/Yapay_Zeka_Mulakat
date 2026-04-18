namespace InterviewCoach.Domain;

public class TranscriptSegment
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid SessionId { get; set; }

    public Guid ClientSegmentId { get; set; }
    
    /// <summary>
    /// Start time in milliseconds
    /// </summary>
    public long StartMs { get; set; }
    
    /// <summary>
    /// End time in milliseconds
    /// </summary>
    public long EndMs { get; set; }
    
    public string Text { get; set; } = string.Empty;

    public double? Confidence { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation
    public Session? Session { get; set; }
}
