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

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // Session
        modelBuilder.Entity<Session>()
            .HasKey(s => s.Id);
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

        // MetricEvent
        modelBuilder.Entity<MetricEvent>()
            .HasKey(m => m.Id);

        // FeedbackItem
        modelBuilder.Entity<FeedbackItem>()
            .HasKey(f => f.Id);

        // ScoreCard
        modelBuilder.Entity<ScoreCard>()
            .HasKey(sc => sc.Id);
    }
}
