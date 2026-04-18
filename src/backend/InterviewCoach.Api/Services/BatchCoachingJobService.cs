using System.Diagnostics;
using System.Text.Json;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public interface IBatchCoachingJobService
{
    Task<BatchCoachingJobCreateResult> CreateJobAsync(BatchCoachingJobCreateRequest request, Guid? createdByUserId, CancellationToken cancellationToken = default);
    Task<List<BatchCoachingJobSummaryDto>> GetJobsAsync(int take, CancellationToken cancellationToken = default);
    Task<BatchCoachingJobDetailDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<BatchCoachingJobItemsPageDto?> GetJobItemsAsync(Guid jobId, string? status, int take, int skip, CancellationToken cancellationToken = default);
    Task<BatchCoachingJobDetailDto?> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default);
    Task<bool> ResetStaleRunningJobsAsync(CancellationToken cancellationToken = default);
    Task<bool> ProcessNextQueuedJobAsync(CancellationToken cancellationToken = default);
}

public class BatchCoachingJobService : IBatchCoachingJobService
{
    private const string CoachKind = "coach";

    private readonly ApplicationDbContext _db;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<BatchCoachingOptions> _optionsMonitor;
    private readonly ApiTelemetry _telemetry;
    private readonly ILogger<BatchCoachingJobService> _logger;

    public BatchCoachingJobService(
        ApplicationDbContext db,
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<BatchCoachingOptions> optionsMonitor,
        ApiTelemetry telemetry,
        ILogger<BatchCoachingJobService> logger)
    {
        _db = db;
        _scopeFactory = scopeFactory;
        _optionsMonitor = optionsMonitor;
        _telemetry = telemetry;
        _logger = logger;
    }

    public async Task<BatchCoachingJobCreateResult> CreateJobAsync(BatchCoachingJobCreateRequest request, Guid? createdByUserId, CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            return BatchCoachingJobCreateResult.Failed("Batch coaching is disabled.");
        }

        var hasExplicit = request.SessionIds is { Count: > 0 };
        var hasFilters = request.Filters is not null;

        if (!hasExplicit && !hasFilters)
        {
            return BatchCoachingJobCreateResult.Failed("sessionIds or filters must be provided.");
        }

        var hardMax = Math.Min(1000, options.MaxSessionsPerJob);
        var requestedMax = request.Options?.MaxSessions is > 0 ? request.Options.MaxSessions.Value : options.MaxSessionsPerJob;
        var maxSessions = Math.Clamp(requestedMax, 1, hardMax);

        List<Guid> targetSessionIds;
        if (hasExplicit)
        {
            targetSessionIds = request.SessionIds!
                .Where(id => id != Guid.Empty)
                .Distinct()
                .Take(maxSessions)
                .ToList();

            if (targetSessionIds.Count == 0)
            {
                return BatchCoachingJobCreateResult.Failed("No valid sessionIds were provided.");
            }

            var existingIds = await _db.Sessions
                .AsNoTracking()
                .Where(s => targetSessionIds.Contains(s.Id))
                .Select(s => s.Id)
                .ToListAsync(cancellationToken);

            targetSessionIds = targetSessionIds.Where(id => existingIds.Contains(id)).ToList();
        }
        else
        {
            targetSessionIds = await ResolveSessionIdsByFilterAsync(request.Filters!, maxSessions, cancellationToken);
        }

        if (targetSessionIds.Count == 0)
        {
            return BatchCoachingJobCreateResult.Failed("No sessions matched the selection criteria.");
        }

        var filtersJson = JsonSerializer.Serialize(request.Filters ?? new BatchCoachingJobFilterDto());
        var optionsDto = request.Options ?? new BatchCoachingJobOptionsDto();
        optionsDto.MaxSessions = maxSessions;
        optionsDto.Filters = request.Filters;
        var optionsJson = JsonSerializer.Serialize(optionsDto);

        var job = new BatchCoachingJob
        {
            Id = Guid.NewGuid(),
            CreatedAtUtc = DateTime.UtcNow,
            Status = BatchCoachingJobStatus.Queued,
            CreatedByUserId = createdByUserId,
            FiltersJson = filtersJson,
            OptionsJson = optionsJson,
            TotalSessions = targetSessionIds.Count,
            ProcessedSessions = 0,
            SucceededSessions = 0,
            FailedSessions = 0,
            SkippedSessions = 0,
            ProgressPercent = 0
        };

