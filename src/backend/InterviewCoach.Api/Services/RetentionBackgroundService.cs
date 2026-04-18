using Microsoft.Extensions.Options;

namespace InterviewCoach.Api.Services;

public class RetentionBackgroundService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IOptionsMonitor<RetentionOptions> _optionsMonitor;
    private readonly ILogger<RetentionBackgroundService> _logger;

    public RetentionBackgroundService(
        IServiceScopeFactory scopeFactory,
        IOptionsMonitor<RetentionOptions> optionsMonitor,
        ILogger<RetentionBackgroundService> logger)
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
                var options = _optionsMonitor.CurrentValue;
                var runHour = Math.Clamp(options.RunHourUtc, 0, 23);
                var delay = GetDelayUntilNextRun(runHour, DateTime.UtcNow);

                await Task.Delay(delay, stoppingToken);

                if (stoppingToken.IsCancellationRequested)
                    break;

                using var scope = _scopeFactory.CreateScope();
                var cleanupService = scope.ServiceProvider.GetRequiredService<IRetentionCleanupService>();
                await cleanupService.RunOnceAsync(respectEnabled: true, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Retention background job failed");
                await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
            }
        }
    }

    private static TimeSpan GetDelayUntilNextRun(int runHourUtc, DateTime nowUtc)
    {
        var todayRun = new DateTime(nowUtc.Year, nowUtc.Month, nowUtc.Day, runHourUtc, 0, 0, DateTimeKind.Utc);
        var nextRun = nowUtc < todayRun ? todayRun : todayRun.AddDays(1);
        return nextRun - nowUtc;
    }
}