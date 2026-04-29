namespace InterviewCoach.Domain;

public class Question
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid SessionId { get; set; }
    
    /// <summary>
    /// Ordinal position (1-indexed) in the session
    /// </summary>
    public int Order { get; set; }
    
    public string Prompt { get; set; } = string.Empty;
    
    public string? AudioUrl { get; set; }
    
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    
    // Navigation
    public Session? Session { get; set; }
}
