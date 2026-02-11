using System;

namespace InterviewAI.Models
{
    public class UserProgress
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Metric { get; set; } // TechnicalKnowledge, Communication, ProblemSolving, etc.
        public double CurrentScore { get; set; } // 0-100 scale
        public double PreviousScore { get; set; }
        public int TotalInterviewsCompleted { get; set; }
        public int TotalQuestionsAttempted { get; set; }
        public double AverageAccuracy { get; set; }
        public int WeakAreas { get; set; } // Count of identified weak areas
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public User User { get; set; }
    }
}
