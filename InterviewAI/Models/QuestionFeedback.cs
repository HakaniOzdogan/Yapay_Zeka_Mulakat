using System;

namespace InterviewAI.Models
{
    public class QuestionFeedback
    {
        public int Id { get; set; }
        public int QuestionId { get; set; }
        public string Strength { get; set; } // What was done well
        public string Weakness { get; set; } // What needs improvement
        public string Suggestion { get; set; } // How to improve
        public int ImprovementScore { get; set; } // 0-100 scale for potential improvement
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public InterviewQuestion Question { get; set; }
    }
}
