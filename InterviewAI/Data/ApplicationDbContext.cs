using Microsoft.EntityFrameworkCore;
using InterviewAI.Models;

namespace InterviewAI.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : base(options)
        {
        }

        public DbSet<User> Users { get; set; }
        public DbSet<Interview> Interviews { get; set; }
        public DbSet<InterviewQuestion> InterviewQuestions { get; set; }
        public DbSet<Feedback> Feedbacks { get; set; }
        public DbSet<QuestionFeedback> QuestionFeedbacks { get; set; }
        public DbSet<UserProgress> UserProgress { get; set; }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // User configuration
            modelBuilder.Entity<User>()
                .HasMany(u => u.Interviews)
                .WithOne(i => i.User)
                .HasForeignKey(i => i.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<User>()
                .HasMany(u => u.Progress)
                .WithOne(p => p.User)
                .HasForeignKey(p => p.UserId)
                .OnDelete(DeleteBehavior.Cascade);

            // Interview configuration
            modelBuilder.Entity<Interview>()
                .HasMany(i => i.Questions)
                .WithOne(q => q.Interview)
                .HasForeignKey(q => q.InterviewId)
                .OnDelete(DeleteBehavior.Cascade);

            modelBuilder.Entity<Interview>()
                .HasMany(i => i.Feedbacks)
                .WithOne(f => f.Interview)
                .HasForeignKey(f => f.InterviewId)
                .OnDelete(DeleteBehavior.Cascade);

            // InterviewQuestion configuration
            modelBuilder.Entity<InterviewQuestion>()
                .HasMany(q => q.QuestionFeedbacks)
                .WithOne(qf => qf.Question)
                .HasForeignKey(qf => qf.QuestionId)
                .OnDelete(DeleteBehavior.Cascade);

            // Indices for performance
            modelBuilder.Entity<User>()
                .HasIndex(u => u.Email)
                .IsUnique();

            modelBuilder.Entity<Interview>()
                .HasIndex(i => i.UserId);

            modelBuilder.Entity<InterviewQuestion>()
                .HasIndex(q => q.InterviewId);

            modelBuilder.Entity<Feedback>()
                .HasIndex(f => f.InterviewId);

            modelBuilder.Entity<UserProgress>()
                .HasIndex(p => p.UserId);
        }
    }
}
