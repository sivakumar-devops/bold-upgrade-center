using System.Collections.Concurrent;

namespace Bold.UpgradeCenter.Services;

// Keeps a hot in-memory cache and writes operational state through to persistent storage.
public sealed class InMemoryUpgradeJobStore : IUpgradeJobStore
{
    private readonly ConcurrentDictionary<string, UpgradeJob> jobs = new(StringComparer.OrdinalIgnoreCase);
    private readonly IUpgradeOperationalStateStore operationalStateStore;
    private readonly object syncRoot = new();

    public InMemoryUpgradeJobStore(IUpgradeOperationalStateStore operationalStateStore)
    {
        this.operationalStateStore = operationalStateStore;
    }

    public UpgradeJob Create(
        string currentVersion,
        string targetVersion,
        UpgradeJobType upgradeType,
        IReadOnlyList<string> selectedDeployments,
        IReadOnlyList<CustomPatchImageMapping>? customImages = null,
        UpgradeUserInfo? initiatedBy = null)
    {
        var job = new UpgradeJob
        {
            Id = Guid.NewGuid().ToString("N"),
            CurrentVersion = currentVersion,
            TargetVersion = targetVersion,
            UpgradeType = upgradeType,
            SelectedDeployments = selectedDeployments.Count > 0
                ? selectedDeployments.ToArray()
                : ["All applicable Bold BI deployments"],
            CustomImages = customImages?.ToArray() ?? Array.Empty<CustomPatchImageMapping>(),
            InitiatedBy = initiatedBy ?? UpgradeUserInfo.System(),
            StartedAt = DateTimeOffset.UtcNow,
            Stages =
            [
                CreateStage(
                    UpgradeJobStageName.PreUpgradeValidation,
                    "Pre-upgrade Playwright validation",
                    [
                        CreateOperation("pre-validation-start", "Started Playwright validation"),
                        CreateOperation("pre-validation-tests", "Executed validation tests"),
                        CreateOperation("pre-validation-complete", "Validation completed")
                    ]),
                CreateStage(
                    UpgradeJobStageName.DatabaseBackup,
                    "Database backup",
                    [
                        CreateOperation("database-discovery", "Identified affected databases"),
                        CreateOperation("database-impact", "Analyzed database impact"),
                        CreateOperation("table-discovery", "Identified affected tables"),
                        CreateOperation("database-backup", "Backed up affected databases")
                    ]),
                CreateStage(
                    UpgradeJobStageName.KubernetesImageUpgrade,
                    "Kubernetes image upgrade",
                    [
                        CreateOperation("kubernetes-images", "Retrieved target image details"),
                        CreateOperation("kubernetes-deployments", "Updated applicable deployment images")
                    ]),
                CreateStage(
                    UpgradeJobStageName.PostUpgradeValidation,
                    "Post-upgrade Playwright validation",
                    [
                        CreateOperation("post-validation-wait", "Waiting for upgrade completion"),
                        CreateOperation("post-validation-tests", "Executed validation tests"),
                        CreateOperation("post-validation-complete", "Validation completed")
                    ]),
                CreateStage(
                    UpgradeJobStageName.AutomaticRollback,
                    "Automatic rollback",
                    [
                        CreateOperation("rollback-database", "Restored database backup"),
                        CreateOperation("rollback-kubernetes", "Restored previous deployment images")
                    ])
            ]
        };

        jobs[job.Id] = job;
        operationalStateStore.SaveJob(Clone(job));
        return Clone(job);
    }

