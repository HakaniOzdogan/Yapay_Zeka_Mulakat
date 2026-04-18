
using System.Security.Cryptography;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using InterviewCoach.Domain;
using InterviewCoach.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public interface ILlmCoachingOrchestrator
{
    Task<LlmCoachingOrchestrationResult> ExecuteAsync(Guid sessionId, bool force, CancellationToken cancellationToken = default);
    Task<LlmOptimizationPreviewDto?> PreviewOptimizationAsync(Guid sessionId, CancellationToken cancellationToken = default);
}

public class LlmCoachingOrchestrator : ILlmCoachingOrchestrator
{
    private const string CoachKind = "coach";

    private readonly ApplicationDbContext _db;
    private readonly IEvidenceSummaryService _evidenceSummaryService;
    private readonly ILlmCoachingService _llmCoachingService;
    private readonly ILlmCoachingGuardrailsService _guardrailsService;
    private readonly ILlmOptimizationService _optimizationService;
    private readonly ApiTelemetry _telemetry;
    private readonly ILogger<LlmCoachingOrchestrator> _logger;
    private readonly LlmOptions _llmOptions;

    public LlmCoachingOrchestrator(
        ApplicationDbContext db,
        IEvidenceSummaryService evidenceSummaryService,
        ILlmCoachingService llmCoachingService,
        ILlmCoachingGuardrailsService guardrailsService,
        ILlmOptimizationService optimizationService,
        ApiTelemetry telemetry,
        ILogger<LlmCoachingOrchestrator> logger,
        IOptions<LlmOptions> llmOptions)
    {
        _db = db;
        _evidenceSummaryService = evidenceSummaryService;
        _llmCoachingService = llmCoachingService;
        _guardrailsService = guardrailsService;
        _optimizationService = optimizationService;
        _telemetry = telemetry;
        _logger = logger;
        _llmOptions = llmOptions.Value ?? new LlmOptions();
    }

    public async Task<LlmOptimizationPreviewDto?> PreviewOptimizationAsync(Guid sessionId, CancellationToken cancellationToken = default)
    {
        var sessionExists = await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId, cancellationToken);
        if (!sessionExists)
        {
            return null;
        }

        var summary = await _evidenceSummaryService.BuildAsync(sessionId, cancellationToken);
        if (summary == null)
        {
            return null;
        }

