using System;
using System.Collections.Generic;

namespace InterviewAI.Models
{
    public class InterviewQuestion
    {
        public int Id { get; set; }
        public int InterviewId { get; set; }
        public string Question { get; set; }
        public string QuestionType { get; set; } // Technical, Behavioral, HR, etc.
        public string UserAnswer { get; set; }
        public string IdealAnswer { get; set; }
        public int OrderNumber { get; set; }
        public DateTime AskedAt { get; set; } = DateTime.UtcNow;
        public DateTime? AnsweredAt { get; set; }
        public double TimeSpentSeconds { get; set; }
        public double Score { get; set; } // 0-100 scale
        public string Difficulty { get; set; } // Easy, Medium, Hard
        
        // Navigation properties
        public Interview Interview { get; set; }
        public ICollection<QuestionFeedback> QuestionFeedbacks { get; set; } = new List<QuestionFeedback>();
    }
}