    public UpgradeJob CreateRollback(
        string currentVersion,
        string targetVersion,
        IReadOnlyList<string> selectedDeployments,
        UpgradeUserInfo? initiatedBy = null)
    {
        var job = new UpgradeJob
        {
            Id = $"manual-rollback-{Guid.NewGuid():N}",
            CurrentVersion = currentVersion,
            TargetVersion = targetVersion,
            UpgradeType = UpgradeJobType.Rollback,
            SelectedDeployments = selectedDeployments.Count > 0
                ? selectedDeployments.ToArray()
                : ["All recorded rollback targets"],
            CustomImages = Array.Empty<CustomPatchImageMapping>(),
            InitiatedBy = initiatedBy ?? UpgradeUserInfo.System(),
            StartedAt = DateTimeOffset.UtcNow,
            Stages =
            [
                CreateStage(
                    UpgradeJobStageName.RollbackDatabaseRestore,
                    "Restore database",
                    [
                        CreateOperation("rollback-database", "Restore affected database tables")
                    ]),
                CreateStage(
                    UpgradeJobStageName.RollbackImageRevert,
                    "Revert Kubernetes image tags",
                    selectedDeployments.Count > 0
                        ? selectedDeployments
                            .Select(deployment => CreateOperation(KubernetesRollbackImageOperationId(deployment), $"{deployment} image reverted"))
                            .ToList()
                        : [CreateOperation("rollback-images", "Revert recorded deployment images")]),
                CreateStage(
                    UpgradeJobStageName.RollbackRolloutWait,
                    "Wait for Kubernetes rollout completion",
                    selectedDeployments.Count > 0
                        ? selectedDeployments
                            .Select(deployment => CreateOperation(KubernetesRollbackRolloutOperationId(deployment), $"{deployment} rollout completed"))
                            .ToList()
                        : [CreateOperation("rollback-rollout", "Wait for deployment rollouts")]),
                CreateStage(
                    UpgradeJobStageName.RollbackHealthVerification,
                    "Verify deployment health",
                    [
                        CreateOperation("rollback-health", "Verify rollout health")
                    ]),
                CreateStage(
                    UpgradeJobStageName.RollbackComplete,
                    "Complete rollback",
                    [
                        CreateOperation("rollback-complete", "Finalize rollback state")
                    ])
            ]
        };

        jobs[job.Id] = job;
        operationalStateStore.SaveJob(Clone(job));
        return Clone(job);
    }

    public bool TryGet(string jobId, out UpgradeJob job)
    {
        lock (syncRoot)
        {
            if (jobs.TryGetValue(jobId, out var storedJob))
            {
                job = Clone(storedJob);
                return true;
            }
        }

        if (operationalStateStore.TryGetJob(jobId, out var persistedJob))
        {
            jobs[jobId] = Clone(persistedJob);
            job = Clone(persistedJob);
            return true;
        }

        job = null!;
        return false;
    }

    public bool TryGetLatestActive(out UpgradeJob job)
    {
        lock (syncRoot)
        {
            var activeJob = jobs.Values
                .Where(IsActive)
                .OrderByDescending(item => item.StartedAt)
                .FirstOrDefault();

            if (activeJob is not null)
            {
                job = Clone(activeJob);
                return true;
            }
        }

        if (operationalStateStore.TryGetLatestActiveJob(out var persistedJob))
        {
            jobs[persistedJob.Id] = Clone(persistedJob);
            job = Clone(persistedJob);
            return true;
        }

        job = null!;
        return false;
    }

    public void MarkRunning(string jobId)
    {
        Update(jobId, job =>
        {
            job.Status = UpgradeJobStatus.Running;
        });
    }

    public void MarkStageRunning(string jobId, UpgradeJobStageName stageName, string? message = null)
    {
        Update(jobId, job =>
        {
            job.Status = UpgradeJobStatus.Running;
            job.CurrentStage = stageName;
            var stage = GetStage(job, stageName);
            stage.Status = UpgradeJobStageStatus.Running;
            stage.StartedAt ??= DateTimeOffset.UtcNow;
            stage.CompletedAt = null;
            stage.Message = message;
            stage.SafeErrorDetails = null;
        });
    }

    public void MarkStageSucceeded(string jobId, UpgradeJobStageName stageName, string? message = null)
    {
        Update(jobId, job =>
        {
            var now = DateTimeOffset.UtcNow;
            var stage = GetStage(job, stageName);
            stage.Status = UpgradeJobStageStatus.Succeeded;
            stage.CompletedAt = now;
            stage.Message = message;
            stage.SafeErrorDetails = null;
            CompletePendingOperations(stage, UpgradeJobStageStatus.Succeeded, message);
            if (job.UpgradeType == UpgradeJobType.Rollback && stageName == UpgradeJobStageName.RollbackComplete)
            {
                job.Status = UpgradeJobStatus.RollbackSucceeded;
                job.CompletedAt ??= now;
                job.SafeErrorDetails = null;
            }
        });
    }

