// Placeholder for application services
namespace InterviewCoach.Application;

public class ApplicationOptions
{
    /// <summary>
    /// Scoring thresholds and configuration
    /// </summary>
    public ScoringConfig ScoringConfig { get; set; } = new();
}

public class ScoringConfig
{
    public double EyeContactGoodRatio { get; set; } = 0.7; // 70% of time looking forward
    public double HeadStabilityMaxStd { get; set; } = 15.0; // degrees
    public int WpmIdealMin { get; set; } = 120;
    public int WpmIdealMax { get; set; } = 160;
    public int FillerPerMinMax { get; set; } = 3; // max filler words per minute
    public double PostureLeanMax { get; set; } = 10.0; // degrees forward lean
}
