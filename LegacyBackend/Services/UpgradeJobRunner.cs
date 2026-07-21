using System.Text.RegularExpressions;

namespace Bold.UpgradeCenter.Services;

public sealed class UpgradeJobRunner : IUpgradeJobRunner
{
    private static readonly Regex SensitiveAssignmentPattern = new(
        @"(?<key>Password|Pwd|User\s*ID|Username|Token|AccessToken|Secret|Key)\s*=\s*[^;,\s]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly IServiceScopeFactory scopeFactory;
    private readonly IUpgradeJobStore jobStore;
    private readonly ILogger<UpgradeJobRunner> logger;

    public UpgradeJobRunner(
        IServiceScopeFactory scopeFactory,
        IUpgradeJobStore jobStore,
        ILogger<UpgradeJobRunner> logger)
    {
        this.scopeFactory = scopeFactory;
        this.jobStore = jobStore;
        this.logger = logger;
    }

    public void Start(string jobId)
    {
        _ = Task.Run(() => RunAsync(jobId, CancellationToken.None));
    }

    public void Recover(string jobId)
    {
        _ = Task.Run(() => RunRecoveryAsync(jobId, CancellationToken.None));
    }

    private async Task RunAsync(string jobId, CancellationToken cancellationToken)
    {
        if (!jobStore.TryGet(jobId, out var job))
        {
            logger.LogWarning("Upgrade job {JobId} could not be started because it was not found.", jobId);
            return;
        }

        jobStore.MarkRunning(jobId);
        logger.LogInformation("Upgrade job started. JobId: {JobId}. TargetVersion: {TargetVersion}.", job.Id, job.TargetVersion);

        try
        {
            using var scope = scopeFactory.CreateScope();
            var playwrightRunner = scope.ServiceProvider.GetRequiredService<IPlaywrightScriptRunner>();
            var databaseBackupService = scope.ServiceProvider.GetRequiredService<IUpgradeDatabaseBackupService>();
            var kubernetesUpgradeService = scope.ServiceProvider.GetRequiredService<IKubernetesUpgradeService>();
            var rollbackStore = scope.ServiceProvider.GetRequiredService<IUpgradeRollbackStore>();
            var historyStore = scope.ServiceProvider.GetRequiredService<IUpgradeHistoryStore>();
            var isCustomPatch = job.UpgradeType == UpgradeJobType.CustomVersionOrPatch;

            jobStore.MarkStageRunning(jobId, UpgradeJobStageName.PreUpgradeValidation, "Running pre-upgrade Playwright validation.");
            jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-start", "Starting pre-upgrade Playwright validation.");
            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-start", "Pre-upgrade Playwright validation started.");
            jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-tests", "Executing pre-upgrade validation tests.");
            var preUpgradeResult = await playwrightRunner.ExecutePreUpgradeAsync(cancellationToken);
            if (!preUpgradeResult.Succeeded)
            {
                jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-tests", Safe(preUpgradeResult.Message));
                FailStageAndJob(jobId, UpgradeJobStageName.PreUpgradeValidation, preUpgradeResult.Message);
                await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Failed, false, null, UpgradeJobStageName.PreUpgradeValidation, preUpgradeResult.Message, cancellationToken);
                return;
            }

            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-tests", Safe(preUpgradeResult.Message));
            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.PreUpgradeValidation, "pre-validation-complete", "Pre-upgrade validation completed successfully.");
            jobStore.MarkStageSucceeded(jobId, UpgradeJobStageName.PreUpgradeValidation, Safe(preUpgradeResult.Message));

            UpgradeDatabaseBackupResult backupResult;
            if (isCustomPatch)
            {
                backupResult = UpgradeDatabaseBackupResult.Skipped(
                    job.TargetVersion,
                    "Database script lookup and affected-table backup are skipped for custom patch image upgrades.");
                jobStore.MarkStageSkipped(jobId, UpgradeJobStageName.DatabaseBackup, Safe(backupResult.Message));
            }
            else
            {
                jobStore.MarkStageRunning(jobId, UpgradeJobStageName.DatabaseBackup, "Backing up affected database tables.");
                jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.DatabaseBackup, "database-discovery", "Discovering master and tenant databases.");
                jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.DatabaseBackup, "database-impact", "Analyzing database scripts for the selected upgrade range.");
                jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.DatabaseBackup, "table-discovery", "Identifying cumulative affected tables.");
                jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.DatabaseBackup, "database-backup", "Backing up affected database tables.");
                backupResult = await databaseBackupService.BackupAffectedTablesAsync(job.CurrentVersion, job.TargetVersion, cancellationToken);
                jobStore.ReplaceStageOperations(jobId, UpgradeJobStageName.DatabaseBackup, BuildDatabaseBackupOperations(backupResult));
                if (!backupResult.Succeeded)
                {
                    await CleanupFailedNewBackupAsync(backupResult, databaseBackupService, cancellationToken);
                    FailStageAndJob(jobId, UpgradeJobStageName.DatabaseBackup, backupResult.Message);
                    await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Failed, false, null, UpgradeJobStageName.DatabaseBackup, backupResult.Message, cancellationToken);
                    return;
                }

                jobStore.MarkStageSucceeded(
                    jobId,
                    UpgradeJobStageName.DatabaseBackup,
                    backupResult.BackupSkipped
                        ? Safe(backupResult.Message)
                        : $"Database backup completed. Version count: {backupResult.AnalyzedVersions.Count}. Backup database count: {backupResult.BackupMappings.Count}. Affected table count: {backupResult.AffectedTables.Count}.");
            }

            jobStore.MarkStageRunning(jobId, UpgradeJobStageName.KubernetesImageUpgrade, "Updating all applicable Bold BI deployment images.");
            jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.KubernetesImageUpgrade, "kubernetes-images", "Retrieving target image details.");
            jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.KubernetesImageUpgrade, "kubernetes-deployments", "Updating applicable Bold BI deployments.");
            void ReportKubernetesProgress(KubernetesDeploymentProgress progress)
            {
                UpdateKubernetesProgress(jobId, progress);
            }

            UpgradeRollbackEntry? preparedRollbackEntry = null;
            async Task PersistPreparedRollbackContextAsync(KubernetesUpgradeResult preparedKubernetesResult)
            {
                if (preparedRollbackEntry is not null ||
                    preparedKubernetesResult.Skipped ||
                    preparedKubernetesResult.PreviousImages.Count == 0)
                {
                    return;
                }

                preparedRollbackEntry = rollbackStore.Save(
                    backupResult,
                    preparedKubernetesResult,
                    jobId,
                    job.InitiatedBy,
                    job.CurrentVersion,
                    job.TargetVersion);
                jobStore.MarkRollbackAvailable(jobId, preparedRollbackEntry.Id);
                await historyStore.UpdateCompletionAsync(
                    jobId,
                    UpgradeJobStatus.Running.ToString(),
                    null,
                    true,
                    preparedRollbackEntry.Id,
                    UpgradeJobStageName.KubernetesImageUpgrade.ToString(),
                    "Kubernetes rollback context was recorded before patching deployment images.",
                    cancellationToken);
                logger.LogInformation(
                    "Kubernetes rollback context recorded before image patching. JobId: {JobId}. RollbackId: {RollbackId}. DeploymentCount: {DeploymentCount}.",
                    jobId,
                    preparedRollbackEntry.Id,
                    preparedKubernetesResult.PreviousImages.Count);
                await CleanupSupersededRollbackPointsAsync(preparedRollbackEntry.Id, rollbackStore, databaseBackupService, cancellationToken);
            }

            var kubernetesResult = isCustomPatch
                ? await kubernetesUpgradeService.UpgradeWithCustomImagesAsync(
                    job.TargetVersion,
                    job.CustomImages,
                    cancellationToken,
                    ReportKubernetesProgress,
                    PersistPreparedRollbackContextAsync)
                : await kubernetesUpgradeService.UpgradeAsync(
                    job.TargetVersion,
                    KubernetesUpgradeScope.Bulk,
                    null,
                    cancellationToken,
                    ReportKubernetesProgress,
                    PersistPreparedRollbackContextAsync);
            jobStore.ReplaceStageOperations(jobId, UpgradeJobStageName.KubernetesImageUpgrade, BuildKubernetesUpgradeOperations(kubernetesResult));
            if (!kubernetesResult.Succeeded)
            {
                jobStore.MarkStageFailed(jobId, UpgradeJobStageName.KubernetesImageUpgrade, Safe(kubernetesResult.Message));
                var failedUpgradeRollbackEntry = preparedRollbackEntry;
                if (failedUpgradeRollbackEntry is null &&
                    !kubernetesResult.Skipped &&
                    kubernetesResult.PreviousImages.Count > 0)
                {
                    failedUpgradeRollbackEntry = rollbackStore.Save(
                        backupResult,
                        kubernetesResult,
                        jobId,
                        job.InitiatedBy,
                        job.CurrentVersion,
                        job.TargetVersion);
                    jobStore.MarkRollbackAvailable(jobId, failedUpgradeRollbackEntry.Id);
                    await historyStore.UpdateCompletionAsync(
                        jobId,
                        UpgradeJobStatus.Running.ToString(),
                        null,
                        true,
                        failedUpgradeRollbackEntry.Id,
                        UpgradeJobStageName.KubernetesImageUpgrade.ToString(),
                        Safe(kubernetesResult.Message),
                        cancellationToken);
                    await CleanupSupersededRollbackPointsAsync(failedUpgradeRollbackEntry.Id, rollbackStore, databaseBackupService, cancellationToken);
                }

                if (failedUpgradeRollbackEntry is not null)
                {
                    await RunAutomaticRollbackAsync(
                        job,
                        backupResult,
                        kubernetesResult,
                        failedUpgradeRollbackEntry,
                        databaseBackupService,
                        kubernetesUpgradeService,
                        rollbackStore,
                        historyStore,
                        UpgradeJobStageName.KubernetesImageUpgrade,
                        "Kubernetes image rollout failed. Running automatic rollback.",
                        cancellationToken);
                    return;
                }

                jobStore.Complete(jobId, UpgradeJobStatus.Failed, Safe(kubernetesResult.Message));
                await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Failed, false, null, UpgradeJobStageName.KubernetesImageUpgrade, kubernetesResult.Message, cancellationToken);
                return;
            }

            jobStore.MarkStageSucceeded(
                jobId,
                UpgradeJobStageName.KubernetesImageUpgrade,
                kubernetesResult.Skipped
                    ? Safe(kubernetesResult.Message)
                    : $"Kubernetes image upgrade completed. Deployment count: {kubernetesResult.DeploymentResults.Count}.");

            UpgradeRollbackEntry? rollbackEntry = null;
            if (kubernetesResult.Succeeded && !kubernetesResult.Skipped && kubernetesResult.PreviousImages.Count > 0)
            {
                rollbackEntry = preparedRollbackEntry;
                if (rollbackEntry is null)
                {
                    rollbackEntry = rollbackStore.Save(
                        backupResult,
                        kubernetesResult,
                        jobId,
                        job.InitiatedBy,
                        job.CurrentVersion,
                        job.TargetVersion);
                    jobStore.MarkRollbackAvailable(jobId, rollbackEntry.Id);
                }

                await historyStore.UpdateCompletionAsync(
                    jobId,
                    UpgradeJobStatus.Running.ToString(),
                    null,
                    true,
                    rollbackEntry.Id,
                    null,
                    null,
                    cancellationToken);
                    logger.LogInformation(
                        "Rollback point created for upgrade job. JobId: {JobId}. RollbackId: {RollbackId}.",
                        jobId,
                        rollbackEntry.Id);
                    await CleanupSupersededRollbackPointsAsync(rollbackEntry.Id, rollbackStore, databaseBackupService, cancellationToken);
                }

            if (kubernetesResult.Skipped)
            {
                jobStore.MarkStageSkipped(jobId, UpgradeJobStageName.PostUpgradeValidation, "Post-upgrade validation skipped because Kubernetes image upgrade was skipped.");
                jobStore.MarkStageSkipped(jobId, UpgradeJobStageName.AutomaticRollback, "Automatic rollback was not required.");
                jobStore.Complete(jobId, UpgradeJobStatus.Succeeded);
                await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Succeeded, rollbackEntry is not null, rollbackEntry?.Id, null, null, cancellationToken);
                return;
            }

            jobStore.MarkStageRunning(jobId, UpgradeJobStageName.PostUpgradeValidation, "Running post-upgrade Playwright validation.");
            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.PostUpgradeValidation, "post-validation-wait", "Kubernetes image upgrade completed; starting post-upgrade validation.");
            jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.PostUpgradeValidation, "post-validation-tests", "Executing post-upgrade validation tests.");
            var postUpgradeResult = await playwrightRunner.ExecutePostUpgradeAsync(cancellationToken);
            if (postUpgradeResult.Succeeded)
            {
                jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.PostUpgradeValidation, "post-validation-tests", Safe(postUpgradeResult.Message));
                jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.PostUpgradeValidation, "post-validation-complete", "Post-upgrade validation completed successfully.");
                jobStore.MarkStageSucceeded(jobId, UpgradeJobStageName.PostUpgradeValidation, Safe(postUpgradeResult.Message));
                jobStore.MarkStageSkipped(jobId, UpgradeJobStageName.AutomaticRollback, "Automatic rollback was not required.");
                jobStore.Complete(jobId, UpgradeJobStatus.Succeeded);
                await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.Succeeded, rollbackEntry is not null, rollbackEntry?.Id, null, null, cancellationToken);
                return;
            }

            jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.PostUpgradeValidation, "post-validation-tests", Safe(postUpgradeResult.Message));
            jobStore.MarkStageFailed(jobId, UpgradeJobStageName.PostUpgradeValidation, Safe(postUpgradeResult.Message));
            await RunAutomaticRollbackAsync(
                job,
                backupResult,
                kubernetesResult,
                rollbackEntry,
                databaseBackupService,
                kubernetesUpgradeService,
                rollbackStore,
                historyStore,
                UpgradeJobStageName.PostUpgradeValidation,
                "Post-upgrade validation failed. Running automatic rollback.",
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            FailCurrentJob(jobId, "Upgrade job was canceled.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Upgrade job failed unexpectedly. JobId: {JobId}.", jobId);
            FailCurrentJob(jobId, $"Upgrade job failed unexpectedly: {exception.Message}");
        }
    }

    private async Task RunRecoveryAsync(string jobId, CancellationToken cancellationToken)
    {
        if (!jobStore.TryGet(jobId, out var job))
        {
            logger.LogWarning("Upgrade job recovery skipped because job {JobId} was not found.", jobId);
            return;
        }

        if (job.Status is not (UpgradeJobStatus.Queued or UpgradeJobStatus.Running))
        {
            logger.LogInformation(
                "Upgrade job recovery skipped because job is already terminal. JobId: {JobId}. Status: {Status}.",
                jobId,
                job.Status);
            return;
        }

        if (job.Status == UpgradeJobStatus.Queued)
        {
            logger.LogInformation("Queued upgrade job recovered and started. JobId: {JobId}.", jobId);
            await RunAsync(jobId, cancellationToken);
            return;
        }

        logger.LogWarning(
            "Recovering active upgrade job after Upgrade Center restart. JobId: {JobId}. Stage: {Stage}.",
            jobId,
            job.CurrentStage);

        using var scope = scopeFactory.CreateScope();
        var playwrightRunner = scope.ServiceProvider.GetRequiredService<IPlaywrightScriptRunner>();
        var databaseBackupService = scope.ServiceProvider.GetRequiredService<IUpgradeDatabaseBackupService>();
        var kubernetesUpgradeService = scope.ServiceProvider.GetRequiredService<IKubernetesUpgradeService>();
        var rollbackStore = scope.ServiceProvider.GetRequiredService<IUpgradeRollbackStore>();
        var historyStore = scope.ServiceProvider.GetRequiredService<IUpgradeHistoryStore>();
        var rollbackEntry = ResolveRollbackEntry(job, rollbackStore);

        try
        {
            switch (job.CurrentStage)
            {
                case UpgradeJobStageName.KubernetesImageUpgrade:
                    await RecoverKubernetesImageUpgradeAsync(
                        job,
                        rollbackEntry,
                        playwrightRunner,
                        databaseBackupService,
                        kubernetesUpgradeService,
                        rollbackStore,
                        historyStore,
                        cancellationToken);
                    return;

                case UpgradeJobStageName.PostUpgradeValidation:
                    await RecoverPostUpgradeValidationAsync(
                        job,
                        rollbackEntry,
                        playwrightRunner,
                        databaseBackupService,
                        kubernetesUpgradeService,
                        rollbackStore,
                        historyStore,
                        cancellationToken);
                    return;

                case UpgradeJobStageName.AutomaticRollback:
                    await RecoverAutomaticRollbackAsync(
                        job,
                        rollbackEntry,
                        databaseBackupService,
                        kubernetesUpgradeService,
                        rollbackStore,
                        historyStore,
                        cancellationToken);
                    return;

                default:
                    await FailRecoveredJob(
                        job,
                        historyStore,
                        $"Upgrade Center restarted while job was in stage '{job.CurrentStage}'. This stage cannot be safely resumed automatically.",
                        cancellationToken);
                    return;
            }
        }
        catch (OperationCanceledException)
        {
            FailCurrentJob(jobId, "Recovered upgrade job was canceled.");
        }
        catch (Exception exception)
        {
            logger.LogError(exception, "Upgrade job recovery failed unexpectedly. JobId: {JobId}.", jobId);
            FailCurrentJob(jobId, $"Upgrade job recovery failed unexpectedly: {exception.Message}");
        }
    }

    private async Task RecoverKubernetesImageUpgradeAsync(
        UpgradeJob job,
        UpgradeRollbackEntry? rollbackEntry,
        IPlaywrightScriptRunner playwrightRunner,
        IUpgradeDatabaseBackupService databaseBackupService,
        IKubernetesUpgradeService kubernetesUpgradeService,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeHistoryStore historyStore,
        CancellationToken cancellationToken)
    {
        if (rollbackEntry?.KubernetesResult is null)
        {
            await FailRecoveredJob(
                job,
                historyStore,
                "Upgrade Center restarted during Kubernetes image upgrade, but no persisted Kubernetes rollback context was available. Automatic rollback cannot safely continue.",
                cancellationToken);
            return;
        }

        jobStore.MarkStageRunning(job.Id, UpgradeJobStageName.KubernetesImageUpgrade, "Recovering Kubernetes rollout status after Upgrade Center restart.");
        void ReportKubernetesProgress(KubernetesDeploymentProgress progress)
        {
            UpdateKubernetesProgress(job.Id, progress);
        }

        var recoveredKubernetesResult = await kubernetesUpgradeService.ReconcileUpgradeRolloutAsync(
            rollbackEntry.KubernetesResult,
            cancellationToken,
            ReportKubernetesProgress);
        jobStore.ReplaceStageOperations(job.Id, UpgradeJobStageName.KubernetesImageUpgrade, BuildKubernetesUpgradeOperations(recoveredKubernetesResult));

        if (!recoveredKubernetesResult.Succeeded)
        {
            jobStore.MarkStageFailed(job.Id, UpgradeJobStageName.KubernetesImageUpgrade, Safe(recoveredKubernetesResult.Message));
            await historyStore.UpdateCompletionAsync(
                job.Id,
                UpgradeJobStatus.Running.ToString(),
                null,
                true,
                rollbackEntry.Id,
                UpgradeJobStageName.KubernetesImageUpgrade.ToString(),
                Safe(recoveredKubernetesResult.Message),
                cancellationToken);
            await RunAutomaticRollbackAsync(
                job,
                rollbackEntry.BackupResult,
                recoveredKubernetesResult,
                rollbackEntry,
                databaseBackupService,
                kubernetesUpgradeService,
                rollbackStore,
                historyStore,
                UpgradeJobStageName.KubernetesImageUpgrade,
                "Recovered Kubernetes rollout failed. Running automatic rollback.",
                cancellationToken);
            return;
        }

        jobStore.MarkStageSucceeded(
            job.Id,
            UpgradeJobStageName.KubernetesImageUpgrade,
            $"Kubernetes rollout recovery completed. Deployment count: {recoveredKubernetesResult.DeploymentResults.Count}.");
        await RunPostUpgradeValidationAfterKubernetesRecoveryAsync(
            job,
            rollbackEntry.BackupResult,
            recoveredKubernetesResult,
            rollbackEntry,
            playwrightRunner,
            databaseBackupService,
            kubernetesUpgradeService,
            rollbackStore,
            historyStore,
            cancellationToken);
    }

    private async Task RecoverPostUpgradeValidationAsync(
        UpgradeJob job,
        UpgradeRollbackEntry? rollbackEntry,
        IPlaywrightScriptRunner playwrightRunner,
        IUpgradeDatabaseBackupService databaseBackupService,
        IKubernetesUpgradeService kubernetesUpgradeService,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeHistoryStore historyStore,
        CancellationToken cancellationToken)
    {
        if (rollbackEntry?.KubernetesResult is null)
        {
            await FailRecoveredJob(
                job,
                historyStore,
                "Upgrade Center restarted during post-upgrade validation, but no persisted rollback context was available. Automatic rollback cannot safely continue.",
                cancellationToken);
            return;
        }

        await RunPostUpgradeValidationAfterKubernetesRecoveryAsync(
            job,
            rollbackEntry.BackupResult,
            rollbackEntry.KubernetesResult,
            rollbackEntry,
            playwrightRunner,
            databaseBackupService,
            kubernetesUpgradeService,
            rollbackStore,
            historyStore,
            cancellationToken);
    }

    private async Task RecoverAutomaticRollbackAsync(
        UpgradeJob job,
        UpgradeRollbackEntry? rollbackEntry,
        IUpgradeDatabaseBackupService databaseBackupService,
        IKubernetesUpgradeService kubernetesUpgradeService,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeHistoryStore historyStore,
        CancellationToken cancellationToken)
    {
        if (rollbackEntry?.KubernetesResult is null)
        {
            await FailRecoveredJob(
                job,
                historyStore,
                "Upgrade Center restarted during automatic rollback, but no persisted rollback context was available.",
                cancellationToken);
            return;
        }

        var alreadyStarted = rollbackEntry.State == UpgradeRollbackState.InProgress;
        await RunAutomaticRollbackAsync(
            job,
            rollbackEntry.BackupResult,
            rollbackEntry.KubernetesResult,
            rollbackEntry,
            databaseBackupService,
            kubernetesUpgradeService,
            rollbackStore,
            historyStore,
            UpgradeJobStageName.AutomaticRollback,
            "Recovering automatic rollback after Upgrade Center restart.",
            cancellationToken,
            alreadyStarted);
    }

    private async Task RunPostUpgradeValidationAfterKubernetesRecoveryAsync(
        UpgradeJob job,
        UpgradeDatabaseBackupResult backupResult,
        KubernetesUpgradeResult kubernetesResult,
        UpgradeRollbackEntry rollbackEntry,
        IPlaywrightScriptRunner playwrightRunner,
        IUpgradeDatabaseBackupService databaseBackupService,
        IKubernetesUpgradeService kubernetesUpgradeService,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeHistoryStore historyStore,
        CancellationToken cancellationToken)
    {
        jobStore.MarkStageRunning(job.Id, UpgradeJobStageName.PostUpgradeValidation, "Running post-upgrade Playwright validation after recovery.");
        jobStore.MarkOperationSucceeded(job.Id, UpgradeJobStageName.PostUpgradeValidation, "post-validation-wait", "Kubernetes rollout recovery completed; starting post-upgrade validation.");
        jobStore.MarkOperationRunning(job.Id, UpgradeJobStageName.PostUpgradeValidation, "post-validation-tests", "Executing post-upgrade validation tests.");

        var postUpgradeResult = await playwrightRunner.ExecutePostUpgradeAsync(cancellationToken);
        if (postUpgradeResult.Succeeded)
        {
            jobStore.MarkOperationSucceeded(job.Id, UpgradeJobStageName.PostUpgradeValidation, "post-validation-tests", Safe(postUpgradeResult.Message));
            jobStore.MarkOperationSucceeded(job.Id, UpgradeJobStageName.PostUpgradeValidation, "post-validation-complete", "Post-upgrade validation completed successfully.");
            jobStore.MarkStageSucceeded(job.Id, UpgradeJobStageName.PostUpgradeValidation, Safe(postUpgradeResult.Message));
            jobStore.MarkStageSkipped(job.Id, UpgradeJobStageName.AutomaticRollback, "Automatic rollback was not required.");
            jobStore.Complete(job.Id, UpgradeJobStatus.Succeeded);
            await UpdateHistoryCompletionAsync(historyStore, job.Id, UpgradeJobStatus.Succeeded, true, rollbackEntry.Id, null, null, cancellationToken);
            return;
        }

        jobStore.MarkOperationFailed(job.Id, UpgradeJobStageName.PostUpgradeValidation, "post-validation-tests", Safe(postUpgradeResult.Message));
        jobStore.MarkStageFailed(job.Id, UpgradeJobStageName.PostUpgradeValidation, Safe(postUpgradeResult.Message));
        await RunAutomaticRollbackAsync(
            job,
            backupResult,
            kubernetesResult,
            rollbackEntry,
            databaseBackupService,
            kubernetesUpgradeService,
            rollbackStore,
            historyStore,
            UpgradeJobStageName.PostUpgradeValidation,
            "Post-upgrade validation failed after recovery. Running automatic rollback.",
            cancellationToken);
    }

    private async Task RunAutomaticRollbackAsync(
        UpgradeJob job,
        UpgradeDatabaseBackupResult backupResult,
        KubernetesUpgradeResult kubernetesResult,
        UpgradeRollbackEntry? rollbackEntry,
        IUpgradeDatabaseBackupService databaseBackupService,
        IKubernetesUpgradeService kubernetesUpgradeService,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeHistoryStore historyStore,
        UpgradeJobStageName failureStage,
        string failureSummary,
        CancellationToken cancellationToken,
        bool rollbackAlreadyStarted = false)
    {
        var jobId = job.Id;
        jobStore.MarkStageRunning(jobId, UpgradeJobStageName.AutomaticRollback, failureSummary);
        jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-database", "Restoring affected database backups.");
        if (!rollbackAlreadyStarted &&
            rollbackEntry is not null &&
            !rollbackStore.TryBeginRestore(rollbackEntry.Id, out rollbackEntry, out var beginRollbackMessage))
        {
            var safeBeginRollbackMessage = Safe(beginRollbackMessage);
            jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-database", safeBeginRollbackMessage);
            jobStore.MarkStageFailed(jobId, UpgradeJobStageName.AutomaticRollback, safeBeginRollbackMessage);
            jobStore.Complete(jobId, UpgradeJobStatus.RollbackFailed, safeBeginRollbackMessage);
            await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.RollbackFailed, false, rollbackEntry?.Id, UpgradeJobStageName.AutomaticRollback, safeBeginRollbackMessage, cancellationToken);
            return;
        }

        var rollbackHistoryJobId = $"auto-rollback-{Guid.NewGuid():N}";
        await historyStore.SaveStartedAsync(new UpgradeHistoryRecord
        {
            JobId = rollbackHistoryJobId,
            ParentJobId = jobId,
            OperationType = UpgradeHistoryOperationTypes.AutomaticRollback,
            PreviousVersion = job.TargetVersion,
            TargetVersion = job.CurrentVersion,
            UpgradeType = "Rollback",
            Status = UpgradeJobStatus.Running.ToString(),
            StartedAt = DateTimeOffset.UtcNow,
            InitiatedByUserId = job.InitiatedBy.UserId,
            InitiatedByName = job.InitiatedBy.DisplayName,
            InitiatedByEmail = job.InitiatedBy.Email,
            InitiatedByRole = job.InitiatedBy.Role,
            RollbackMode = UpgradeHistoryRollbackModes.Automatic,
            RollbackJobId = rollbackHistoryJobId,
            FailureStage = failureStage.ToString(),
            FailureSummary = failureSummary
        }, cancellationToken);

        var databaseRollbackResult = await databaseBackupService.RestoreAffectedTablesAsync(backupResult, cancellationToken);
        if (databaseRollbackResult.Succeeded)
        {
            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-database", Safe(databaseRollbackResult.Message));
        }
        else
        {
            jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-database", Safe(databaseRollbackResult.Message));
        }

        jobStore.MarkOperationRunning(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-kubernetes", "Restoring previous Kubernetes deployment images.");
        void ReportRollbackProgress(KubernetesDeploymentProgress progress)
        {
            UpdateKubernetesProgress(jobId, progress);
        }

        var kubernetesRollbackResult = await kubernetesUpgradeService.RollbackAsync(kubernetesResult, cancellationToken, ReportRollbackProgress);
        if (kubernetesRollbackResult.Succeeded)
        {
            jobStore.MarkOperationSucceeded(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-kubernetes", Safe(kubernetesRollbackResult.Message));
        }
        else
        {
            jobStore.MarkOperationFailed(jobId, UpgradeJobStageName.AutomaticRollback, "rollback-kubernetes", Safe(kubernetesRollbackResult.Message));
        }

        if (rollbackEntry is not null)
        {
            rollbackStore.MarkRestoreCompleted(rollbackEntry.Id, databaseRollbackResult, kubernetesRollbackResult);
        }

        var rollbackSucceeded = databaseRollbackResult.Succeeded && kubernetesRollbackResult.Succeeded;
        var message = $"Database rollback: {databaseRollbackResult.Message}. Kubernetes rollback: {kubernetesRollbackResult.Message}.";
        if (rollbackSucceeded)
        {
            if (rollbackEntry is not null)
            {
                await CleanupConsumedRollbackPointAsync(rollbackEntry.Id, rollbackStore, databaseBackupService, cancellationToken);
            }

            jobStore.MarkStageSucceeded(jobId, UpgradeJobStageName.AutomaticRollback, Safe(message));
            jobStore.Complete(jobId, UpgradeJobStatus.RollbackSucceeded, "Automatic rollback completed.");
            await historyStore.UpdateCompletionAsync(rollbackHistoryJobId, UpgradeJobStatus.RollbackSucceeded.ToString(), DateTimeOffset.UtcNow, false, rollbackHistoryJobId, null, null, cancellationToken);
            await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.RollbackSucceeded, false, rollbackEntry?.Id, failureStage, "Automatic rollback completed.", cancellationToken);
            return;
        }

        jobStore.MarkStageFailed(jobId, UpgradeJobStageName.AutomaticRollback, Safe(message));
        jobStore.Complete(jobId, UpgradeJobStatus.RollbackFailed, "Automatic rollback did not complete successfully.");
        await historyStore.UpdateCompletionAsync(rollbackHistoryJobId, UpgradeJobStatus.RollbackFailed.ToString(), DateTimeOffset.UtcNow, false, rollbackHistoryJobId, UpgradeJobStageName.AutomaticRollback.ToString(), message, cancellationToken);
        await UpdateHistoryCompletionAsync(historyStore, jobId, UpgradeJobStatus.RollbackFailed, false, rollbackEntry?.Id, UpgradeJobStageName.AutomaticRollback, message, cancellationToken);
    }

    private async Task FailRecoveredJob(
        UpgradeJob job,
        IUpgradeHistoryStore historyStore,
        string message,
        CancellationToken cancellationToken)
    {
        var safeMessage = Safe(message);
        jobStore.MarkStageFailed(job.Id, job.CurrentStage, safeMessage);
        jobStore.Complete(job.Id, UpgradeJobStatus.Failed, safeMessage);
        await UpdateHistoryCompletionAsync(historyStore, job.Id, UpgradeJobStatus.Failed, false, job.RollbackId, job.CurrentStage, safeMessage, cancellationToken);
    }

    private static UpgradeRollbackEntry? ResolveRollbackEntry(UpgradeJob job, IUpgradeRollbackStore rollbackStore)
    {
        if (!string.IsNullOrWhiteSpace(job.RollbackId) &&
            rollbackStore.TryGet(job.RollbackId, out var entry))
        {
            return entry;
        }

        var latest = rollbackStore.GetLatest();
        return string.Equals(latest?.UpgradeJobId, job.Id, StringComparison.OrdinalIgnoreCase)
            ? latest
            : null;
    }

    private async Task CleanupSupersededRollbackPointsAsync(
        string activeRollbackId,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeDatabaseBackupService databaseBackupService,
        CancellationToken cancellationToken)
    {
        var supersededEntries = rollbackStore.MarkSupersededExcept(activeRollbackId);
        foreach (var supersededEntry in supersededEntries)
        {
            await CleanupRollbackPointAsync(supersededEntry, rollbackStore, databaseBackupService, cancellationToken);
        }
    }

    private async Task CleanupFailedNewBackupAsync(
        UpgradeDatabaseBackupResult backupResult,
        IUpgradeDatabaseBackupService databaseBackupService,
        CancellationToken cancellationToken)
    {
        if (backupResult.BackupMappings.Count == 0)
        {
            return;
        }

        try
        {
            var cleanupResult = await databaseBackupService.CleanupBackupDatabasesAsync(backupResult, cancellationToken);
            if (!cleanupResult.Succeeded)
            {
                logger.LogWarning(
                    "Partial backup cleanup did not complete after failed backup preparation. Message: {Message}",
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
                "Partial backup cleanup failed unexpectedly after failed backup preparation. SafeMessage: {SafeMessage}",
                Safe(exception.Message));
        }
    }

    private async Task CleanupConsumedRollbackPointAsync(
        string rollbackId,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeDatabaseBackupService databaseBackupService,
        CancellationToken cancellationToken)
    {
        if (rollbackStore.TryGet(rollbackId, out var entry))
        {
            await CleanupRollbackPointAsync(entry, rollbackStore, databaseBackupService, cancellationToken);
        }
    }

    private async Task CleanupRollbackPointAsync(
        UpgradeRollbackEntry rollbackEntry,
        IUpgradeRollbackStore rollbackStore,
        IUpgradeDatabaseBackupService databaseBackupService,
        CancellationToken cancellationToken)
    {
        try
        {
            var cleanupResult = await databaseBackupService.CleanupBackupDatabasesAsync(rollbackEntry.BackupResult, cancellationToken);
            rollbackStore.MarkCleanupCompleted(rollbackEntry.Id, cleanupResult, cleanupResult.Succeeded);
            if (!cleanupResult.Succeeded)
            {
                logger.LogWarning(
                    "Rollback backup cleanup did not complete. RollbackId: {RollbackId}. Message: {Message}",
                    rollbackEntry.Id,
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
                "Rollback backup cleanup failed unexpectedly. RollbackId: {RollbackId}. SafeMessage: {SafeMessage}",
                rollbackEntry.Id,
                Safe(exception.Message));
            rollbackStore.MarkCleanupCompleted(
                rollbackEntry.Id,
                new BackupCleanupResult(false, $"Backup cleanup failed unexpectedly: {Safe(exception.Message)}"),
                false);
        }
    }

    private static async Task UpdateHistoryCompletionAsync(
        IUpgradeHistoryStore historyStore,
        string jobId,
        UpgradeJobStatus status,
        bool rollbackAvailable,
        string? rollbackJobId,
        UpgradeJobStageName? failureStage,
        string? failureSummary,
        CancellationToken cancellationToken)
    {
        await historyStore.UpdateCompletionAsync(
            jobId,
            status.ToString(),
            DateTimeOffset.UtcNow,
            rollbackAvailable,
            rollbackJobId,
            failureStage?.ToString(),
            string.IsNullOrWhiteSpace(failureSummary) ? null : Safe(failureSummary),
            cancellationToken);
    }

    private void FailStageAndJob(string jobId, UpgradeJobStageName stageName, string message)
    {
        var safeMessage = Safe(message);
        jobStore.MarkStageFailed(jobId, stageName, safeMessage);
        jobStore.Complete(jobId, UpgradeJobStatus.Failed, safeMessage);
    }

    private void FailCurrentJob(string jobId, string message)
    {
        var safeMessage = Safe(message);
        if (jobStore.TryGet(jobId, out var job))
        {
            jobStore.MarkStageFailed(jobId, job.CurrentStage, safeMessage);
        }

        jobStore.Complete(jobId, UpgradeJobStatus.Failed, safeMessage);
    }

    private static IReadOnlyList<UpgradeJobOperation> BuildDatabaseBackupOperations(UpgradeDatabaseBackupResult backupResult)
    {
        if (backupResult.BackupSkipped)
        {
            return
            [
                Operation("database-discovery", "Identified affected databases", UpgradeJobStageStatus.Succeeded, BuildDatabaseDiscoveryMessage(backupResult)),
                Operation("database-impact", "Analyzed database impact", UpgradeJobStageStatus.Succeeded, BuildDatabaseImpactMessage(backupResult)),
                Operation("table-discovery", "Identified affected tables", UpgradeJobStageStatus.Skipped, Safe(backupResult.Message)),
                Operation("database-backup", "Backed up affected databases", UpgradeJobStageStatus.Skipped, Safe(backupResult.Message))
            ];
        }

        var operations = new List<UpgradeJobOperation>
        {
            Operation(
                "database-discovery",
                "Identified affected databases",
                backupResult.AnalyzedDatabaseTypes.Count > 0 ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Failed,
                backupResult.AnalyzedDatabaseTypes.Count > 0
                    ? BuildDatabaseDiscoveryMessage(backupResult)
                    : Safe(backupResult.Message)),
            Operation(
                "database-impact",
                "Analyzed database impact",
                backupResult.AnalyzedVersions.Count > 0 && backupResult.AffectedTables.Count > 0 ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Failed,
                BuildDatabaseImpactMessage(backupResult)),
            Operation(
                "table-discovery",
                "Identified affected tables",
                backupResult.AffectedTables.Count > 0 ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Skipped,
                backupResult.AffectedTables.Count > 0
                    ? $"Identified {backupResult.AffectedTables.Count} affected table(s)."
                    : "No affected tables were returned for backup.")
        };

        if (backupResult.BackupMappings.Count == 0)
        {
            operations.Add(Operation("database-backup", "Backed up affected databases", UpgradeJobStageStatus.Failed, Safe(backupResult.Message)));
            return operations;
        }

        for (var index = 0; index < backupResult.BackupMappings.Count; index++)
        {
            var mapping = backupResult.BackupMappings[index];
            var tableCount = mapping.BackupStatus?.TablesSucceeded ?? 0;
            operations.Add(Operation(
                $"database-backup-{index + 1}",
                $"{mapping.OriginalDatabaseName} backup",
                mapping.Succeeded ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Failed,
                mapping.Succeeded
                    ? $"Backed up {tableCount} table(s). Backup identifier: {mapping.BackupDatabaseName}."
                    : Safe(mapping.Message)));
        }

        return operations;
    }

    private static string BuildDatabaseDiscoveryMessage(UpgradeDatabaseBackupResult backupResult)
    {
        var databaseTypeText = backupResult.AnalyzedDatabaseTypes.Count == 0
            ? "not available"
            : string.Join(", ", backupResult.AnalyzedDatabaseTypes);
        var databaseCount = backupResult.BackupMappings.Count;
        return databaseCount == 0
            ? $"Database types analyzed: {databaseTypeText}."
            : $"Database types analyzed: {databaseTypeText}. Databases processed: {databaseCount}.";
    }

    private static string BuildDatabaseImpactMessage(UpgradeDatabaseBackupResult backupResult)
    {
        var versionText = backupResult.AnalyzedVersions.Count == 0
            ? "none"
            : string.Join(", ", backupResult.AnalyzedVersions.Select(FormatVersion));
        var currentVersion = string.IsNullOrWhiteSpace(backupResult.CurrentVersion)
            ? "current version"
            : FormatVersion(backupResult.CurrentVersion);
        var databaseTypes = backupResult.AnalyzedDatabaseTypes.Count == 0
            ? "not available"
            : string.Join(", ", backupResult.AnalyzedDatabaseTypes);

        return $"Current version: {currentVersion}. Target version: {FormatVersion(backupResult.SelectedVersion)}. Versions analyzed: {backupResult.AnalyzedVersions.Count} ({versionText}). Database types analyzed: {databaseTypes}. Unique affected tables: {backupResult.AffectedTables.Count}.";
    }

    private static string FormatVersion(string version)
    {
        return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : $"v{version}";
    }

    private static IReadOnlyList<UpgradeJobOperation> BuildKubernetesUpgradeOperations(KubernetesUpgradeResult kubernetesResult)
    {
        if (kubernetesResult.Skipped)
        {
            return
            [
                Operation("kubernetes-images", "Retrieved target image details", UpgradeJobStageStatus.Skipped, Safe(kubernetesResult.Message)),
                Operation("kubernetes-deployments", "Updated applicable deployment images", UpgradeJobStageStatus.Skipped, Safe(kubernetesResult.Message))
            ];
        }

        var operations = new List<UpgradeJobOperation>
        {
            Operation(
                "kubernetes-images",
                "Retrieved target image details",
                kubernetesResult.DeploymentResults.Count > 0 ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Failed,
                kubernetesResult.DeploymentResults.Count > 0
                    ? $"Retrieved image details for {kubernetesResult.DeploymentResults.Count} deployment(s)."
                    : Safe(kubernetesResult.Message))
        };

        if (kubernetesResult.DeploymentResults.Count == 0)
        {
            operations.Add(Operation("kubernetes-deployments", "Updated applicable deployment images", UpgradeJobStageStatus.Failed, Safe(kubernetesResult.Message)));
            return operations;
        }

        for (var index = 0; index < kubernetesResult.DeploymentResults.Count; index++)
        {
            var deployment = kubernetesResult.DeploymentResults[index];
            operations.Add(Operation(
                KubernetesUpgradeOperationId(deployment.DeploymentName),
                $"{deployment.DeploymentName} image update",
                deployment.Succeeded ? UpgradeJobStageStatus.Succeeded : UpgradeJobStageStatus.Failed,
                Safe(deployment.Message)));
        }

        return operations;
    }

    private void UpdateKubernetesProgress(string jobId, KubernetesDeploymentProgress progress)
    {
        var stageName = progress.Kind == KubernetesDeploymentProgressKind.Rollback
            ? UpgradeJobStageName.AutomaticRollback
            : UpgradeJobStageName.KubernetesImageUpgrade;
        var operationId = progress.Kind == KubernetesDeploymentProgressKind.Rollback
            ? KubernetesRollbackOperationId(progress.DeploymentName)
            : KubernetesUpgradeOperationId(progress.DeploymentName);
        var message = Safe(progress.Message);

        switch (progress.Status)
        {
            case KubernetesDeploymentProgressStatus.Completed:
            case KubernetesDeploymentProgressStatus.RollbackCompleted:
                jobStore.MarkOperationSucceeded(jobId, stageName, operationId, message);
                break;
            case KubernetesDeploymentProgressStatus.Failed:
            case KubernetesDeploymentProgressStatus.RollbackFailed:
                jobStore.MarkOperationFailed(jobId, stageName, operationId, message);
                break;
            default:
                jobStore.MarkOperationRunning(jobId, stageName, operationId, message);
                break;
        }
    }

    private static string KubernetesUpgradeOperationId(string deploymentName)
    {
        return $"kubernetes-deployment-{NormalizeOperationId(deploymentName)}";
    }

    private static string KubernetesRollbackOperationId(string deploymentName)
    {
        return $"kubernetes-rollback-{NormalizeOperationId(deploymentName)}";
    }

    private static string NormalizeOperationId(string value)
    {
        var normalized = Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static UpgradeJobOperation Operation(string id, string name, UpgradeJobStageStatus status, string? message)
    {
        var now = DateTimeOffset.UtcNow;
        return new UpgradeJobOperation
        {
            Id = id,
            Name = name,
            Status = status,
            StartedAt = now,
            CompletedAt = status is UpgradeJobStageStatus.Pending or UpgradeJobStageStatus.Running ? null : now,
            Message = status == UpgradeJobStageStatus.Failed ? null : Safe(message),
            SafeErrorDetails = status == UpgradeJobStageStatus.Failed ? Safe(message) : null
        };
    }

    private static string Safe(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "No additional details were returned.";
        }

        var sanitized = SensitiveAssignmentPattern.Replace(value, match => $"{match.Groups["key"].Value}=***");
        return sanitized.Length <= 1200 ? sanitized : sanitized[..1200] + "...";
    }
}
