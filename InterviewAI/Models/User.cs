using System.Collections.Generic;

namespace InterviewAI.Models
{
    public class User
    {
        public int Id { get; set; }
        public string Username { get; set; }
        public string Email { get; set; }
        public string PasswordHash { get; set; }
        public string FullName { get; set; }
        public string CV { get; set; } // CV/Resume content
        public string Profession { get; set; }
        public int ExperienceLevel { get; set; } // 1: Beginner, 2: Intermediate, 3: Advanced
        public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
        public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;
        
        // Navigation properties
        public ICollection<Interview> Interviews { get; set; } = new List<Interview>();
        public ICollection<UserProgress> Progress { get; set; } = new List<UserProgress>();
    }
}
