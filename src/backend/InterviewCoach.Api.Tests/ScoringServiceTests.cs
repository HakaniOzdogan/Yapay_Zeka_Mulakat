using Xunit;
using InterviewCoach.Api.Services;
using InterviewCoach.Domain;

namespace InterviewCoach.Api.Tests;

public class ScoringServiceTests
{
    private readonly ScoringService _scoringService;

    public ScoringServiceTests()
    {
        _scoringService = new ScoringService();
    }

    #region Eye Contact Score Tests

    [Fact]
    public void ComputeEyeContactScore_WithNoMetrics_ReturnsDefault()
    {
        // Arrange
        var metrics = new List<MetricEvent>();

        // Act
        var result = _scoringService.ComputeScoreCard(
            new Session { Id = Guid.NewGuid() },
            metrics,
            null
        );

        // Assert
        Assert.Equal(50, result.EyeContactScore);
    }

    [Fact]
    public void ComputeEyeContactScore_WithGoodEyeContact_ReturnsHighScore()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var metrics = new List<MetricEvent>
        {
            new()
            {
                SessionId = session.Id,
                Type = "combined",
                ValueJson = "{\"eyeContact\": 85, \"headStability\": 50, \"posture\": 50, \"fidget\": 50}"
            }
        };

        // Act
        var result = _scoringService.ComputeScoreCard(session, metrics, null);

