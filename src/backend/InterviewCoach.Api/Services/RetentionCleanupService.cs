using InterviewCoach.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public interface IRetentionCleanupService
{
    Task<RetentionRunSummary> RunOnceAsync(bool respectEnabled, CancellationToken cancellationToken = default);
}

public class RetentionCleanupService : IRetentionCleanupService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<RetentionOptions> _optionsMonitor;
    private readonly IRetentionRunState _state;
    private readonly ILogger<RetentionCleanupService> _logger;

    public RetentionCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> optionsMonitor,
        IRetentionRunState state,
        ILogger<RetentionCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _optionsMonitor = optionsMonitor;
        _state = state;
        _logger = logger;
    }

    public async Task<RetentionRunSummary> RunOnceAsync(bool respectEnabled, CancellationToken cancellationToken = default)
    {
        var options = Normalize(_optionsMonitor.CurrentValue);

        if (respectEnabled && !options.Enabled)
        {
            var skipped = new RetentionRunSummary { RanAtUtc = DateTime.UtcNow };
            _state.SetLastRun(skipped);
            return skipped;
        }

        var summary = new RetentionRunSummary { RanAtUtc = DateTime.UtcNow };

        var now = DateTime.UtcNow;
        var deleteCutoff = now.AddDays(-options.DeleteAfterDays);
        DateTime? pruneCutoff = null;
        if (options.KeepSummariesOnlyAfterDays.HasValue && options.KeepSummariesOnlyAfterDays.Value < options.DeleteAfterDays)
        {
            pruneCutoff = now.AddDays(-options.KeepSummariesOnlyAfterDays.Value);
        }

        using var scope = _scopeFactory.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();

        var sessionsToDelete = await db.Sessions
            .AsNoTracking()
            .Where(s => s.CreatedAt <= deleteCutoff)
            .Select(s => s.Id)
            .ToListAsync(cancellationToken);

        List<Guid> sessionsToPrune = [];
        if (pruneCutoff.HasValue)
        {
            sessionsToPrune = await db.Sessions
                .AsNoTracking()
                .Where(s => s.CreatedAt <= pruneCutoff.Value && s.CreatedAt > deleteCutoff)
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);
        }

        foreach (var sessionId in sessionsToDelete)
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            summary.AddRows("MetricEvents", await db.MetricEvents.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken));
            summary.AddRows("TranscriptSegments", await db.TranscriptSegments.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken));
            summary.AddRows("LlmRuns", await db.LlmRuns.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken));
            summary.AddRows("FeedbackItems", await db.FeedbackItems.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken));
            summary.AddRows("ScoreCards", await db.ScoreCards.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken));
            summary.AddRows("Questions", await db.Questions.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken));
            summary.AddRows("Sessions", await db.Sessions.Where(x => x.Id == sessionId).ExecuteDeleteAsync(cancellationToken));

            await tx.CommitAsync(cancellationToken);
            summary.SessionsDeleted++;
        }

        foreach (var sessionId in sessionsToPrune)
        {
            await using var tx = await db.Database.BeginTransactionAsync(cancellationToken);

            summary.AddRows("MetricEvents", await db.MetricEvents.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken));
            summary.AddRows("TranscriptSegments", await db.TranscriptSegments.Where(x => x.SessionId == sessionId).ExecuteDeleteAsync(cancellationToken));

            await tx.CommitAsync(cancellationToken);
            summary.SessionsPruned++;
        }

        _state.SetLastRun(summary);

        _logger.LogInformation(
            "Retention cleanup summary: sessionsDeleted={sessionsDeleted}, sessionsPruned={sessionsPruned}, rowsDeleted={rowsDeleted}",
            summary.SessionsDeleted,
            summary.SessionsPruned,
            string.Join(", ", summary.RowsDeleted.Select(kvp => $"{kvp.Key}:{kvp.Value}")));

        return summary;
    }

    private static RetentionOptions Normalize(RetentionOptions options)
    {
        options.DeleteAfterDays = options.DeleteAfterDays <= 0 ? 30 : options.DeleteAfterDays;
        options.RunHourUtc = Math.Clamp(options.RunHourUtc, 0, 23);

        if (options.KeepSummariesOnlyAfterDays.HasValue && options.KeepSummariesOnlyAfterDays.Value <= 0)
        {
            options.KeepSummariesOnlyAfterDays = null;
        }

        return options;
    }
}