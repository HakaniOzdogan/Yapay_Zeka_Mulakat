namespace InterviewCoach.Domain;

public class ScoreCard
{
    public Guid Id { get; set; } = Guid.NewGuid();
    
    public Guid SessionId { get; set; }
    
    /// <summary>
    /// Eye contact score: 0-100
    /// </summary>
    public int EyeContactScore { get; set; }
    
    /// <summary>
    /// Speaking rate score: 0-100
    /// </summary>
    public int SpeakingRateScore { get; set; }
    
    /// <summary>
    /// Filler words score: 0-100
    /// </summary>
    public int FillerScore { get; set; }
    
    /// <summary>
    /// Posture score: 0-100
    /// </summary>
    public int PostureScore { get; set; }
    
    /// <summary>
    /// Overall score: 0-100 (average or weighted)
    /// </summary>
    public int OverallScore { get; set; }
    
    // Navigation
    public Session? Session { get; set; }
}
