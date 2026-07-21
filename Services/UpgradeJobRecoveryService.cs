namespace BoldBI.UpgradeCenter.Services;

public sealed class UpgradeJobRecoveryService : BackgroundService
{
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(10);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly ILogger<UpgradeJobRecoveryService> logger;

    public UpgradeJobRecoveryService(
        IServiceScopeFactory scopeFactory,
        ILogger<UpgradeJobRecoveryService> logger)
    {
        this.scopeFactory = scopeFactory;
        this.logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(StartupDelay, stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        try
        {
            using var scope = scopeFactory.CreateScope();
            var jobStore = scope.ServiceProvider.GetRequiredService<IUpgradeJobStore>();
            if (!jobStore.TryGetLatestActive(out var activeJob))
            {
                logger.LogInformation("No active upgrade job was found during startup recovery.");
                return;
            }

            logger.LogWarning(
                "Active upgrade job found during startup recovery. JobId: {JobId}. Stage: {Stage}. Status: {Status}.",
                activeJob.Id,
                activeJob.CurrentStage,
                activeJob.Status);

            var jobRunner = scope.ServiceProvider.GetRequiredService<IUpgradeJobRunner>();
            jobRunner.Recover(activeJob.Id);
        }
        catch (OperationCanceledException)
        {
            // Application is shutting down.
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Upgrade job startup recovery failed.");
        }
    }
}
