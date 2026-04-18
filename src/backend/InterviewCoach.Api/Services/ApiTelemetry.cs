using System.Diagnostics;
using System.Diagnostics.Metrics;

namespace InterviewCoach.Api.Services;

public sealed class ApiTelemetry : IDisposable
{
    public const string ActivitySourceName = "InterviewCoach";
    public const string MeterName = "InterviewCoach";

    private readonly Meter _meter;

    public ApiTelemetry()
    {
        ActivitySource = new ActivitySource(ActivitySourceName);
        _meter = new Meter(MeterName);

        EventsInsertedTotal = _meter.CreateCounter<long>("interviewcoach_events_inserted_total");
        EventsDuplicatesTotal = _meter.CreateCounter<long>("interviewcoach_events_duplicates_total");
        TranscriptInsertedTotal = _meter.CreateCounter<long>("interviewcoach_transcript_inserted_total");
        TranscriptDuplicatesTotal = _meter.CreateCounter<long>("interviewcoach_transcript_duplicates_total");
        FinalizeRunsTotal = _meter.CreateCounter<long>("interviewcoach_finalize_runs_total");
        LlmCallsTotal = _meter.CreateCounter<long>("interviewcoach_llm_calls_total");
        LlmFallbackUsedTotal = _meter.CreateCounter<long>("interviewcoach_llm_fallback_used_total");
        LlmCacheFallbackTotal = _meter.CreateCounter<long>("interviewcoach_llm_cache_fallback_total");
        LlmOptimizationTierTotal = _meter.CreateCounter<long>("interviewcoach_llm_optimization_tier_total");
        LlmModelRoutedTotal = _meter.CreateCounter<long>("interviewcoach_llm_model_routed_total");

        HttpRequestDurationMs = _meter.CreateHistogram<double>("interviewcoach_http_request_duration_ms", unit: "ms");
        LlmLatencyMs = _meter.CreateHistogram<double>("interviewcoach_llm_latency_ms", unit: "ms");
        LlmAttemptLatencyMs = _meter.CreateHistogram<double>("interviewcoach_llm_attempt_latency_ms", unit: "ms");
        LlmPromptChars = _meter.CreateHistogram<long>("interviewcoach_llm_prompt_chars");
        LlmEvidenceCompactionRatio = _meter.CreateHistogram<double>("interviewcoach_llm_evidence_compaction_ratio");
        LlmBatchJobsTotal = _meter.CreateCounter<long>("interviewcoach_llm_batch_jobs_total");
        LlmBatchItemsTotal = _meter.CreateCounter<long>("interviewcoach_llm_batch_items_total");
        LlmBatchJobDurationMs = _meter.CreateHistogram<double>("interviewcoach_llm_batch_job_duration_ms", unit: "ms");
        LlmBatchItemDurationMs = _meter.CreateHistogram<double>("interviewcoach_llm_batch_item_duration_ms", unit: "ms");
        FinalizeDurationMs = _meter.CreateHistogram<double>("interviewcoach_finalize_duration_ms", unit: "ms");
        EventPayloadBytes = _meter.CreateHistogram<long>("interviewcoach_event_payload_bytes", unit: "By");
    }

    public ActivitySource ActivitySource { get; }

    public Counter<long> EventsInsertedTotal { get; }
    public Counter<long> EventsDuplicatesTotal { get; }
    public Counter<long> TranscriptInsertedTotal { get; }
    public Counter<long> TranscriptDuplicatesTotal { get; }
    public Counter<long> FinalizeRunsTotal { get; }
    public Counter<long> LlmCallsTotal { get; }
    public Counter<long> LlmFallbackUsedTotal { get; }
    public Counter<long> LlmCacheFallbackTotal { get; }
    public Counter<long> LlmOptimizationTierTotal { get; }
    public Counter<long> LlmModelRoutedTotal { get; }
    public Counter<long> LlmBatchJobsTotal { get; }
    public Counter<long> LlmBatchItemsTotal { get; }

    public Histogram<double> HttpRequestDurationMs { get; }
    public Histogram<double> LlmLatencyMs { get; }
    public Histogram<double> LlmAttemptLatencyMs { get; }
    public Histogram<long> LlmPromptChars { get; }
    public Histogram<double> LlmEvidenceCompactionRatio { get; }
    public Histogram<double> LlmBatchJobDurationMs { get; }
    public Histogram<double> LlmBatchItemDurationMs { get; }
    public Histogram<double> FinalizeDurationMs { get; }
    public Histogram<long> EventPayloadBytes { get; }

    public void Dispose()
    {
        ActivitySource.Dispose();
        _meter.Dispose();
    }
}
