namespace BoldBI.UpgradeCenter.Services;

public sealed class UpgradeRollbackJobRunner : IUpgradeRollbackJobRunner
{
    private readonly IServiceScopeFactory scopeFactory;
    private readonly IUpgradeJobStore jobStore;
    private readonly IUpgradeRollbackStore rollbackStore;
    private readonly ILogger<UpgradeRollbackJobRunner> logger;

    public UpgradeRollbackJobRunner(
        IServiceScopeFactory scopeFactory,
        IUpgradeJobStore jobStore,
        IUpgradeRollbackStore rollbackStore,
        ILogger<UpgradeRollbackJobRunner> logger)
    {
        this.scopeFactory = scopeFactory;
        this.jobStore = jobStore;
        this.rollbackStore = rollbackStore;
        this.logger = logger;
    }

    public void Start(string rollbackJobId, string rollbackId)
    {
        _ = Task.Run(() => RunAsync(rollbackJobId, rollbackId, CancellationToken.None));
    }

    private async Task RunAsync(string rollbackJobId, string rollbackId, CancellationToken cancellationToken)
    {
        try
        {
            using var scope = scopeFactory.CreateScope();
            var databaseBackupService = scope.ServiceProvider.GetRequiredService<IUpgradeDatabaseBackupService>();
            var kubernetesUpgradeService = scope.ServiceProvider.GetRequiredService<IKubernetesUpgradeService>();
            var historyStore = scope.ServiceProvider.GetRequiredService<IUpgradeHistoryStore>();

            if (!rollbackStore.TryGet(rollbackId, out var rollbackEntry))
            {
                FailJob(rollbackJobId, UpgradeJobStageName.RollbackComplete, "Rollback failed because the selected rollback point is no longer available.");
                return;
            }

            jobStore.MarkRunning(rollbackJobId);
            await RestoreDatabaseAsync(rollbackJobId, rollbackEntry, databaseBackupService, cancellationToken);

            var databaseSucceeded = rollbackStore.TryGet(rollbackId, out rollbackEntry) &&
                                    rollbackEntry.LastRestoreResult?.Succeeded == true;
            var kubernetesSucceeded = await RestoreKubernetesImagesAsync(rollbackJobId, rollbackEntry, kubernetesUpgradeService, cancellationToken);
            if (rollbackStore.TryGet(rollbackId, out rollbackEntry) &&
                rollbackEntry.LastRestoreResult is not null)
            {
                rollbackStore.MarkRestoreCompleted(
                    rollbackId,
                    rollbackEntry.LastRestoreResult,
                    rollbackEntry.LastKubernetesRollbackResult);
            }

            await VerifyHealthAsync(rollbackJobId, kubernetesSucceeded, cancellationToken);

            var rollbackSucceeded = databaseSucceeded && kubernetesSucceeded;
            var message = BuildCompletionMessage(rollbackEntry);
            jobStore.MarkStageRunning(rollbackJobId, UpgradeJobStageName.RollbackComplete, "Finalizing rollback state.");
            jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackComplete, "rollback-complete", "Finalizing rollback state.");

            if (rollbackSucceeded)
            {
                await CleanupConsumedRollbackPointAsync(rollbackId, databaseBackupService, cancellationToken);
                jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackComplete, "rollback-complete", "Rollback completed successfully.");
                jobStore.MarkStageSucceeded(rollbackJobId, UpgradeJobStageName.RollbackComplete, "Rollback completed successfully.");
                jobStore.Complete(rollbackJobId, UpgradeJobStatus.RollbackSucceeded, "Manual rollback completed.");
                CompleteParentUpgradeJob(rollbackEntry, UpgradeJobStatus.RollbackSucceeded, "Manual rollback completed.");
            }
            else
            {
                jobStore.MarkOperationFailed(rollbackJobId, UpgradeJobStageName.RollbackComplete, "rollback-complete", message);
                jobStore.MarkStageFailed(rollbackJobId, UpgradeJobStageName.RollbackComplete, message);
                jobStore.Complete(rollbackJobId, UpgradeJobStatus.RollbackFailed, message);
                CompleteParentUpgradeJob(rollbackEntry, UpgradeJobStatus.RollbackFailed, message);
            }