        // Assert
        Assert.True(result.EyeContactScore >= 80);
    }

    #endregion

    #region Speaking Rate Score Tests

    [Fact]
    public void ComputeSpeakingRateScore_WithIdealWPM_ReturnsMaxScore()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var stats = new Dictionary<string, object> { { "wpm", 140 } };

        // Act
        var result = _scoringService.ComputeScoreCard(session, new List<MetricEvent>(), stats);

        // Assert
        Assert.Equal(100, result.SpeakingRateScore);
    }

    [Fact]
    public void ComputeSpeakingRateScore_WithTooSlowWPM_DeductesPoints()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var stats = new Dictionary<string, object> { { "wpm", 80 } };

        // Act
        var result = _scoringService.ComputeScoreCard(session, new List<MetricEvent>(), stats);

        // Assert
        Assert.True(result.SpeakingRateScore < 100);
        Assert.True(result.SpeakingRateScore >= 20);
    }

    [Fact]
    public void ComputeSpeakingRateScore_WithTooFastWPM_DeductesPoints()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var stats = new Dictionary<string, object> { { "wpm", 200 } };

        // Act
        var result = _scoringService.ComputeScoreCard(session, new List<MetricEvent>(), stats);

        // Assert
        Assert.True(result.SpeakingRateScore < 100);
        Assert.True(result.SpeakingRateScore >= 20);
    }

    #endregion

    #region Filler Words Score Tests

    [Fact]
    public void ComputeFillerScore_WithNoFillers_ReturnsMaxScore()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var stats = new Dictionary<string, object>
        {
            { "filler_count", 0 },
            { "duration_ms", 60000 }
        };

        // Act
        var result = _scoringService.ComputeScoreCard(session, new List<MetricEvent>(), stats);

        // Assert
        Assert.Equal(100, result.FillerScore);
    }

    [Fact]
    public void ComputeFillerScore_WithFewFillers_ReturnsHighScore()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var stats = new Dictionary<string, object>
        {
            { "filler_count", 2 },
            { "duration_ms", 60000 }
        };

        // Act
        var result = _scoringService.ComputeScoreCard(session, new List<MetricEvent>(), stats);

        // Assert
        Assert.Equal(100, result.FillerScore);
    }

    [Fact]
    public void ComputeFillerScore_WithManyFillers_ReturnsLowScore()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var stats = new Dictionary<string, object>
        {
            { "filler_count", 12 },
            { "duration_ms", 60000 }
        };

        // Act
        var result = _scoringService.ComputeScoreCard(session, new List<MetricEvent>(), stats);

        // Assert
        Assert.True(result.FillerScore < 50);
    }

    #endregion

    #region Overall Score Tests

    [Fact]
    public void ComputeScoreCard_WithExcellentPerformance_ReturnsHighOverallScore()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var metrics = new List<MetricEvent>
        {
            new()
            {
                SessionId = session.Id,
                Type = "combined",
                ValueJson = "{\"eyeContact\": 90, \"headStability\": 90, \"posture\": 90, \"fidget\": 90}"
            }
        };
        var stats = new Dictionary<string, object>
        {
            { "wpm", 140 },
            { "filler_count", 1 },
            { "duration_ms", 60000 }
        };

        // Act
        var result = _scoringService.ComputeScoreCard(session, metrics, stats);

        // Assert
        Assert.True(result.OverallScore >= 80);
    }

    [Fact]
    public void ComputeScoreCard_WithPoorPerformance_ReturnsLowOverallScore()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var metrics = new List<MetricEvent>
        {
            new()
            {
                SessionId = session.Id,
                Type = "combined",
                ValueJson = "{\"eyeContact\": 20, \"headStability\": 20, \"posture\": 20, \"fidget\": 20}"
            }
        };
        var stats = new Dictionary<string, object>
        {
            { "wpm", 60 },
            { "filler_count", 15 },
            { "duration_ms", 60000 }
        };

        // Act
        var result = _scoringService.ComputeScoreCard(session, metrics, stats);

        // Assert
        Assert.True(result.OverallScore < 50);
    }

    [Fact]
    public void ComputeScoreCard_AllScoresInValidRange()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var metrics = new List<MetricEvent>
        {
            new()
            {
                SessionId = session.Id,
                Type = "combined",
                ValueJson = "{\"eyeContact\": 75, \"headStability\": 75, \"posture\": 75, \"fidget\": 75}"
            }
        };
        var stats = new Dictionary<string, object>
        {
            { "wpm", 140 },
            { "filler_count", 3 },
            { "duration_ms", 60000 }
        };

        // Act
        var result = _scoringService.ComputeScoreCard(session, metrics, stats);

        // Assert
        Assert.InRange(result.EyeContactScore, 0, 100);
        Assert.InRange(result.SpeakingRateScore, 0, 100);
        Assert.InRange(result.FillerScore, 0, 100);
        Assert.InRange(result.PostureScore, 0, 100);
        Assert.InRange(result.OverallScore, 0, 100);
    }

    #endregion

    #region Feedback Generation Tests

    [Fact]
    public void GenerateFeedback_WithPoorEyeContact_GeneratesEyeContactFeedback()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var scoreCard = new ScoreCard
        {
            SessionId = session.Id,
            EyeContactScore = 35,
            SpeakingRateScore = 75,
            FillerScore = 75,
            PostureScore = 75,
            OverallScore = 65
        };

        // Act
        var feedback = _scoringService.GenerateFeedback(session, scoreCard, new List<MetricEvent>(), null);

        // Assert
        Assert.NotEmpty(feedback);
        Assert.Contains(feedback, f => f.Category == "Eye Contact");
    }

    [Fact]
    public void GenerateFeedback_WithExcellentPerformance_GeneratesPositiveFeedback()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var scoreCard = new ScoreCard
        {
            SessionId = session.Id,
            EyeContactScore = 90,
            SpeakingRateScore = 90,
            FillerScore = 90,
            PostureScore = 90,
            OverallScore = 90
        };

        // Act
        var feedback = _scoringService.GenerateFeedback(session, scoreCard, new List<MetricEvent>(), null);

        // Assert
        Assert.NotEmpty(feedback);
        var positiveItems = feedback.Where(f => f.Severity == 1).ToList();
        Assert.NotEmpty(positiveItems);
    }

    [Fact]
    public void GenerateFeedback_ReturnsValidCategoryAndSeverity()
    {
        // Arrange
        var session = new Session { Id = Guid.NewGuid() };
        var scoreCard = new ScoreCard
        {
            SessionId = session.Id,
            EyeContactScore = 40,
            SpeakingRateScore = 40,
            FillerScore = 40,
            PostureScore = 40,
            OverallScore = 40
        };

        // Act
        var feedback = _scoringService.GenerateFeedback(session, scoreCard, new List<MetricEvent>(), null);

        // Assert
        foreach (var item in feedback)
        {
            Assert.NotEmpty(item.Category);
            Assert.InRange(item.Severity, 1, 5);
            Assert.NotEmpty(item.Title);
            Assert.NotEmpty(item.Details);
        }
    }

    #endregion
}
