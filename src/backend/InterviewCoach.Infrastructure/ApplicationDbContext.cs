using InterviewCoach.Domain;
using Microsoft.EntityFrameworkCore;

namespace InterviewCoach.Infrastructure;

public class ApplicationDbContext : DbContext
{
    public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
        : base(options)
    {
    }

    public DbSet<Session> Sessions { get; set; }
    public DbSet<Question> Questions { get; set; }
    public DbSet<TranscriptSegment> TranscriptSegments { get; set; }
    public DbSet<MetricEvent> MetricEvents { get; set; }
    public DbSet<FeedbackItem> FeedbackItems { get; set; }
    public DbSet<ScoreCard> ScoreCards { get; set; }
    public DbSet<LlmRun> LlmRuns { get; set; }
    public DbSet<User> Users { get; set; }
    public DbSet<BatchCoachingJob> BatchCoachingJobs { get; set; }
    public DbSet<BatchCoachingJobItem> BatchCoachingJobItems { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Session
        modelBuilder.Entity<Session>()
            .HasKey(s => s.Id);
        modelBuilder.Entity<Session>()
            .Property(s => s.ScoringProfile)
            .HasMaxLength(64);
        modelBuilder.Entity<Session>()
            .HasIndex(s => new { s.UserId, s.CreatedAt })
            .HasDatabaseName("IX_Sessions_UserId_CreatedAt");
        modelBuilder.Entity<Session>()
            .HasOne(s => s.User)
            .WithMany(u => u.Sessions)
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.SetNull);
        modelBuilder.Entity<Session>()
            .HasMany(s => s.Questions)
            .WithOne(q => q.Session)
            .HasForeignKey(q => q.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Session>()
            .HasMany(s => s.TranscriptSegments)
            .WithOne(t => t.Session)
            .HasForeignKey(t => t.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Session>()
            .HasMany(s => s.MetricEvents)
            .WithOne(m => m.Session)
            .HasForeignKey(m => m.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Session>()
            .HasMany(s => s.FeedbackItems)
            .WithOne(f => f.Session)
            .HasForeignKey(f => f.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Session>()
            .HasMany(s => s.LlmRuns)
            .WithOne(l => l.Session)
            .HasForeignKey(l => l.SessionId)
            .OnDelete(DeleteBehavior.Cascade);
        modelBuilder.Entity<Session>()
            .HasOne(s => s.ScoreCard)
            .WithOne(sc => sc.Session)
            .HasForeignKey<ScoreCard>(sc => sc.SessionId)
            .OnDelete(DeleteBehavior.Cascade);

        // Question
        modelBuilder.Entity<Question>()
            .HasKey(q => q.Id);

        // TranscriptSegment
        modelBuilder.Entity<TranscriptSegment>()
            .HasKey(t => t.Id);
        modelBuilder.Entity<TranscriptSegment>()
            .HasIndex(t => new { t.SessionId, t.StartMs })
            .HasDatabaseName("IX_TranscriptSegments_SessionId_StartMs");
        modelBuilder.Entity<TranscriptSegment>()
            .HasIndex(t => new { t.SessionId, t.ClientSegmentId })
            .HasDatabaseName("UX_TranscriptSegments_SessionId_ClientSegmentId")
            .IsUnique();

        // MetricEvent
        modelBuilder.Entity<MetricEvent>()
            .HasKey(m => m.Id);
        modelBuilder.Entity<MetricEvent>()
            .Property(m => m.PayloadJson)
            .HasColumnType("jsonb");
        modelBuilder.Entity<MetricEvent>()
            .HasIndex(m => new { m.SessionId, m.TsMs })
            .HasDatabaseName("IX_MetricEvents_SessionId_TsMs");
        modelBuilder.Entity<MetricEvent>()
            .HasIndex(m => new { m.SessionId, m.ClientEventId })
            .HasDatabaseName("UX_MetricEvents_SessionId_ClientEventId")
            .IsUnique();

        // LlmRun
        modelBuilder.Entity<LlmRun>()
            .HasKey(l => l.Id);
        modelBuilder.Entity<LlmRun>()
            .Property(l => l.OutputJson)
            .HasColumnType("jsonb");
        modelBuilder.Entity<LlmRun>()
            .HasIndex(l => new { l.SessionId, l.Kind, l.CreatedAt })
            .HasDatabaseName("IX_LlmRuns_SessionId_Kind_CreatedAt");
        modelBuilder.Entity<LlmRun>()
            .HasIndex(l => new { l.SessionId, l.Kind, l.InputHash })
            .HasDatabaseName("UX_LlmRuns_SessionId_Kind_InputHash")
            .IsUnique();

        // FeedbackItem
        modelBuilder.Entity<FeedbackItem>()
            .HasKey(f => f.Id);

        // ScoreCard
        modelBuilder.Entity<ScoreCard>()
            .HasKey(sc => sc.Id);

        // User
        modelBuilder.Entity<User>()
            .HasKey(u => u.Id);
        modelBuilder.Entity<User>()
            .Property(u => u.Email)
            .HasMaxLength(320);
        modelBuilder.Entity<User>()
            .Property(u => u.EmailNormalized)
            .HasMaxLength(320);
        modelBuilder.Entity<User>()
            .Property(u => u.Role)
            .HasMaxLength(16);
        modelBuilder.Entity<User>()
            .HasIndex(u => u.EmailNormalized)
            .HasDatabaseName("UX_Users_EmailNormalized")
            .IsUnique();
        modelBuilder.Entity<User>()
            .HasIndex(u => u.Role)
            .HasDatabaseName("IX_Users_Role");

        // BatchCoachingJob
        modelBuilder.Entity<BatchCoachingJob>()
            .HasKey(j => j.Id);
        modelBuilder.Entity<BatchCoachingJob>()
            .Property(j => j.Status)
            .HasMaxLength(16);
        modelBuilder.Entity<BatchCoachingJob>()
            .Property(j => j.FiltersJson)
            .HasColumnType("jsonb");
        modelBuilder.Entity<BatchCoachingJob>()
            .Property(j => j.OptionsJson)
            .HasColumnType("jsonb");
        modelBuilder.Entity<BatchCoachingJob>()
            .HasMany(j => j.Items)
            .WithOne(i => i.Job)
            .HasForeignKey(i => i.JobId)
            .OnDelete(DeleteBehavior.Cascade);

        // BatchCoachingJobItem
        modelBuilder.Entity<BatchCoachingJobItem>()
            .HasKey(i => i.Id);
        modelBuilder.Entity<BatchCoachingJobItem>()
            .Property(i => i.Status)
            .HasMaxLength(16);
        modelBuilder.Entity<BatchCoachingJobItem>()
            .Property(i => i.ResultSource)
            .HasMaxLength(32);
        modelBuilder.Entity<BatchCoachingJobItem>()
            .HasIndex(i => new { i.JobId, i.Status })
            .HasDatabaseName("IX_BatchCoachingJobItems_JobId_Status");
        modelBuilder.Entity<BatchCoachingJobItem>()
            .HasIndex(i => new { i.JobId, i.SessionId })
            .HasDatabaseName("UX_BatchCoachingJobItems_JobId_SessionId")
            .IsUnique();
    }
}
