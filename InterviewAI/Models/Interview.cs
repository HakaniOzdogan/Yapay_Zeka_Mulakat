using System;
using System.Collections.Generic;

namespace InterviewAI.Models
{
    public class Interview
    {
        public int Id { get; set; }
        public int UserId { get; set; }
        public string Title { get; set; }
        public string Description { get; set; }
        public string JobPosition { get; set; }
        public string JobField { get; set; } // IT, HR, Finance, etc.
        public int DifficultyLevel { get; set; } // 1: Beginner, 2: Intermediate, 3: Advanced
        public DateTime StartedAt { get; set; } = DateTime.UtcNow;
        public DateTime? CompletedAt { get; set; }
        public string Status { get; set; } = "In Progress"; // In Progress, Completed, Abandoned
        public int TotalQuestions { get; set; }
        public int CorrectAnswers { get; set; }
        public double Duration { get; set; } // Duration in minutes
        public double OverallScore { get; set; } // 0-100 scale
        
        // Navigation properties
        public User User { get; set; }
        public ICollection<InterviewQuestion> Questions { get; set; } = new List<InterviewQuestion>();
        public ICollection<Feedback> Feedbacks { get; set; } = new List<Feedback>();
    }
}