    public void MarkStageSkipped(string jobId, UpgradeJobStageName stageName, string? message = null)
    {
        Update(jobId, job =>
        {
            var stage = GetStage(job, stageName);
            stage.Status = UpgradeJobStageStatus.Skipped;
            stage.CompletedAt = DateTimeOffset.UtcNow;
            stage.Message = message;
            stage.SafeErrorDetails = null;
            CompletePendingOperations(stage, UpgradeJobStageStatus.Skipped, message);
        });
    }

    public void MarkStageFailed(string jobId, UpgradeJobStageName stageName, string safeErrorDetails)
    {
        Update(jobId, job =>
        {
            var now = DateTimeOffset.UtcNow;
            var stage = GetStage(job, stageName);
            stage.Status = UpgradeJobStageStatus.Failed;
            stage.CompletedAt = now;
            stage.SafeErrorDetails = safeErrorDetails;
            job.SafeErrorDetails = safeErrorDetails;
            job.CurrentStage = stageName;
            FailRunningOrPendingOperation(stage, safeErrorDetails);
            if (job.UpgradeType == UpgradeJobType.Rollback && stageName == UpgradeJobStageName.RollbackComplete)
            {
                job.Status = UpgradeJobStatus.RollbackFailed;
                job.CompletedAt ??= now;
            }
        });
    }

    public void ReplaceStageOperations(string jobId, UpgradeJobStageName stageName, IReadOnlyList<UpgradeJobOperation> operations)
    {
        Update(jobId, job =>
        {
            var stage = GetStage(job, stageName);
            stage.Operations.Clear();
            stage.Operations.AddRange(operations.Select(CloneOperation));
        });
    }

    public void MarkOperationRunning(string jobId, UpgradeJobStageName stageName, string operationId, string? message = null)
    {
        UpdateOperation(jobId, stageName, operationId, operation =>
        {
            operation.Status = UpgradeJobStageStatus.Running;
            operation.StartedAt ??= DateTimeOffset.UtcNow;
            operation.CompletedAt = null;
            operation.Message = message;
            operation.SafeErrorDetails = null;
        });
    }

    public void MarkOperationSucceeded(string jobId, UpgradeJobStageName stageName, string operationId, string? message = null)
    {
        UpdateOperation(jobId, stageName, operationId, operation =>
        {
            operation.Status = UpgradeJobStageStatus.Succeeded;
            operation.StartedAt ??= DateTimeOffset.UtcNow;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Message = message;
            operation.SafeErrorDetails = null;
        });
    }

    public void MarkOperationSkipped(string jobId, UpgradeJobStageName stageName, string operationId, string? message = null)
    {
        UpdateOperation(jobId, stageName, operationId, operation =>
        {
            operation.Status = UpgradeJobStageStatus.Skipped;
            operation.StartedAt ??= DateTimeOffset.UtcNow;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Message = message;
            operation.SafeErrorDetails = null;
        });
    }

    public void MarkOperationFailed(string jobId, UpgradeJobStageName stageName, string operationId, string safeErrorDetails)
    {
        UpdateOperation(jobId, stageName, operationId, operation =>
        {
            operation.Status = UpgradeJobStageStatus.Failed;
            operation.StartedAt ??= DateTimeOffset.UtcNow;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.SafeErrorDetails = safeErrorDetails;
        });
    }

    public void MarkRollbackAvailable(string jobId, string rollbackId)
    {
        Update(jobId, job =>
        {
            job.RollbackAvailable = true;
            job.RollbackId = rollbackId;
        });
    }

    public void Complete(string jobId, UpgradeJobStatus status, string? safeErrorDetails = null)
    {
        Update(jobId, job =>
        {
            job.Status = status;
            job.CompletedAt = DateTimeOffset.UtcNow;
            job.SafeErrorDetails = safeErrorDetails;
        });
    }

    private void Update(string jobId, Action<UpgradeJob> update)
    {
        lock (syncRoot)
        {
            if (jobs.TryGetValue(jobId, out var job))
            {
                update(job);
                operationalStateStore.SaveJob(Clone(job));
            }
        }
    }

    private static bool IsActive(UpgradeJob job)
    {
        return job.Status is UpgradeJobStatus.Queued or UpgradeJobStatus.Running;
    }

    private static UpgradeJobStage GetStage(UpgradeJob job, UpgradeJobStageName stageName)
    {
        return job.Stages.First(stage => stage.Name == stageName);
    }

