using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public class BatchCoachingOptions
{
    public bool Enabled { get; set; } = true;
    public int PollIntervalSeconds { get; set; } = 5;
    public int MaxParallelism { get; set; } = 2;
    public int MaxSessionsPerJob { get; set; } = 500;
    public bool ResetStaleRunningJobsOnStartup { get; set; } = true;
    public int StaleRunningJobMinutes { get; set; } = 30;
    public int ItemRetryCount { get; set; }
}

public sealed class BatchCoachingOptionsValidator : IValidateOptions<BatchCoachingOptions>
{
    public ValidateOptionsResult Validate(string? name, BatchCoachingOptions options)
    {
        if (options.PollIntervalSeconds < 1 || options.PollIntervalSeconds > 300)
            return ValidateOptionsResult.Fail("BatchCoaching:PollIntervalSeconds must be in [1,300].");

        if (options.MaxParallelism < 1 || options.MaxParallelism > 4)
            return ValidateOptionsResult.Fail("BatchCoaching:MaxParallelism must be in [1,4].");

        if (options.MaxSessionsPerJob < 1 || options.MaxSessionsPerJob > 1000)
            return ValidateOptionsResult.Fail("BatchCoaching:MaxSessionsPerJob must be in [1,1000].");

        if (options.StaleRunningJobMinutes < 1 || options.StaleRunningJobMinutes > 1440)
            return ValidateOptionsResult.Fail("BatchCoaching:StaleRunningJobMinutes must be in [1,1440].");

        if (options.ItemRetryCount < 0 || options.ItemRetryCount > 3)
            return ValidateOptionsResult.Fail("BatchCoaching:ItemRetryCount must be in [0,3].");

        return ValidateOptionsResult.Success;
    }
}