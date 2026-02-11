using System;

namespace InterviewAI.Models
{
    public class Feedback
    {
        public int Id { get; set; }
        public int InterviewId { get; set; }
        public string Category { get; set; } // Communication, Technical Knowledge, Problem Solving, etc.
        public string Comment { get; set; } // AI-generated feedback
        public int SeverityLevel { get; set; } // 1: Low, 2: Medium, 3: High
        public string Recommendation { get; set; } // Improvement suggestion
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public bool IsAddressed { get; set; } = false;
        
        // Navigation properties
        public Interview Interview { get; set; }
    }
}