        var items = targetSessionIds.Select(sessionId => new BatchCoachingJobItem
        {
            Id = Guid.NewGuid(),
            JobId = job.Id,
            SessionId = sessionId,
            Status = BatchCoachingJobItemStatus.Pending,
            Attempts = 0
        }).ToList();

        _db.BatchCoachingJobs.Add(job);
        _db.BatchCoachingJobItems.AddRange(items);
        await _db.SaveChangesAsync(cancellationToken);

        using (_logger.BeginScope(new Dictionary<string, object?> { ["jobId"] = job.Id }))
        {
            _logger.LogInformation(
                "Batch coaching job created: totalSessions={totalSessions}, createdByUserId={createdByUserId}",
                job.TotalSessions,
                job.CreatedByUserId);
        }

        return BatchCoachingJobCreateResult.Succeeded(job.Id, job.Status, job.TotalSessions);
    }

    public async Task<List<BatchCoachingJobSummaryDto>> GetJobsAsync(int take, CancellationToken cancellationToken = default)
    {
        take = Math.Clamp(take, 1, 100);

        return await _db.BatchCoachingJobs
            .AsNoTracking()
            .OrderByDescending(j => j.CreatedAtUtc)
            .Take(take)
            .Select(j => new BatchCoachingJobSummaryDto
            {
                JobId = j.Id,
                CreatedAtUtc = j.CreatedAtUtc,
                StartedAtUtc = j.StartedAtUtc,
                CompletedAtUtc = j.CompletedAtUtc,
                Status = j.Status,
                TotalSessions = j.TotalSessions,
                ProcessedSessions = j.ProcessedSessions,
                SucceededSessions = j.SucceededSessions,
                FailedSessions = j.FailedSessions,
                SkippedSessions = j.SkippedSessions,
                ProgressPercent = j.ProgressPercent,
                LastError = j.LastError
            })
            .ToListAsync(cancellationToken);
    }

    public async Task<BatchCoachingJobDetailDto?> GetJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _db.BatchCoachingJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => new BatchCoachingJobDetailDto
            {
                JobId = j.Id,
                CreatedAtUtc = j.CreatedAtUtc,
                StartedAtUtc = j.StartedAtUtc,
                CompletedAtUtc = j.CompletedAtUtc,
                Status = j.Status,
                CreatedByUserId = j.CreatedByUserId,
                FiltersJson = j.FiltersJson,
                OptionsJson = j.OptionsJson,
                TotalSessions = j.TotalSessions,
                ProcessedSessions = j.ProcessedSessions,
                SucceededSessions = j.SucceededSessions,
                FailedSessions = j.FailedSessions,
                SkippedSessions = j.SkippedSessions,
                ProgressPercent = j.ProgressPercent,
                LastError = j.LastError
            })
            .FirstOrDefaultAsync(cancellationToken);

        return job;
    }

    public async Task<BatchCoachingJobItemsPageDto?> GetJobItemsAsync(Guid jobId, string? status, int take, int skip, CancellationToken cancellationToken = default)
    {
        var exists = await _db.BatchCoachingJobs.AsNoTracking().AnyAsync(j => j.Id == jobId, cancellationToken);
        if (!exists)
        {
            return null;
        }

        take = Math.Clamp(take, 1, 500);
        skip = Math.Max(0, skip);

        var query = _db.BatchCoachingJobItems
            .AsNoTracking()
            .Where(i => i.JobId == jobId);

        if (!string.IsNullOrWhiteSpace(status))
        {
            query = query.Where(i => i.Status == status);
        }

        var total = await query.CountAsync(cancellationToken);

        var items = await query
            .OrderBy(i => i.Id)
            .Skip(skip)
            .Take(take)
            .Select(i => new BatchCoachingJobItemDto
            {
                ItemId = i.Id,
                SessionId = i.SessionId,
                Status = i.Status,
                Attempts = i.Attempts,
                StartedAtUtc = i.StartedAtUtc,
                CompletedAtUtc = i.CompletedAtUtc,
                ResultSource = i.ResultSource,
                LlmRunId = i.LlmRunId,
                Error = i.Error
            })
            .ToListAsync(cancellationToken);

        return new BatchCoachingJobItemsPageDto
        {
            JobId = jobId,
            Total = total,
            Skip = skip,
            Take = take,
            Items = items
        };
    }

    public async Task<BatchCoachingJobDetailDto?> CancelJobAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        var job = await _db.BatchCoachingJobs.FirstOrDefaultAsync(j => j.Id == jobId, cancellationToken);
        if (job == null)
        {
            return null;
        }

        if (job.Status is BatchCoachingJobStatus.Completed or BatchCoachingJobStatus.Failed)
        {
            return await GetJobAsync(jobId, cancellationToken);
        }

        job.Status = BatchCoachingJobStatus.Canceled;
        if (!job.CompletedAtUtc.HasValue)
        {
            job.CompletedAtUtc = DateTime.UtcNow;
        }

        await _db.SaveChangesAsync(cancellationToken);

        using (_logger.BeginScope(new Dictionary<string, object?> { ["jobId"] = job.Id }))
        {
            _logger.LogInformation("Batch coaching job canceled.");
        }

        return await GetJobAsync(jobId, cancellationToken);
    }

    public async Task<bool> ResetStaleRunningJobsAsync(CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.ResetStaleRunningJobsOnStartup)
        {
            return false;
        }

        var threshold = DateTime.UtcNow.AddMinutes(-Math.Abs(options.StaleRunningJobMinutes));
        var staleJobs = await _db.BatchCoachingJobs
            .Where(j => j.Status == BatchCoachingJobStatus.Running &&
                        ((j.StartedAtUtc.HasValue && j.StartedAtUtc < threshold) || !j.StartedAtUtc.HasValue))
            .ToListAsync(cancellationToken);

        if (staleJobs.Count == 0)
        {
            return false;
        }

        foreach (var job in staleJobs)
        {
            job.Status = BatchCoachingJobStatus.Queued;
            job.LastError = "Reset stale running job on startup.";
        }

        await _db.SaveChangesAsync(cancellationToken);
        _logger.LogWarning("Reset {count} stale running batch coaching jobs to queued.", staleJobs.Count);
        return true;
    }

    public async Task<bool> ProcessNextQueuedJobAsync(CancellationToken cancellationToken = default)
    {
        var options = _optionsMonitor.CurrentValue;
        if (!options.Enabled)
        {
            return false;
        }

        var job = await _db.BatchCoachingJobs
            .OrderBy(j => j.CreatedAtUtc)
            .FirstOrDefaultAsync(j => j.Status == BatchCoachingJobStatus.Queued, cancellationToken);

        if (job == null)
        {
            return false;
        }

        job.Status = BatchCoachingJobStatus.Running;
        job.StartedAtUtc = DateTime.UtcNow;
        job.LastError = null;
        await _db.SaveChangesAsync(cancellationToken);

        var sw = Stopwatch.StartNew();
        using var scope = _logger.BeginScope(new Dictionary<string, object?> { ["jobId"] = job.Id });
        _logger.LogInformation("Batch coaching job started: totalSessions={totalSessions}", job.TotalSessions);

        var jobOptions = ParseOptions(job.OptionsJson, options);
        var parallelism = Math.Clamp(jobOptions.Parallelism ?? options.MaxParallelism, 1, options.MaxParallelism);
        var stopOnError = jobOptions.StopOnError;

        var pendingIds = await _db.BatchCoachingJobItems
            .AsNoTracking()
            .Where(i => i.JobId == job.Id && i.Status == BatchCoachingJobItemStatus.Pending)
            .OrderBy(i => i.Id)
            .Select(i => i.Id)
            .ToListAsync(cancellationToken);

        var running = new List<Task<BatchCoachingItemOutcome>>();
        var nextIndex = 0;
        var stopScheduling = false;

        while ((nextIndex < pendingIds.Count || running.Count > 0) && !cancellationToken.IsCancellationRequested)
        {
            while (!stopScheduling && nextIndex < pendingIds.Count && running.Count < parallelism)
            {
                var latestStatus = await _db.BatchCoachingJobs
                    .AsNoTracking()
                    .Where(j => j.Id == job.Id)
                    .Select(j => j.Status)
                    .FirstAsync(cancellationToken);

                if (latestStatus == BatchCoachingJobStatus.Canceled)
                {
                    stopScheduling = true;
                    break;
                }

                var itemId = pendingIds[nextIndex++];
                running.Add(ProcessItemInScopeAsync(job.Id, itemId, jobOptions, cancellationToken));
            }

            if (running.Count == 0)
            {
                break;
            }

            var completed = await Task.WhenAny(running);
            running.Remove(completed);

            BatchCoachingItemOutcome outcome;
            try
            {
                outcome = await completed;
            }
            catch (Exception ex)
            {
                outcome = new BatchCoachingItemOutcome
                {
                    Status = BatchCoachingJobItemStatus.Failed,
                    Error = ex.Message,
                    Processed = true
                };
            }

            await ApplyOutcomeAsync(job.Id, outcome, cancellationToken);

            if (stopOnError && outcome.Status == BatchCoachingJobItemStatus.Failed)
            {
                stopScheduling = true;
            }
        }

        while (running.Count > 0)
        {
            var completed = await Task.WhenAny(running);
            running.Remove(completed);
            BatchCoachingItemOutcome outcome;
            try
            {
                outcome = await completed;
            }
            catch (Exception ex)
            {
                outcome = new BatchCoachingItemOutcome
                {
                    Status = BatchCoachingJobItemStatus.Failed,
                    Error = ex.Message,
                    Processed = true
                };
            }

            await ApplyOutcomeAsync(job.Id, outcome, cancellationToken);
        }

        var finalJob = await _db.BatchCoachingJobs.FirstAsync(j => j.Id == job.Id, cancellationToken);
        if (finalJob.Status != BatchCoachingJobStatus.Canceled)
        {
            if (stopOnError && finalJob.FailedSessions > 0)
            {
                finalJob.Status = BatchCoachingJobStatus.Failed;
            }
            else
            {
                finalJob.Status = BatchCoachingJobStatus.Completed;
            }
        }

        finalJob.CompletedAtUtc = DateTime.UtcNow;
        finalJob.ProgressPercent = finalJob.TotalSessions == 0
            ? 100
            : Math.Round(100d * finalJob.ProcessedSessions / finalJob.TotalSessions, 2);

        await _db.SaveChangesAsync(cancellationToken);

        sw.Stop();
        _telemetry.LlmBatchJobsTotal.Add(1, new KeyValuePair<string, object?>("status", finalJob.Status));
        _telemetry.LlmBatchJobDurationMs.Record(sw.Elapsed.TotalMilliseconds);

        _logger.LogInformation(
            "Batch coaching job completed: status={status}, processed={processed}, succeeded={succeeded}, failed={failed}, skipped={skipped}",
            finalJob.Status,
            finalJob.ProcessedSessions,
            finalJob.SucceededSessions,
            finalJob.FailedSessions,
            finalJob.SkippedSessions);

        return true;
    }

    private async Task ApplyOutcomeAsync(Guid jobId, BatchCoachingItemOutcome outcome, CancellationToken cancellationToken)
    {
        var job = await _db.BatchCoachingJobs.FirstAsync(j => j.Id == jobId, cancellationToken);

        if (outcome.Processed)
        {
            job.ProcessedSessions += 1;
        }

        switch (outcome.Status)
        {
            case BatchCoachingJobItemStatus.Succeeded:
                job.SucceededSessions += 1;
                _telemetry.LlmBatchItemsTotal.Add(1, new KeyValuePair<string, object?>("status", "Succeeded"));
                break;
            case BatchCoachingJobItemStatus.Failed:
                job.FailedSessions += 1;
                job.LastError = outcome.Error;
                _telemetry.LlmBatchItemsTotal.Add(1, new KeyValuePair<string, object?>("status", "Failed"));
                break;
            case BatchCoachingJobItemStatus.Skipped:
                job.SkippedSessions += 1;
                _telemetry.LlmBatchItemsTotal.Add(1, new KeyValuePair<string, object?>("status", "Skipped"));
                break;
        }

        job.ProgressPercent = job.TotalSessions == 0
            ? 100
            : Math.Round(100d * job.ProcessedSessions / job.TotalSessions, 2);

        await _db.SaveChangesAsync(cancellationToken);

        if (outcome.DurationMs > 0)
        {
            _telemetry.LlmBatchItemDurationMs.Record(outcome.DurationMs);
        }
    }

    private async Task<BatchCoachingItemOutcome> ProcessItemInScopeAsync(
        Guid jobId,
        Guid itemId,
        BatchCoachingJobOptionsDto jobOptions,
        CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();

        using var scope = _scopeFactory.CreateScope();
        var scopedDb = scope.ServiceProvider.GetRequiredService<ApplicationDbContext>();
        var orchestrator = scope.ServiceProvider.GetRequiredService<ILlmCoachingOrchestrator>();
        var options = _optionsMonitor.CurrentValue;

        var item = await scopedDb.BatchCoachingJobItems.FirstOrDefaultAsync(i => i.Id == itemId && i.JobId == jobId, cancellationToken);
        if (item == null)
        {
            return new BatchCoachingItemOutcome
            {
                Status = BatchCoachingJobItemStatus.Failed,
                Error = "Job item not found.",
                Processed = true
            };
        }

        var jobStatus = await scopedDb.BatchCoachingJobs
            .AsNoTracking()
            .Where(j => j.Id == jobId)
            .Select(j => j.Status)
            .FirstOrDefaultAsync(cancellationToken);

        if (jobStatus == BatchCoachingJobStatus.Canceled)
        {
            return new BatchCoachingItemOutcome
            {
                Status = BatchCoachingJobItemStatus.Skipped,
                Error = "Job canceled.",
                Processed = false
            };
        }

        item.Status = BatchCoachingJobItemStatus.Running;
        item.StartedAtUtc = DateTime.UtcNow;
        item.Attempts += 1;
        await scopedDb.SaveChangesAsync(cancellationToken);

        var maxAttempts = Math.Max(1, 1 + options.ItemRetryCount);

        BatchCoachingItemOutcome? finalOutcome = null;
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            var sessionExists = await scopedDb.Sessions.AsNoTracking().AnyAsync(s => s.Id == item.SessionId, cancellationToken);
            if (!sessionExists)
            {
                finalOutcome = new BatchCoachingItemOutcome
                {
                    Status = BatchCoachingJobItemStatus.Skipped,
                    Error = "Session not found.",
                    Processed = true
                };
                break;
            }

            var onlyIfNoCoach = jobOptions.Filters?.OnlyIfNoCoach == true;
            var force = jobOptions.Force;
            if (onlyIfNoCoach && !force)
            {
                var hasCoach = await scopedDb.LlmRuns
                    .AsNoTracking()
                    .AnyAsync(r => r.SessionId == item.SessionId && r.Kind == CoachKind, cancellationToken);

                if (hasCoach)
                {
                    finalOutcome = new BatchCoachingItemOutcome
                    {
                        Status = BatchCoachingJobItemStatus.Skipped,
                        Error = "Skipped because coaching already exists.",
                        Processed = true
                    };
                    break;
                }
            }

            var result = await orchestrator.ExecuteAsync(item.SessionId, force, cancellationToken);
            if (result.NotFound)
            {
                finalOutcome = new BatchCoachingItemOutcome
                {
                    Status = BatchCoachingJobItemStatus.Skipped,
                    Error = "Session not found during processing.",
                    Processed = true
                };
                break;
            }

            if (result.Success)
            {
                var latestRunId = await scopedDb.LlmRuns
                    .AsNoTracking()
                    .Where(r => r.SessionId == item.SessionId && r.Kind == CoachKind)
                    .OrderByDescending(r => r.CreatedAt)
                    .Select(r => (Guid?)r.Id)
                    .FirstOrDefaultAsync(cancellationToken);

                finalOutcome = new BatchCoachingItemOutcome
                {
                    Status = BatchCoachingJobItemStatus.Succeeded,
                    ResultSource = NormalizeResultSource(result.Metadata.SourcePath),
                    LlmRunId = latestRunId,
                    Processed = true
                };
                break;
            }

            finalOutcome = new BatchCoachingItemOutcome
            {
                Status = BatchCoachingJobItemStatus.Failed,
                Error = result.ErrorMessage ?? result.Metadata.ErrorSummary ?? "LLM orchestration failed.",
                Processed = true
            };

            if (attempt < maxAttempts)
            {
                item.Attempts += 1;
                await scopedDb.SaveChangesAsync(cancellationToken);
            }
        }

        finalOutcome ??= new BatchCoachingItemOutcome
        {
            Status = BatchCoachingJobItemStatus.Failed,
            Error = "Unknown batch item processing failure.",
            Processed = true
        };

        item.Status = finalOutcome.Status;
        item.ResultSource = finalOutcome.ResultSource;
        item.LlmRunId = finalOutcome.LlmRunId;
        item.Error = finalOutcome.Error;
        item.CompletedAtUtc = DateTime.UtcNow;
        await scopedDb.SaveChangesAsync(cancellationToken);

        sw.Stop();
        finalOutcome.DurationMs = sw.Elapsed.TotalMilliseconds;

        if (finalOutcome.Status == BatchCoachingJobItemStatus.Failed)
        {
            using (_logger.BeginScope(new Dictionary<string, object?> { ["jobId"] = jobId, ["itemId"] = itemId, ["sessionId"] = item.SessionId }))
            {
                _logger.LogWarning("Batch coaching item failed: error={error}", finalOutcome.Error);
            }
        }

        return finalOutcome;
    }

    private async Task<List<Guid>> ResolveSessionIdsByFilterAsync(BatchCoachingJobFilterDto filters, int take, CancellationToken cancellationToken)
    {
        var query = _db.Sessions.AsNoTracking().AsQueryable();

        if (filters.CreatedFromUtc.HasValue)
        {
            query = query.Where(s => s.CreatedAt >= filters.CreatedFromUtc.Value);
        }

        if (filters.CreatedToUtc.HasValue)
        {
            query = query.Where(s => s.CreatedAt <= filters.CreatedToUtc.Value);
        }

        if (!string.IsNullOrWhiteSpace(filters.Language))
        {
            var lang = filters.Language.Trim().ToLowerInvariant();
            query = query.Where(s => s.Language.ToLower() == lang);
        }

        if (!string.IsNullOrWhiteSpace(filters.RoleContains))
        {
            var rolePart = filters.RoleContains.Trim().ToLowerInvariant();
            query = query.Where(s => s.SelectedRole.ToLower().Contains(rolePart));
        }

        if (filters.OnlyIfNoCoach)
        {
            query = query.Where(s => !_db.LlmRuns.Any(r => r.SessionId == s.Id && r.Kind == CoachKind));
        }

        return await query
            .OrderByDescending(s => s.CreatedAt)
            .Select(s => s.Id)
            .Take(take)
            .ToListAsync(cancellationToken);
    }

    private static BatchCoachingJobOptionsDto ParseOptions(string optionsJson, BatchCoachingOptions defaults)
    {
        try
        {
            var parsed = JsonSerializer.Deserialize<BatchCoachingJobOptionsDto>(optionsJson) ?? new BatchCoachingJobOptionsDto();
            parsed.Parallelism = parsed.Parallelism is > 0 ? parsed.Parallelism : defaults.MaxParallelism;
            parsed.MaxSessions = parsed.MaxSessions is > 0 ? parsed.MaxSessions : defaults.MaxSessionsPerJob;
            return parsed;
        }
        catch
        {
            return new BatchCoachingJobOptionsDto
            {
                Parallelism = defaults.MaxParallelism,
                MaxSessions = defaults.MaxSessionsPerJob
            };
        }
    }

    private static string NormalizeResultSource(string sourcePath)
    {
        if (sourcePath.StartsWith("cache", StringComparison.OrdinalIgnoreCase))
            return "cache";

        if (string.Equals(sourcePath, "fallback_model", StringComparison.OrdinalIgnoreCase))
            return "fallback";

        return "primary";
    }
}

