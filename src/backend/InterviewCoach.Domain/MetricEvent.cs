using System.ComponentModel.DataAnnotations.Schema;

namespace InterviewCoach.Domain;

public class MetricEvent
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid SessionId { get; set; }

    public Guid ClientEventId { get; set; }
    
    /// <summary>
    /// Milliseconds since session start
    /// </summary>
    public long TsMs { get; set; }

    public string Source { get; set; } = "Vision";
    
    /// <summary>
    /// Type: EyeContact, HeadStability, Posture, Fidget, VolumeStability, SpeechRate, Pause, etc.
    /// </summary>
    public string Type { get; set; } = string.Empty;
    
    /// <summary>
    /// JSON serialized metric value(s)
    /// </summary>
    public string PayloadJson { get; set; } = "{}";

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;

    // Compatibility aliases for existing code paths.
    [NotMapped]
    public long TimestampMs
    {
        get => TsMs;
        set => TsMs = value;
    }

    [NotMapped]
    public string ValueJson
    {
        get => PayloadJson;
        set => PayloadJson = value;
    }
    
    // Navigation
    public Session? Session { get; set; }
}
