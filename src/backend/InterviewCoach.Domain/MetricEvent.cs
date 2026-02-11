namespace InterviewCoach.Domain;

public class MetricEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid SessionId { get; set; }
    
    /// <summary>
    /// Milliseconds since session start
    /// </summary>
    public long TimestampMs { get; set; }
    
    /// <summary>
    /// Type: EyeContact, HeadStability, Posture, Fidget, VolumeStability, SpeechRate, Pause, etc.
    /// </summary>
    public string Type { get; set; } = string.Empty;
    
    /// <summary>
    /// JSON serialized metric value(s)
    /// </summary>
    public string ValueJson { get; set; } = "{}";
    
    // Navigation
    public Session? Session { get; set; }
}