public sealed class BatchCoachingWorker : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<BatchCoachingOptions> _optionsMonitor;
    private readonly ILogger<BatchCoachingWorker> _logger;
    private bool _startupResetDone;

    public BatchCoachingWorker(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<BatchCoachingOptions> optionsMonitor,
        ILogger<BatchCoachingWorker> logger)
    {
        _scopeFactory = scopeFactory;
        _optionsMonitor = optionsMonitor;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var scope = _scopeFactory.CreateScope();
                var service = scope.ServiceProvider.GetRequiredService<IBatchCoachingJobService>();

                if (!_startupResetDone)
                {
                    await service.ResetStaleRunningJobsAsync(stoppingToken);
                    _startupResetDone = true;
                }

                await service.ProcessNextQueuedJobAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Batch coaching worker loop failed.");
            }

            var delaySeconds = Math.Clamp(_optionsMonitor.CurrentValue.PollIntervalSeconds, 1, 300);
            await Task.Delay(TimeSpan.FromSeconds(delaySeconds), stoppingToken);
        }
    }
}

public class BatchCoachingJobCreateRequest
{
    public List<Guid>? SessionIds { get; set; }
    public BatchCoachingJobFilterDto? Filters { get; set; }
    public BatchCoachingJobOptionsDto? Options { get; set; }
}