            await historyStore.UpdateCompletionAsync(
                rollbackJobId,
                rollbackSucceeded ? UpgradeJobStatus.RollbackSucceeded.ToString() : UpgradeJobStatus.RollbackFailed.ToString(),
                DateTimeOffset.UtcNow,
                false,
                rollbackJobId,
                rollbackSucceeded ? null : UpgradeJobStageName.RollbackComplete.ToString(),
                rollbackSucceeded ? null : message,
                cancellationToken);
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Manual rollback job failed unexpectedly. RollbackJobId: {RollbackJobId}.", rollbackJobId);
            var message = $"Manual rollback failed unexpectedly: {exception.Message}";
            FailJob(rollbackJobId, UpgradeJobStageName.RollbackComplete, message);
        }
    }

    private async Task RestoreDatabaseAsync(
        string rollbackJobId,
        UpgradeRollbackEntry rollbackEntry,
        IUpgradeDatabaseBackupService databaseBackupService,
        CancellationToken cancellationToken)
    {
        jobStore.MarkStageRunning(rollbackJobId, UpgradeJobStageName.RollbackDatabaseRestore, "Restoring affected database tables from backup.");
        jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackDatabaseRestore, "rollback-database", "Restoring affected database tables from backup.");

        var restoreResult = await databaseBackupService.RestoreAffectedTablesAsync(rollbackEntry.BackupResult, cancellationToken);
        if (restoreResult.Succeeded)
        {
            jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackDatabaseRestore, "rollback-database", Safe(restoreResult.Message));
            jobStore.MarkStageSucceeded(rollbackJobId, UpgradeJobStageName.RollbackDatabaseRestore, Safe(restoreResult.Message));
        }
        else
        {
            jobStore.MarkOperationFailed(rollbackJobId, UpgradeJobStageName.RollbackDatabaseRestore, "rollback-database", Safe(restoreResult.Message));
            jobStore.MarkStageFailed(rollbackJobId, UpgradeJobStageName.RollbackDatabaseRestore, Safe(restoreResult.Message));
        }

        rollbackStore.MarkRestoreProgress(rollbackEntry.Id, restoreResult, null, restoreResult.Succeeded
            ? "Database restore completed. Kubernetes image rollback is pending."
            : "Database restore failed. Kubernetes image rollback will still be attempted.");
    }

    private async Task<bool> RestoreKubernetesImagesAsync(
        string rollbackJobId,
        UpgradeRollbackEntry rollbackEntry,
        IKubernetesUpgradeService kubernetesUpgradeService,
        CancellationToken cancellationToken)
    {
        jobStore.MarkStageRunning(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, "Reverting recorded Kubernetes deployment images.");
        jobStore.MarkStageRunning(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, "Waiting for reverted deployment rollouts.");

        void ReportRollbackProgress(KubernetesDeploymentProgress progress)
        {
            UpdateKubernetesRollbackProgress(rollbackJobId, progress);
        }

        var kubernetesRollbackResult = rollbackEntry.KubernetesResult is null
            ? KubernetesRollbackResult.CreateSkipped("Kubernetes rollback skipped because this rollback point does not contain deployment image mappings.")
            : await kubernetesUpgradeService.RollbackAsync(rollbackEntry.KubernetesResult, cancellationToken, ReportRollbackProgress);

        if (kubernetesRollbackResult.Skipped)
        {
            jobStore.MarkStageSkipped(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, Safe(kubernetesRollbackResult.Message));
            jobStore.MarkStageSkipped(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, Safe(kubernetesRollbackResult.Message));
        }
        else if (kubernetesRollbackResult.Succeeded)
        {
            jobStore.MarkStageSucceeded(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, "Deployment image tags were reverted.");
            jobStore.MarkStageSucceeded(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, Safe(kubernetesRollbackResult.Message));
        }
        else
        {
            jobStore.MarkStageFailed(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, Safe(kubernetesRollbackResult.Message));
        }

        var databaseResult = rollbackStore.TryGet(rollbackEntry.Id, out var latestEntry)
            ? latestEntry.LastRestoreResult
            : null;
        rollbackStore.MarkRestoreProgress(
            rollbackEntry.Id,
            databaseResult,
            kubernetesRollbackResult,
            $"Database rollback: {databaseResult?.Message ?? "Database rollback result is not available yet."}. Kubernetes rollback: {kubernetesRollbackResult.Message}.");

        return kubernetesRollbackResult.Succeeded;
    }

    private Task VerifyHealthAsync(string rollbackJobId, bool kubernetesSucceeded, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        jobStore.MarkStageRunning(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, "Verifying deployment health from Kubernetes rollout results.");
        jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, "rollback-health", "Verifying deployment health from Kubernetes rollout results.");

        if (kubernetesSucceeded)
        {
            jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, "rollback-health", "All reverted deployments reported healthy rollouts.");
            jobStore.MarkStageSucceeded(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, "All reverted deployments reported healthy rollouts.");
        }
        else
        {
            jobStore.MarkOperationFailed(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, "rollback-health", "One or more reverted deployments did not report a healthy rollout.");
            jobStore.MarkStageFailed(rollbackJobId, UpgradeJobStageName.RollbackHealthVerification, "One or more reverted deployments did not report a healthy rollout.");
        }

        return Task.CompletedTask;
    }

    private void UpdateKubernetesRollbackProgress(string rollbackJobId, KubernetesDeploymentProgress progress)
    {
        var imageOperationId = InMemoryUpgradeJobStore.KubernetesRollbackImageOperationId(progress.DeploymentName);
        var rolloutOperationId = InMemoryUpgradeJobStore.KubernetesRollbackRolloutOperationId(progress.DeploymentName);
        var message = Safe(progress.Message);

        switch (progress.Status)
        {
            case KubernetesDeploymentProgressStatus.RollingBack:
                jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, imageOperationId, message);
                break;
            case KubernetesDeploymentProgressStatus.RollbackWaitingForRollout:
                jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, imageOperationId, "Deployment image tag was reverted.");
                jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, rolloutOperationId, message);
                break;
            case KubernetesDeploymentProgressStatus.RollbackCompleted:
                jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackImageRevert, imageOperationId, "Deployment image tag was reverted.");
                jobStore.MarkOperationSucceeded(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, rolloutOperationId, message);
                break;
            case KubernetesDeploymentProgressStatus.RollbackFailed:
                jobStore.MarkOperationFailed(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, rolloutOperationId, message);
                break;
            default:
                jobStore.MarkOperationRunning(rollbackJobId, UpgradeJobStageName.RollbackRolloutWait, rolloutOperationId, message);
                break;
        }
    }

    private void FailJob(string rollbackJobId, UpgradeJobStageName stageName, string message)
    {
        jobStore.MarkStageFailed(rollbackJobId, stageName, Safe(message));
        jobStore.Complete(rollbackJobId, UpgradeJobStatus.RollbackFailed, Safe(message));
    }

    private void CompleteParentUpgradeJob(UpgradeRollbackEntry rollbackEntry, UpgradeJobStatus status, string message)
    {
        if (string.IsNullOrWhiteSpace(rollbackEntry.UpgradeJobId))
        {
            return;
        }

        if (!jobStore.TryGet(rollbackEntry.UpgradeJobId, out _))
        {
            return;
        }

        jobStore.Complete(rollbackEntry.UpgradeJobId, status, Safe(message));
    }

    private async Task CleanupConsumedRollbackPointAsync(
        string rollbackId,
        IUpgradeDatabaseBackupService databaseBackupService,
        CancellationToken cancellationToken)
    {
        if (!rollbackStore.TryGet(rollbackId, out var rollbackEntry))
        {
            return;
        }

        try
        {
            var cleanupResult = await databaseBackupService.CleanupBackupDatabasesAsync(rollbackEntry.BackupResult, cancellationToken);
            rollbackStore.MarkCleanupCompleted(rollbackId, cleanupResult, cleanupResult.Succeeded);
            if (!cleanupResult.Succeeded)
            {
                logger.LogWarning(
                    "Manual rollback backup cleanup did not complete. RollbackId: {RollbackId}. Message: {Message}",
                    rollbackId,
                    Safe(cleanupResult.Message));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Manual rollback backup cleanup failed unexpectedly. RollbackId: {RollbackId}. SafeMessage: {SafeMessage}",
                rollbackId,
                Safe(exception.Message));
            rollbackStore.MarkCleanupCompleted(
                rollbackId,
                new BackupCleanupResult(false, $"Backup cleanup failed unexpectedly: {Safe(exception.Message)}"),
                false);
        }
    }

    private static string BuildCompletionMessage(UpgradeRollbackEntry rollbackEntry)
    {
        return $"Database rollback: {rollbackEntry.LastRestoreResult?.Message ?? "Result unavailable"}. Kubernetes rollback: {rollbackEntry.LastKubernetesRollbackResult?.Message ?? "Result unavailable"}.";
    }

    private static string Safe(string? value)
    {
        return string.IsNullOrWhiteSpace(value)
            ? "No additional details were returned."
            : value;
    }
}