        var plan = _optimizationService.BuildPlan(summary);
        return new LlmOptimizationPreviewDto
        {
            SessionId = sessionId,
            ComplexityScore = plan.ComplexityScore,
            ComplexityBand = plan.ComplexityBand,
            TierSelected = TierToText(plan.TierUsed),
            ModelSelected = plan.ModelChosen,
            Estimates = new LlmOptimizationEstimateDto
            {
                OriginalEvidenceChars = plan.OriginalEvidenceChars,
                CompactedEvidenceChars = plan.CompactedEvidenceChars,
                PromptEstimatedChars = plan.PromptEstimatedChars,
                BudgetChars = plan.PromptBudgetChars
            },
            Dropped = plan.Dropped,
            Warnings = plan.Warnings
        };
    }

    public async Task<LlmCoachingOrchestrationResult> ExecuteAsync(Guid sessionId, bool force, CancellationToken cancellationToken = default)
    {
        var sessionExists = await _db.Sessions.AsNoTracking().AnyAsync(s => s.Id == sessionId, cancellationToken);
        if (!sessionExists)
        {
            return LlmCoachingOrchestrationResult.CreateNotFound();
        }

        var summary = await _evidenceSummaryService.BuildAsync(sessionId, cancellationToken);
        if (summary == null)
        {
            return LlmCoachingOrchestrationResult.CreateNotFound();
        }

        var optimizationPlan = _optimizationService.BuildPlan(summary);
        var optimizationMeta = ToOptimizationMetadata(optimizationPlan);

        var canonicalInputJson = CanonicalizeJson(JsonSerializer.Serialize(summary));
        var inputHash = ComputeSha256Hex(canonicalInputJson);
        var promptVersion = string.IsNullOrWhiteSpace(_llmOptions.PromptVersionCoach) ? "coach_v1" : _llmOptions.PromptVersionCoach;

        var metadata = new LlmCoachingOrchestrationMetadata
        {
            SourcePath = "failed",
            ModelUsed = string.Empty,
            Attempts = 0,
            FallbackUsed = false,
            ValidationFailures = 0,
            GuardrailFailures = 0,
            Optimization = optimizationMeta
        };

        _telemetry.LlmOptimizationTierTotal.Add(1, new KeyValuePair<string, object?>("tier", TierToText(optimizationPlan.TierUsed)));
        _telemetry.LlmModelRoutedTotal.Add(
            1,
            new KeyValuePair<string, object?>("model", optimizationPlan.ModelChosen),
            new KeyValuePair<string, object?>("band", optimizationPlan.ComplexityBand));
        _telemetry.LlmPromptChars.Record(optimizationPlan.PromptEstimatedChars);
        var ratio = optimizationPlan.OriginalEvidenceChars <= 0
            ? 1d
            : (double)optimizationPlan.CompactedEvidenceChars / optimizationPlan.OriginalEvidenceChars;
        _telemetry.LlmEvidenceCompactionRatio.Record(ratio);

        if (!force)
        {
            var cachedSame = await GetCachedSuccessfulRunForInputAsync(sessionId, inputHash, null, cancellationToken);
            if (cachedSame != null)
            {
                metadata.SourcePath = "cache_same_input";
                metadata.ModelUsed = cachedSame.Model;

                _telemetry.LlmCallsTotal.Add(1, new KeyValuePair<string, object?>("result", "cached"));
                _logger.LogInformation(
                    "LLM orchestration summary: sessionId={sessionId}, sourcePath={sourcePath}, modelUsed={modelUsed}, attempts={attempts}",
                    sessionId,
                    metadata.SourcePath,
                    metadata.ModelUsed,
                    metadata.Attempts);

                return LlmCoachingOrchestrationResult.CreateSuccess(cachedSame.Response, metadata);
            }
        }

        var allAttemptErrors = new List<string>();
        var attemptNo = 0;
        var guardrailRetried = false;

        var optimizedSummary = optimizationPlan.CompactedSummary;
        var primaryModel = string.IsNullOrWhiteSpace(optimizationPlan.ModelChosen)
            ? _llmOptions.Model
            : optimizationPlan.ModelChosen;
        var maxPrimaryAttempts = Math.Max(1, _llmOptions.Retry.MaxAttemptsPrimary);

        for (var i = 0; i < maxPrimaryAttempts; i++)
        {
            attemptNo++;
            var attempt = await TryModelAttemptAsync(
                optimizedSummary,
                primaryModel,
                sessionId,
                attemptNo,
                metadata,
                cancellationToken);

            if (attempt.Success)
            {
                metadata.SourcePath = "primary";
                metadata.ModelUsed = primaryModel;
                metadata.Attempts = attemptNo;
                metadata.ErrorSummary = null;

                var persisted = await PersistIfNeededAsync(
                    sessionId,
                    inputHash,
                    promptVersion,
                    primaryModel,
                    summary,
                    force,
                    attempt.SanitizedResponse!,
                    attempt.GuardrailsMetadata!,
                    optimizationMeta,
                    metadata,
                    cancellationToken);

                if (!persisted.Success)
                {
                    return persisted;
                }

                _telemetry.LlmCallsTotal.Add(1, new KeyValuePair<string, object?>("result", "success"));

                _logger.LogInformation(
                    "LLM orchestration summary: sessionId={sessionId}, sourcePath={sourcePath}, modelUsed={modelUsed}, attempts={attempts}, complexityBand={complexityBand}, tierUsed={tierUsed}, promptChars={promptChars}",
                    sessionId,
                    metadata.SourcePath,
                    metadata.ModelUsed,
                    metadata.Attempts,
                    optimizationMeta.ComplexityBand,
                    optimizationMeta.TierUsed,
                    optimizationMeta.PromptEstimatedChars);

                return LlmCoachingOrchestrationResult.CreateSuccess(attempt.SanitizedResponse!, metadata);
            }

            if (!string.IsNullOrWhiteSpace(attempt.ErrorMessage))
            {
                allAttemptErrors.Add(attempt.ErrorMessage);
            }

            if (attempt.FailureType == LlmAttemptFailureType.InvalidJson)
            {
                metadata.ValidationFailures++;
                _telemetry.LlmCallsTotal.Add(1, new KeyValuePair<string, object?>("result", "invalid_json"));
            }
            else if (attempt.FailureType == LlmAttemptFailureType.GuardrailRejected)
            {
                metadata.GuardrailFailures++;
                _telemetry.LlmCallsTotal.Add(1, new KeyValuePair<string, object?>("result", "guardrail_rejected"));
            }
            else if (attempt.FailureType == LlmAttemptFailureType.Timeout)
            {
                _telemetry.LlmCallsTotal.Add(1, new KeyValuePair<string, object?>("result", "timeout"));
            }
            else if (attempt.FailureType == LlmAttemptFailureType.HttpError)
            {
                _telemetry.LlmCallsTotal.Add(1, new KeyValuePair<string, object?>("result", "http_error"));
            }

            var allowGuardrailRetry = _llmOptions.Retry.RetryOnInvalidJson && !guardrailRetried && attempt.FailureType == LlmAttemptFailureType.GuardrailRejected;
            if (allowGuardrailRetry)
            {
                guardrailRetried = true;
            }

            var shouldRetry = ShouldRetryPrimary(attempt.FailureType) || allowGuardrailRetry;
            if (!shouldRetry || i == maxPrimaryAttempts - 1)
            {
                break;
            }

            var backoff = ResolveBackoff(i);
            if (backoff > 0)
            {
                await Task.Delay(backoff, cancellationToken);
            }
        }

        if (_llmOptions.Fallback.Enabled && _llmOptions.Fallback.TryFallbackModelsOnFailure)
        {
            foreach (var fallbackModel in _llmOptions.FallbackModels.Where(m => !string.IsNullOrWhiteSpace(m)))
            {
                attemptNo++;
                var modelName = fallbackModel.Trim();
                var attempt = await TryModelAttemptAsync(
                    optimizedSummary,
                    modelName,
                    sessionId,
                    attemptNo,
                    metadata,
                    cancellationToken);

                if (attempt.Success)
                {
                    metadata.SourcePath = "fallback_model";
                    metadata.ModelUsed = modelName;
                    metadata.Attempts = attemptNo;
                    metadata.FallbackUsed = true;
                    metadata.ErrorSummary = null;

                    var persisted = await PersistIfNeededAsync(
                        sessionId,
                        inputHash,
                        promptVersion,
                        modelName,
                        summary,
                        force,
                        attempt.SanitizedResponse!,
                        attempt.GuardrailsMetadata!,
                        optimizationMeta,
                        metadata,
                        cancellationToken);

                    if (!persisted.Success)
                    {
                        return persisted;
                    }

                    _telemetry.LlmCallsTotal.Add(1, new KeyValuePair<string, object?>("result", "fallback_success"));
                    _telemetry.LlmFallbackUsedTotal.Add(1, new KeyValuePair<string, object?>("model", modelName));

                    _logger.LogWarning(
                        "LLM fallback model used: sessionId={sessionId}, model={model}, attempts={attempts}",
                        sessionId,
                        modelName,
                        metadata.Attempts);

                    return LlmCoachingOrchestrationResult.CreateSuccess(attempt.SanitizedResponse!, metadata);
                }

                if (!string.IsNullOrWhiteSpace(attempt.ErrorMessage))
                {
                    allAttemptErrors.Add(attempt.ErrorMessage);
                }

                if (attempt.FailureType == LlmAttemptFailureType.InvalidJson)
                {
                    metadata.ValidationFailures++;
                }
                else if (attempt.FailureType == LlmAttemptFailureType.GuardrailRejected)
                {
                    metadata.GuardrailFailures++;
                }
            }
        }

        var maxAge = TimeSpan.FromHours(Math.Max(1, _llmOptions.Fallback.CacheFallbackMaxAgeHours));
        if (_llmOptions.Fallback.UseCachedSameInputHashIfAllFail)
        {
            var sameInputCached = await GetCachedSuccessfulRunForInputAsync(sessionId, inputHash, maxAge, cancellationToken);
            if (sameInputCached != null)
            {
                metadata.SourcePath = "cache_same_input";
                metadata.ModelUsed = sameInputCached.Model;
                metadata.FallbackUsed = true;
                metadata.Attempts = attemptNo;
                metadata.ErrorSummary = BuildErrorSummary(allAttemptErrors);

                _telemetry.LlmCallsTotal.Add(1, new KeyValuePair<string, object?>("result", "cached"));
                _telemetry.LlmCacheFallbackTotal.Add(1, new KeyValuePair<string, object?>("type", "same_input"));

                _logger.LogWarning(
                    "LLM cache fallback used (same input): sessionId={sessionId}, attempts={attempts}",
                    sessionId,
                    metadata.Attempts);

                return LlmCoachingOrchestrationResult.CreateSuccess(sameInputCached.Response, metadata);
            }
        }

        if (_llmOptions.Fallback.UseCachedAnyPreviousForSessionIfSameInputMissing)
        {
            var anySessionCached = await GetCachedSuccessfulRunForSessionAsync(sessionId, maxAge, cancellationToken);
            if (anySessionCached != null)
            {
                metadata.SourcePath = "cache_previous_session";
                metadata.ModelUsed = anySessionCached.Model;
                metadata.FallbackUsed = true;
                metadata.Attempts = attemptNo;
                metadata.ErrorSummary = BuildErrorSummary(allAttemptErrors);

                _telemetry.LlmCallsTotal.Add(1, new KeyValuePair<string, object?>("result", "cached"));
                _telemetry.LlmCacheFallbackTotal.Add(1, new KeyValuePair<string, object?>("type", "previous_session"));

                _logger.LogWarning(
                    "LLM cache fallback used (previous session run): sessionId={sessionId}, attempts={attempts}",
                    sessionId,
                    metadata.Attempts);

                return LlmCoachingOrchestrationResult.CreateSuccess(anySessionCached.Response, metadata);
            }
        }

        metadata.SourcePath = "failed";
        metadata.Attempts = attemptNo;
        metadata.ErrorSummary = BuildErrorSummary(allAttemptErrors);

        _telemetry.LlmCallsTotal.Add(1, new KeyValuePair<string, object?>("result", "failed"));
        _logger.LogError(
            "LLM orchestration failed: sessionId={sessionId}, attempts={attempts}, errorSummary={errorSummary}",
            sessionId,
            metadata.Attempts,
            metadata.ErrorSummary);

        return LlmCoachingOrchestrationResult.CreateFailed(metadata, metadata.ErrorSummary ?? "All model and cache fallback paths failed.");
    }

    private async Task<LlmAttemptOutcome> TryModelAttemptAsync(
        EvidenceSummaryDto summary,
        string model,
        Guid sessionId,
        int attemptNo,
        LlmCoachingOrchestrationMetadata metadata,
        CancellationToken cancellationToken)
    {
        metadata.AttemptedModels.Add(model);

        var sw = Stopwatch.StartNew();
        try
        {
            var result = await _llmCoachingService.GenerateWithModelAsync(summary, model, cancellationToken);
            sw.Stop();
            _telemetry.LlmAttemptLatencyMs.Record(
                sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("model", model),
                new KeyValuePair<string, object?>("attempt", attemptNo));

            if (!result.IsValid || result.Response == null)
            {
                _logger.LogWarning(
                    "LLM attempt invalid JSON/schema: sessionId={sessionId}, model={model}, attempt={attempt}",
                    sessionId,
                    model,
                    attemptNo);

                return LlmAttemptOutcome.CreateFailed(
                    LlmAttemptFailureType.InvalidJson,
                    $"model={model},attempt={attemptNo},failure=invalid_json");
            }

            var guardrails = _guardrailsService.Apply(result.Response);
            if (!guardrails.Metadata.Passed)
            {
                _logger.LogWarning(
                    "LLM attempt guardrail rejected: sessionId={sessionId}, model={model}, attempt={attempt}, violations={violations}",
                    sessionId,
                    model,
                    attemptNo,
                    guardrails.Metadata.Violations.Count);

                return LlmAttemptOutcome.CreateFailed(
                    LlmAttemptFailureType.GuardrailRejected,
                    $"model={model},attempt={attemptNo},failure=guardrail_rejected:{string.Join("|", guardrails.Metadata.Violations)}");
            }

            return LlmAttemptOutcome.CreateSucceeded(guardrails.Response, guardrails.Metadata);
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            sw.Stop();
            _telemetry.LlmAttemptLatencyMs.Record(
                sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("model", model),
                new KeyValuePair<string, object?>("attempt", attemptNo));

            _logger.LogWarning(
                ex,
                "LLM attempt timeout: sessionId={sessionId}, model={model}, attempt={attempt}",
                sessionId,
                model,
                attemptNo);

            return LlmAttemptOutcome.CreateFailed(
                LlmAttemptFailureType.Timeout,
                $"model={model},attempt={attemptNo},failure=timeout");
        }
        catch (HttpRequestException ex)
        {
            sw.Stop();
            _telemetry.LlmAttemptLatencyMs.Record(
                sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("model", model),
                new KeyValuePair<string, object?>("attempt", attemptNo));

            var failureType = IsHttp5xx(ex.Message) ? LlmAttemptFailureType.HttpError : LlmAttemptFailureType.OtherError;

            _logger.LogWarning(
                ex,
                "LLM attempt http error: sessionId={sessionId}, model={model}, attempt={attempt}",
                sessionId,
                model,
                attemptNo);

            return LlmAttemptOutcome.CreateFailed(
                failureType,
                $"model={model},attempt={attemptNo},failure=http_error");
        }
        catch (Exception ex)
        {
            sw.Stop();
            _telemetry.LlmAttemptLatencyMs.Record(
                sw.Elapsed.TotalMilliseconds,
                new KeyValuePair<string, object?>("model", model),
                new KeyValuePair<string, object?>("attempt", attemptNo));

            _logger.LogWarning(
                ex,
                "LLM attempt error: sessionId={sessionId}, model={model}, attempt={attempt}",
                sessionId,
                model,
                attemptNo);

            return LlmAttemptOutcome.CreateFailed(
                LlmAttemptFailureType.OtherError,
                $"model={model},attempt={attemptNo},failure=error:{ex.Message}");
        }
    }
    private async Task<LlmCoachingOrchestrationResult> PersistIfNeededAsync(
        Guid sessionId,
        string inputHash,
        string promptVersion,
        string model,
        EvidenceSummaryDto summary,
        bool force,
        LlmCoachingResponse response,
        LlmGuardrailsMetadata guardrails,
        LlmOptimizationMetadata optimization,
        LlmCoachingOrchestrationMetadata orchestration,
        CancellationToken cancellationToken)
    {
        var normalizedOutput = JsonSerializer.Serialize(response);
        var existingForInput = await _db.LlmRuns
            .AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.Kind == CoachKind && r.InputHash == inputHash)
            .OrderByDescending(r => r.CreatedAt)
            .FirstOrDefaultAsync(cancellationToken);

        if (existingForInput != null)
        {
            var existingComparable = RemoveMeta(existingForInput.OutputJson);
            if (string.Equals(existingComparable, normalizedOutput, StringComparison.Ordinal))
            {
                orchestration.SourcePath = "cache_same_input";
                return LlmCoachingOrchestrationResult.CreateSuccess(response, orchestration);
            }

            if (!force)
            {
                var parsed = TryParseRun(existingForInput);
                if (parsed != null)
                {
                    orchestration.SourcePath = "cache_same_input";
                    return LlmCoachingOrchestrationResult.CreateSuccess(parsed.Response, orchestration);
                }
            }
            else
            {
                var prior = await _db.LlmRuns.FirstAsync(r => r.Id == existingForInput.Id, cancellationToken);
                _db.LlmRuns.Remove(prior);
                await _db.SaveChangesAsync(cancellationToken);
            }
        }

        var llmRunId = Guid.NewGuid();
        var createdAt = DateTime.UtcNow;

        var payloadWithMeta = MergeOutputWithMeta(
            normalizedOutput,
            promptVersion,
            model,
            inputHash,
            llmRunId,
            createdAt,
            guardrails,
            optimization,
            orchestration);

        var run = new LlmRun
        {
            Id = llmRunId,
            SessionId = sessionId,
            Kind = CoachKind,
            PromptVersion = promptVersion,
            Model = model,
            InputHash = inputHash,
            OutputJson = payloadWithMeta,
            CreatedAt = createdAt
        };

        _db.LlmRuns.Add(run);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            var raced = await _db.LlmRuns
                .AsNoTracking()
                .Where(r => r.SessionId == sessionId && r.Kind == CoachKind && r.InputHash == inputHash)
                .OrderByDescending(r => r.CreatedAt)
                .FirstOrDefaultAsync(cancellationToken);

            var racedParsed = raced == null ? null : TryParseRun(raced);
            if (racedParsed != null)
            {
                orchestration.SourcePath = "cache_same_input";
                return LlmCoachingOrchestrationResult.CreateSuccess(racedParsed.Response, orchestration);
            }

            return LlmCoachingOrchestrationResult.CreateFailed(orchestration, "Failed to persist LLM output due to race condition.");
        }

        var metricEvent = new MetricEvent
        {
            Id = Guid.NewGuid(),
            SessionId = sessionId,
            ClientEventId = Guid.NewGuid(),
            TsMs = Math.Max(0, summary.HighLevel.DurationMs),
            Source = "LLM",
            Type = "llm_coaching_v1",
            PayloadJson = payloadWithMeta,
            CreatedAt = DateTime.UtcNow
        };

        _db.MetricEvents.Add(metricEvent);
        await _db.SaveChangesAsync(cancellationToken);

        return LlmCoachingOrchestrationResult.CreateSuccess(response, orchestration);
    }

    private async Task<CachedRunParseResult?> GetCachedSuccessfulRunForInputAsync(
        Guid sessionId,
        string inputHash,
        TimeSpan? maxAge,
        CancellationToken cancellationToken)
    {
        var query = _db.LlmRuns
            .AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.Kind == CoachKind && r.InputHash == inputHash)
            .OrderByDescending(r => r.CreatedAt)
            .AsQueryable();

        if (maxAge.HasValue)
        {
            var threshold = DateTime.UtcNow.Subtract(maxAge.Value);
            query = query.Where(r => r.CreatedAt >= threshold);
        }

        var runs = await query.Take(10).ToListAsync(cancellationToken);
        foreach (var run in runs)
        {
            var parsed = TryParseRun(run);
            if (parsed != null)
            {
                return parsed;
            }
        }

        return null;
    }

    private async Task<CachedRunParseResult?> GetCachedSuccessfulRunForSessionAsync(
        Guid sessionId,
        TimeSpan maxAge,
        CancellationToken cancellationToken)
    {
        var threshold = DateTime.UtcNow.Subtract(maxAge);
        var runs = await _db.LlmRuns
            .AsNoTracking()
            .Where(r => r.SessionId == sessionId && r.Kind == CoachKind && r.CreatedAt >= threshold)
            .OrderByDescending(r => r.CreatedAt)
            .Take(10)
            .ToListAsync(cancellationToken);

        foreach (var run in runs)
        {
            var parsed = TryParseRun(run);
            if (parsed != null)
            {
                return parsed;
            }
        }

        return null;
    }

    private CachedRunParseResult? TryParseRun(LlmRun run)
    {
        if (!_llmCoachingService.TryParseAndValidate(run.OutputJson, out var cached, out _))
        {
            return null;
        }

        var guardrails = _guardrailsService.Apply(cached!);
        if (!guardrails.Metadata.Passed)
        {
            return null;
        }

        return new CachedRunParseResult(guardrails.Response, run.Model, run.CreatedAt);
    }

    private bool ShouldRetryPrimary(LlmAttemptFailureType failureType)
    {
        return failureType switch
        {
            LlmAttemptFailureType.Timeout => _llmOptions.Retry.RetryOnTimeout,
            LlmAttemptFailureType.HttpError => _llmOptions.Retry.RetryOnHttp5xx,
            LlmAttemptFailureType.InvalidJson => _llmOptions.Retry.RetryOnInvalidJson,
            _ => false
        };
    }

    private int ResolveBackoff(int attemptIndex)
    {
        if (_llmOptions.Retry.BackoffMs == null || _llmOptions.Retry.BackoffMs.Count == 0)
        {
            return 0;
        }

        if (attemptIndex < _llmOptions.Retry.BackoffMs.Count)
        {
            return Math.Max(0, _llmOptions.Retry.BackoffMs[attemptIndex]);
        }

        return Math.Max(0, _llmOptions.Retry.BackoffMs[^1]);
    }
    private static bool IsHttp5xx(string message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return false;
        }

        return message.Contains("(50", StringComparison.Ordinal) ||
               message.Contains("(51", StringComparison.Ordinal) ||
               message.Contains("(52", StringComparison.Ordinal) ||
               message.Contains("(53", StringComparison.Ordinal) ||
               message.Contains("(54", StringComparison.Ordinal) ||
               message.Contains("(55", StringComparison.Ordinal);
    }

    private static string BuildErrorSummary(List<string> errors)
    {
        if (errors.Count == 0)
        {
            return "LLM orchestration failed without a specific error.";
        }

        var limited = errors.Take(5);
        return string.Join("; ", limited);
    }

    private static string ComputeSha256Hex(string input)
    {
        var bytes = Encoding.UTF8.GetBytes(input);
        var hash = SHA256.HashData(bytes);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string CanonicalizeJson(string json)
    {
        using var doc = JsonDocument.Parse(json);
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            WriteCanonicalElement(writer, doc.RootElement);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteCanonicalElement(Utf8JsonWriter writer, JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach (var property in element.EnumerateObject().OrderBy(p => p.Name, StringComparer.Ordinal))
                {
                    writer.WritePropertyName(property.Name);
                    WriteCanonicalElement(writer, property.Value);
                }
                writer.WriteEndObject();
                break;
            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach (var item in element.EnumerateArray())
                {
                    WriteCanonicalElement(writer, item);
                }
                writer.WriteEndArray();
                break;
            case JsonValueKind.String:
                writer.WriteStringValue(element.GetString());
                break;
            case JsonValueKind.Number:
                writer.WriteRawValue(element.GetRawText(), skipInputValidation: true);
                break;
            case JsonValueKind.True:
                writer.WriteBooleanValue(true);
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue(false);
                break;
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                writer.WriteNullValue();
                break;
        }
    }

    private static string RemoveMeta(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return json;
        }

        if (!doc.RootElement.TryGetProperty("_meta", out _))
        {
            return json;
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            foreach (var property in doc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "_meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string MergeOutputWithMeta(
        string outputJson,
        string promptVersion,
        string model,
        string inputHash,
        Guid llmRunId,
        DateTime createdAtUtc,
        LlmGuardrailsMetadata guardrails,
        LlmOptimizationMetadata optimization,
        LlmCoachingOrchestrationMetadata orchestration)
    {
        using var outputDoc = JsonDocument.Parse(outputJson);
        if (outputDoc.RootElement.ValueKind != JsonValueKind.Object)
        {
            return outputJson;
        }

        Dictionary<string, JsonElement> existingMetaProps = new(StringComparer.Ordinal);
        if (outputDoc.RootElement.TryGetProperty("_meta", out var existingMeta) &&
            existingMeta.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in existingMeta.EnumerateObject())
            {
                existingMetaProps[prop.Name] = prop.Value;
            }
        }

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("_meta");
            writer.WriteStartObject();
            foreach (var prop in existingMetaProps)
            {
                if (prop.Key.Equals("guardrails", StringComparison.OrdinalIgnoreCase) ||
                    prop.Key.Equals("optimization", StringComparison.OrdinalIgnoreCase) ||
                    prop.Key.Equals("orchestration", StringComparison.OrdinalIgnoreCase) ||
                    prop.Key.Equals("promptVersion", StringComparison.OrdinalIgnoreCase) ||
                    prop.Key.Equals("model", StringComparison.OrdinalIgnoreCase) ||
                    prop.Key.Equals("inputHash", StringComparison.OrdinalIgnoreCase) ||
                    prop.Key.Equals("llmRunId", StringComparison.OrdinalIgnoreCase) ||
                    prop.Key.Equals("createdAtUtc", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                writer.WritePropertyName(prop.Key);
                prop.Value.WriteTo(writer);
            }
            writer.WriteString("promptVersion", promptVersion);
            writer.WriteString("model", model);
            writer.WriteString("inputHash", inputHash);
            writer.WriteString("llmRunId", llmRunId);
            writer.WriteString("createdAtUtc", createdAtUtc);

            writer.WritePropertyName("guardrails");
            writer.WriteStartObject();
            writer.WriteBoolean("passed", guardrails.Passed);
            writer.WriteNumber("qualityScore", guardrails.QualityScore);
            writer.WriteStartArray("warnings");
            foreach (var warning in guardrails.Warnings)
            {
                writer.WriteStringValue(warning);
            }
            writer.WriteEndArray();
            writer.WriteStartArray("sanitizationsApplied");
            foreach (var item in guardrails.SanitizationsApplied)
            {
                writer.WriteStringValue(item);
            }
            writer.WriteEndArray();
            writer.WriteEndObject();

            writer.WritePropertyName("orchestration");
            writer.WriteStartObject();
            writer.WriteString("sourcePath", orchestration.SourcePath);
            writer.WriteNumber("attempts", orchestration.Attempts);
            writer.WriteBoolean("fallbackUsed", orchestration.FallbackUsed);
            writer.WriteStartArray("attemptedModels");
            foreach (var attempted in orchestration.AttemptedModels)
            {
                writer.WriteStringValue(attempted);
            }
            writer.WriteEndArray();
            writer.WriteNumber("validationFailures", orchestration.ValidationFailures);
            writer.WriteNumber("guardrailFailures", orchestration.GuardrailFailures);
            writer.WriteEndObject();

            writer.WritePropertyName("optimization");
            writer.WriteStartObject();
            writer.WriteBoolean("enabled", optimization.Enabled);
            writer.WriteNumber("complexityScore", optimization.ComplexityScore);
            writer.WriteString("complexityBand", optimization.ComplexityBand);
            writer.WriteString("tierRequested", optimization.TierRequested);
            writer.WriteString("tierUsed", optimization.TierUsed);
            writer.WriteNumber("originalEvidenceChars", optimization.OriginalEvidenceChars);
            writer.WriteNumber("compactedEvidenceChars", optimization.CompactedEvidenceChars);
            writer.WriteNumber("promptEstimatedChars", optimization.PromptEstimatedChars);
            writer.WriteNumber("promptBudgetChars", optimization.PromptBudgetChars);
            writer.WriteString("modelRoutedFromBand", optimization.ModelRoutedFromBand);
            writer.WriteString("modelChosen", optimization.ModelChosen);
            writer.WriteBoolean("truncationApplied", optimization.TruncationApplied);
            writer.WriteStartArray("warnings");
            foreach (var warning in optimization.Warnings)
            {
                writer.WriteStringValue(warning);
            }
            writer.WriteEndArray();
            writer.WritePropertyName("dropped");
            writer.WriteStartObject();
            writer.WriteNumber("transcriptSlices", optimization.Dropped.TranscriptSlicesDropped);
            writer.WriteNumber("patterns", optimization.Dropped.PatternsDropped);
            writer.WriteNumber("worstWindows", optimization.Dropped.WorstWindowsDropped);
            writer.WriteEndObject();
            writer.WriteEndObject();

            writer.WriteEndObject();

            foreach (var property in outputDoc.RootElement.EnumerateObject())
            {
                if (string.Equals(property.Name, "_meta", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                writer.WritePropertyName(property.Name);
                property.Value.WriteTo(writer);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static LlmOptimizationMetadata ToOptimizationMetadata(LlmOptimizationPlan plan)
    {
        return new LlmOptimizationMetadata
        {
            Enabled = plan.Enabled,
            ComplexityScore = plan.ComplexityScore,
            ComplexityBand = plan.ComplexityBand,
            TierRequested = TierToText(plan.TierRequested),
            TierUsed = TierToText(plan.TierUsed),
            OriginalEvidenceChars = plan.OriginalEvidenceChars,
            CompactedEvidenceChars = plan.CompactedEvidenceChars,
            PromptEstimatedChars = plan.PromptEstimatedChars,
            PromptBudgetChars = plan.PromptBudgetChars,
            ModelRoutedFromBand = plan.ModelRoutedFromBand,
            ModelChosen = plan.ModelChosen,
            TruncationApplied = plan.TruncationApplied,
            Warnings = plan.Warnings.ToList(),
            Dropped = new LlmOptimizationDroppedCounts
            {
                TranscriptSlicesDropped = plan.Dropped.TranscriptSlicesDropped,
                PatternsDropped = plan.Dropped.PatternsDropped,
                WorstWindowsDropped = plan.Dropped.WorstWindowsDropped
            }
        };
    }

    private static string TierToText(LlmEvidenceTier tier)
    {
        return tier switch
        {
            LlmEvidenceTier.Small => "small",
            LlmEvidenceTier.Full => "full",
            _ => "medium"
        };
    }
}

public sealed class LlmCoachingOrchestrationResult
{
    private LlmCoachingOrchestrationResult() { }

    public bool Success { get; private set; }
    public bool NotFound { get; private set; }
    public string? ErrorMessage { get; private set; }
    public LlmCoachingResponse? Response { get; private set; }
    public LlmCoachingOrchestrationMetadata Metadata { get; private set; } = new();

    public static LlmCoachingOrchestrationResult CreateNotFound()
    {
        return new LlmCoachingOrchestrationResult
        {
            NotFound = true,
            Success = false,
            Metadata = new LlmCoachingOrchestrationMetadata { SourcePath = "failed" }
        };
    }

    public static LlmCoachingOrchestrationResult CreateSuccess(LlmCoachingResponse response, LlmCoachingOrchestrationMetadata metadata)
    {
        return new LlmCoachingOrchestrationResult
        {
            Success = true,
            Response = response,
            Metadata = metadata
        };
    }

    public static LlmCoachingOrchestrationResult CreateFailed(LlmCoachingOrchestrationMetadata metadata, string error)
    {
        return new LlmCoachingOrchestrationResult
        {
            Success = false,
            ErrorMessage = error,
            Metadata = metadata
        };
    }
}

public sealed class LlmCoachingOrchestrationMetadata
{
    public string SourcePath { get; set; } = "failed";
    public string ModelUsed { get; set; } = string.Empty;
    public int Attempts { get; set; }
    public bool FallbackUsed { get; set; }
    public int ValidationFailures { get; set; }
    public int GuardrailFailures { get; set; }
    public string? ErrorSummary { get; set; }
    public List<string> AttemptedModels { get; set; } = [];
    public LlmOptimizationMetadata Optimization { get; set; } = new();
}

internal enum LlmAttemptFailureType
{
    None,
    Timeout,
    HttpError,
    InvalidJson,
    GuardrailRejected,
    OtherError
}

internal sealed class LlmAttemptOutcome
{
    private LlmAttemptOutcome() { }

    public bool Success { get; private set; }
    public LlmCoachingResponse? SanitizedResponse { get; private set; }
    public LlmGuardrailsMetadata? GuardrailsMetadata { get; private set; }
    public LlmAttemptFailureType FailureType { get; private set; }
    public string? ErrorMessage { get; private set; }

    public static LlmAttemptOutcome CreateSucceeded(LlmCoachingResponse response, LlmGuardrailsMetadata guardrails)
    {
        return new LlmAttemptOutcome
        {
            Success = true,
            SanitizedResponse = response,
            GuardrailsMetadata = guardrails,
            FailureType = LlmAttemptFailureType.None
        };
    }

    public static LlmAttemptOutcome CreateFailed(LlmAttemptFailureType failureType, string error)
    {
        return new LlmAttemptOutcome
        {
            Success = false,
            FailureType = failureType,
            ErrorMessage = error
        };
    }
}

internal sealed record CachedRunParseResult(LlmCoachingResponse Response, string Model, DateTime CreatedAtUtc);