public class BatchCoachingJobFilterDto
{
    public DateTime? CreatedFromUtc { get; set; }
    public DateTime? CreatedToUtc { get; set; }
    public string? Language { get; set; }
    public string? RoleContains { get; set; }
    public bool OnlyIfNoCoach { get; set; }
}

public class BatchCoachingJobOptionsDto
{
    public bool Force { get; set; }
    public int? MaxSessions { get; set; }
    public int? Parallelism { get; set; }
    public bool StopOnError { get; set; }
    public BatchCoachingJobFilterDto? Filters { get; set; }
}

public class BatchCoachingJobCreateResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public Guid JobId { get; set; }
    public string Status { get; set; } = BatchCoachingJobStatus.Queued;
    public int TotalSessions { get; set; }

    public static BatchCoachingJobCreateResult Succeeded(Guid jobId, string status, int totalSessions) =>
        new()
        {
            Success = true,
            JobId = jobId,
            Status = status,
            TotalSessions = totalSessions
        };

    public static BatchCoachingJobCreateResult Failed(string error) =>
        new()
        {
            Success = false,
            Error = error
        };
}

public class BatchCoachingJobSummaryDto
{
    public Guid JobId { get; set; }
    public DateTime CreatedAtUtc { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string Status { get; set; } = string.Empty;
    public int TotalSessions { get; set; }
    public int ProcessedSessions { get; set; }
    public int SucceededSessions { get; set; }
    public int FailedSessions { get; set; }
    public int SkippedSessions { get; set; }
    public double? ProgressPercent { get; set; }
    public string? LastError { get; set; }
}

public class BatchCoachingJobDetailDto : BatchCoachingJobSummaryDto
{
    public Guid? CreatedByUserId { get; set; }
    public string FiltersJson { get; set; } = "{}";
    public string OptionsJson { get; set; } = "{}";
}

public class BatchCoachingJobItemsPageDto
{
    public Guid JobId { get; set; }
    public int Total { get; set; }
    public int Skip { get; set; }
    public int Take { get; set; }
    public List<BatchCoachingJobItemDto> Items { get; set; } = [];
}

public class BatchCoachingJobItemDto
{
    public Guid ItemId { get; set; }
    public Guid SessionId { get; set; }
    public string Status { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public DateTime? StartedAtUtc { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public string? ResultSource { get; set; }
    public Guid? LlmRunId { get; set; }
    public string? Error { get; set; }
}

internal class BatchCoachingItemOutcome
{
    public string Status { get; set; } = BatchCoachingJobItemStatus.Failed;
    public string? ResultSource { get; set; }
    public Guid? LlmRunId { get; set; }
    public string? Error { get; set; }
    public bool Processed { get; set; }
    public double DurationMs { get; set; }
}