    private void UpdateOperation(string jobId, UpgradeJobStageName stageName, string operationId, Action<UpgradeJobOperation> update)
    {
        Update(jobId, job =>
        {
            var stage = GetStage(job, stageName);
            var operation = stage.Operations.FirstOrDefault(item => string.Equals(item.Id, operationId, StringComparison.OrdinalIgnoreCase));
            if (operation is null)
            {
                operation = CreateOperation(operationId, operationId);
                stage.Operations.Add(operation);
            }

            update(operation);
        });
    }

    private static UpgradeJobStage CreateStage(UpgradeJobStageName name, string displayName, IReadOnlyList<UpgradeJobOperation> operations)
    {
        return new UpgradeJobStage
        {
            Name = name,
            DisplayName = displayName,
            Operations = operations.Select(CloneOperation).ToList()
        };
    }

    private static UpgradeJobOperation CreateOperation(string id, string name)
    {
        return new UpgradeJobOperation
        {
            Id = id,
            Name = name
        };
    }

    public static string KubernetesRollbackImageOperationId(string deploymentName)
    {
        return $"rollback-image-{NormalizeOperationId(deploymentName)}";
    }

    public static string KubernetesRollbackRolloutOperationId(string deploymentName)
    {
        return $"rollback-rollout-{NormalizeOperationId(deploymentName)}";
    }

    private static string NormalizeOperationId(string value)
    {
        var normalized = System.Text.RegularExpressions.Regex.Replace(value.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        return string.IsNullOrWhiteSpace(normalized) ? "unknown" : normalized;
    }

    private static void CompletePendingOperations(UpgradeJobStage stage, UpgradeJobStageStatus status, string? message)
    {
        foreach (var operation in stage.Operations.Where(operation => operation.Status is UpgradeJobStageStatus.Pending or UpgradeJobStageStatus.Running))
        {
            operation.Status = status;
            operation.StartedAt ??= stage.StartedAt ?? DateTimeOffset.UtcNow;
            operation.CompletedAt = DateTimeOffset.UtcNow;
            operation.Message ??= message;
        }
    }

    private static void FailRunningOrPendingOperation(UpgradeJobStage stage, string safeErrorDetails)
    {
        var operation = stage.Operations.FirstOrDefault(item => item.Status == UpgradeJobStageStatus.Running)
            ?? stage.Operations.FirstOrDefault(item => item.Status == UpgradeJobStageStatus.Pending);
        if (operation is null)
        {
            return;
        }

        operation.Status = UpgradeJobStageStatus.Failed;
        operation.StartedAt ??= stage.StartedAt ?? DateTimeOffset.UtcNow;
        operation.CompletedAt = DateTimeOffset.UtcNow;
        operation.SafeErrorDetails = safeErrorDetails;
    }

    private static UpgradeJob Clone(UpgradeJob job)
    {
        return new UpgradeJob
        {
            Id = job.Id,
            CurrentVersion = job.CurrentVersion,
            TargetVersion = job.TargetVersion,
            UpgradeType = job.UpgradeType,
            SelectedDeployments = job.SelectedDeployments.ToArray(),
            CustomImages = job.CustomImages.ToArray(),
            InitiatedBy = job.InitiatedBy,
            CurrentStage = job.CurrentStage,
            Status = job.Status,
            StartedAt = job.StartedAt,
            CompletedAt = job.CompletedAt,
            SafeErrorDetails = job.SafeErrorDetails,
            RollbackAvailable = job.RollbackAvailable,
            RollbackId = job.RollbackId,
            Stages = job.Stages.Select(stage => new UpgradeJobStage
            {
                Name = stage.Name,
                DisplayName = stage.DisplayName,
                Status = stage.Status,
                StartedAt = stage.StartedAt,
                CompletedAt = stage.CompletedAt,
                Message = stage.Message,
                SafeErrorDetails = stage.SafeErrorDetails,
                Operations = stage.Operations.Select(CloneOperation).ToList()
            }).ToList()
        };
    }

    private static UpgradeJobOperation CloneOperation(UpgradeJobOperation operation)
    {
        return new UpgradeJobOperation
        {
            Id = operation.Id,
            Name = operation.Name,
            Status = operation.Status,
            StartedAt = operation.StartedAt,
            CompletedAt = operation.CompletedAt,
            Message = operation.Message,
            SafeErrorDetails = operation.SafeErrorDetails
        };
    }
}
