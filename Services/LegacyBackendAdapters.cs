using System.Text.RegularExpressions;
using BoldBI.UpgradeCenter.Models;
using Legacy = Bold.UpgradeCenter.Services;
using Mvc = BoldBI.UpgradeCenter.Services;

namespace BoldBI.UpgradeCenter.Services;

public sealed class LegacyConfigurationProviderAdapter : Legacy.IUpgradeCenterConfigurationProvider
{
    private readonly Mvc.IUpgradeCenterConfigurationProvider configurationProvider;

    public LegacyConfigurationProviderAdapter(Mvc.IUpgradeCenterConfigurationProvider configurationProvider)
    {
        this.configurationProvider = configurationProvider;
    }

    public async Task<Legacy.UpgradeCenterRuntimeConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        var configuration = await configurationProvider.GetConfigurationAsync(cancellationToken);
        return new Legacy.UpgradeCenterRuntimeConfiguration(
            configuration.IdpBaseUrl,
            configuration.BiBaseUrl,
            configuration.BoldBiVersion,
            configuration.EncryptedMasterDatabaseConnectionString,
            configuration.MasterDatabaseServerType,
            configuration.MachineKeyDecryptionKey,
            configuration.InternalAppClientId,
            configuration.InternalAppClientSecret);
    }
}

public sealed class LegacyProductVersionProviderAdapter : Legacy.IProductVersionProvider
{
    private readonly Mvc.IProductVersionProvider productVersionProvider;

    public LegacyProductVersionProviderAdapter(Mvc.IProductVersionProvider productVersionProvider)
    {
        this.productVersionProvider = productVersionProvider;
    }

    public async Task<Legacy.ProductVersionInfo> GetCurrentVersionAsync(CancellationToken cancellationToken = default)
    {
        var version = await productVersionProvider.GetCurrentVersionAsync(cancellationToken);
        return new Legacy.ProductVersionInfo(version.ProductName, version.Version, version.Source);
    }
}

public sealed class LegacyReleaseVersionProviderAdapter : Legacy.IReleaseVersionProvider
{
    private readonly Mvc.IReleaseVersionProvider releaseVersionProvider;

    public LegacyReleaseVersionProviderAdapter(Mvc.IReleaseVersionProvider releaseVersionProvider)
    {
        this.releaseVersionProvider = releaseVersionProvider;
    }

    public async Task<Legacy.ReleaseVersionSummary> GetReleaseVersionsAsync(string currentVersion, CancellationToken cancellationToken = default)
    {
        var summary = await releaseVersionProvider.GetReleaseVersionsAsync(currentVersion, cancellationToken);
        return new Legacy.ReleaseVersionSummary(
            summary.Versions
                .Select(version => new Legacy.ReleaseVersionInfo(
                    version.Version,
                    version.TagName,
                    version.Name,
                    version.Url,
                    version.PublishedAt))
                .ToArray(),
            summary.LatestVersion is null
                ? null
                : new Legacy.ReleaseVersionInfo(
                    summary.LatestVersion.Version,
                    summary.LatestVersion.TagName,
                    summary.LatestVersion.Name,
                    summary.LatestVersion.Url,
                    summary.LatestVersion.PublishedAt),
            summary.ComparisonState switch
            {
                VersionComparisonState.UpToDate => Legacy.VersionComparisonState.UpToDate,
                VersionComparisonState.UpdateAvailable => Legacy.VersionComparisonState.UpdateAvailable,
                VersionComparisonState.CurrentNewerThanAvailable => Legacy.VersionComparisonState.CurrentNewerThanAvailable,
                _ => Legacy.VersionComparisonState.Unknown
            },
            summary.ErrorMessage);
    }
}

public sealed class LegacyUpgradeServiceAdapter : IUpgradeService
{
    private static readonly Regex ImageReferencePattern = new(
        @"^(?=.{1,255}$)([a-zA-Z0-9.-]+(?::[0-9]+)?/)?[a-z0-9]+([._/-][a-z0-9]+)*:[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$",
        RegexOptions.Compiled);

    private readonly Legacy.IUpgradeJobStore legacyJobStore;
    private readonly Legacy.IUpgradeJobRunner legacyJobRunner;
    private readonly Legacy.IUpgradeRollbackStore legacyRollbackStore;
    private readonly Legacy.IUpgradeRollbackJobRunner legacyRollbackJobRunner;
    private readonly Legacy.IProductVersionProvider productVersionProvider;
    private readonly Legacy.IReleaseVersionProvider releaseVersionProvider;
    private readonly Legacy.IUpgradeHistoryStore historyStore;
    private readonly Legacy.IUpgradeUserContextProvider userContextProvider;

    public LegacyUpgradeServiceAdapter(
        Legacy.IUpgradeJobStore legacyJobStore,
        Legacy.IUpgradeJobRunner legacyJobRunner,
        Legacy.IUpgradeRollbackStore legacyRollbackStore,
        Legacy.IUpgradeRollbackJobRunner legacyRollbackJobRunner,
        Legacy.IProductVersionProvider productVersionProvider,
        Legacy.IReleaseVersionProvider releaseVersionProvider,
        Legacy.IUpgradeHistoryStore historyStore,
        Legacy.IUpgradeUserContextProvider userContextProvider)
    {
        this.legacyJobStore = legacyJobStore;
        this.legacyJobRunner = legacyJobRunner;
        this.legacyRollbackStore = legacyRollbackStore;
        this.legacyRollbackJobRunner = legacyRollbackJobRunner;
        this.productVersionProvider = productVersionProvider;
        this.releaseVersionProvider = releaseVersionProvider;
        this.historyStore = historyStore;
        this.userContextProvider = userContextProvider;
    }

    public async Task<List<Release>> GetAvailableReleasesAsync(string installedVersion, CancellationToken ct = default)
    {
        var summary = await releaseVersionProvider.GetReleaseVersionsAsync(installedVersion, ct);
        var ordered = summary.Versions
            .OrderBy(version => Legacy.ProductVersionComparer.Parse(version.Version), Legacy.ProductVersionPartsComparer.Instance)
            .ToList();
        var recommendedNext = ordered.FirstOrDefault()?.Version;

        return summary.Versions
            .Select(version => new Release
            {
                Version = version.Version,
                ReleasedOn = version.PublishedAt?.UtcDateTime ?? DateTime.MinValue,
                ReleaseNotesUrl = version.Url,
                IsRecommendedNext = string.Equals(version.Version, recommendedNext, StringComparison.OrdinalIgnoreCase),
                RequiresIntermediateUpgrade = false,
                HasSchemaChanges = false,
                Type = ReleaseType.Standard
            })
            .ToList();
    }

    public Task<PatchValidationResult> ValidatePatchImageAsync(string imageReference, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            return Task.FromResult(new PatchValidationResult
            {
                IsValid = false,
                ImageReference = imageReference ?? string.Empty,
                ErrorMessage = "Image reference cannot be empty."
            });
        }

        if (!ImageReferencePattern.IsMatch(imageReference.Trim()))
        {
            return Task.FromResult(new PatchValidationResult
            {
                IsValid = false,
                ImageReference = imageReference,
                ErrorMessage = "Image reference must include a valid repository/name and tag."
            });
        }

        return Task.FromResult(new PatchValidationResult
        {
            IsValid = true,
            ImageReference = imageReference,
            ParsedVersion = imageReference.Split(':').LastOrDefault()
        });
    }

    public async Task<UpgradeJob> StartUpgradeAsync(string targetVersion, string initiatedBy, CancellationToken ct = default)
    {
        if (legacyJobStore.TryGetLatestActive(out var active))
        {
            throw new InvalidOperationException($"Another upgrade or rollback operation is already running. Active job: {active.Id}.");
        }

        var currentVersion = await productVersionProvider.GetCurrentVersionAsync(ct);
        var user = userContextProvider.GetCurrentUser();
        var job = legacyJobStore.Create(
            currentVersion.Version,
            targetVersion,
            Legacy.UpgradeJobType.AvailableRelease,
            ["All applicable Bold BI deployments"],
            null,
            user);

        await historyStore.SaveStartedAsync(CreateHistoryRecord(job), ct);
        legacyJobRunner.Start(job.Id);
        return LegacyUpgradeJobMapper.ToMvcJob(job);
    }

    public async Task<UpgradeJob> StartCustomPatchUpgradeAsync(string imageReference, string initiatedBy, CancellationToken ct = default)
    {
        if (legacyJobStore.TryGetLatestActive(out var active))
        {
            throw new InvalidOperationException($"Another upgrade or rollback operation is already running. Active job: {active.Id}.");
        }

        var currentVersion = await productVersionProvider.GetCurrentVersionAsync(ct);
        var targetVersion = imageReference.Split(':').LastOrDefault() ?? imageReference;
        var user = userContextProvider.GetCurrentUser();
        var job = legacyJobStore.Create(
            currentVersion.Version,
            targetVersion,
            Legacy.UpgradeJobType.CustomVersionOrPatch,
            ["All applicable Bold BI deployments"],
            Array.Empty<Legacy.CustomPatchImageMapping>(),
            user);

        await historyStore.SaveStartedAsync(CreateHistoryRecord(job), ct);
        legacyJobRunner.Start(job.Id);
        return LegacyUpgradeJobMapper.ToMvcJob(job);
    }

    public async Task<UpgradeJob> StartRollbackAsync(string upgradeJobId, string initiatedBy, CancellationToken ct = default)
    {
        if (legacyJobStore.TryGetLatestActive(out var active))
        {
            throw new InvalidOperationException($"Another upgrade or rollback operation is already running. Active job: {active.Id}.");
        }

        var rollbackEntry = legacyRollbackStore.GetLatest();
        if (rollbackEntry is null ||
            !rollbackEntry.CanExecute ||
            !string.Equals(rollbackEntry.UpgradeJobId, upgradeJobId, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Rollback is not available for the selected upgrade job.");
        }

        if (!legacyRollbackStore.TryBeginRestore(rollbackEntry.Id, out rollbackEntry, out var message))
        {
            throw new InvalidOperationException(message);
        }

        var user = userContextProvider.GetCurrentUser();
        var rollbackDeployments = rollbackEntry.KubernetesResult?.PreviousImages
            .Select(image => image.DeploymentName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        var rollbackJob = legacyJobStore.CreateRollback(
            rollbackEntry.TargetVersion ?? rollbackEntry.BackupResult.SelectedVersion,
            rollbackEntry.PreviousVersion ?? "Rollback point",
            rollbackDeployments,
            user);

        await historyStore.SaveStartedAsync(new Legacy.UpgradeHistoryRecord
        {
            JobId = rollbackJob.Id,
            ParentJobId = rollbackEntry.UpgradeJobId,
            OperationType = Legacy.UpgradeHistoryOperationTypes.ManualRollback,
            PreviousVersion = rollbackEntry.TargetVersion ?? rollbackEntry.BackupResult.SelectedVersion,
            TargetVersion = rollbackEntry.PreviousVersion ?? "Rollback point",
            UpgradeType = "Rollback",
            Status = "Running",
            StartedAt = DateTimeOffset.UtcNow,
            InitiatedByUserId = user.UserId,
            InitiatedByName = user.DisplayName,
            InitiatedByEmail = user.Email,
            InitiatedByRole = user.Role,
            RollbackMode = Legacy.UpgradeHistoryRollbackModes.Manual,
            RollbackJobId = rollbackJob.Id
        }, ct);

        legacyRollbackJobRunner.Start(rollbackJob.Id, rollbackEntry.Id);
        return LegacyUpgradeJobMapper.ToMvcJob(rollbackJob);
    }

    private static Legacy.UpgradeHistoryRecord CreateHistoryRecord(Legacy.UpgradeJob job)
    {
        return new Legacy.UpgradeHistoryRecord
        {
            JobId = job.Id,
            OperationType = job.UpgradeType == Legacy.UpgradeJobType.CustomVersionOrPatch
                ? Legacy.UpgradeHistoryOperationTypes.CustomVersionOrPatchUpgrade
                : Legacy.UpgradeHistoryOperationTypes.StandardReleaseUpgrade,
            PreviousVersion = job.CurrentVersion,
            TargetVersion = job.TargetVersion,
            UpgradeType = job.UpgradeType.ToString(),
            Status = job.Status.ToString(),
            StartedAt = job.StartedAt,
            InitiatedByUserId = job.InitiatedBy.UserId,
            InitiatedByName = job.InitiatedBy.DisplayName,
            InitiatedByEmail = job.InitiatedBy.Email,
            InitiatedByRole = job.InitiatedBy.Role
        };
    }
}

public sealed class LegacyUpgradeJobStoreAdapter : IUpgradeJobStore
{
    private readonly Legacy.IUpgradeJobStore legacyJobStore;
    private readonly Legacy.IUpgradeRollbackStore legacyRollbackStore;
    private readonly Legacy.IUpgradeHistoryStore historyStore;

    public LegacyUpgradeJobStoreAdapter(
        Legacy.IUpgradeJobStore legacyJobStore,
        Legacy.IUpgradeRollbackStore legacyRollbackStore,
        Legacy.IUpgradeHistoryStore historyStore)
    {
        this.legacyJobStore = legacyJobStore;
        this.legacyRollbackStore = legacyRollbackStore;
        this.historyStore = historyStore;
    }

    public Task SaveJobAsync(UpgradeJob job, CancellationToken ct = default)
    {
        throw new NotSupportedException("Upgrade jobs are created by the legacy backend runner.");
    }

    public Task UpdateJobAsync(UpgradeJob job, CancellationToken ct = default)
    {
        throw new NotSupportedException("Upgrade jobs are updated by the legacy backend runner.");
    }

    public Task<UpgradeJob?> GetJobAsync(string jobId, CancellationToken ct = default)
    {
        return Task.FromResult(legacyJobStore.TryGet(jobId, out var job)
            ? LegacyUpgradeJobMapper.ToMvcJob(job)
            : null);
    }

    public Task<UpgradeJob?> GetActiveJobAsync(CancellationToken ct = default)
    {
        return Task.FromResult(legacyJobStore.TryGetLatestActive(out var job)
            ? LegacyUpgradeJobMapper.ToMvcJob(job)
            : null);
    }

    public Task<UpgradeJob?> GetMostRecentRollbackableJobAsync(CancellationToken ct = default)
    {
        var rollbackEntry = legacyRollbackStore.GetLatest();
        if (rollbackEntry is null ||
            !rollbackEntry.CanExecute ||
            string.IsNullOrWhiteSpace(rollbackEntry.UpgradeJobId))
        {
            return Task.FromResult<UpgradeJob?>(null);
        }

        return Task.FromResult(legacyJobStore.TryGet(rollbackEntry.UpgradeJobId, out var job)
            ? LegacyUpgradeJobMapper.ToMvcJob(job)
            : null);
    }

    public async Task<List<UpgradeJob>> GetAllJobsAsync(int maxCount = 50, CancellationToken ct = default)
    {
        var history = await historyStore.ListAsync(maxCount, ct);
        return history.Select(LegacyUpgradeJobMapper.ToMvcJob).ToList();
    }

    public async Task<List<UpgradeHistoryEntry>> GetHistoryAsync(int maxCount = 20, CancellationToken ct = default)
    {
        var history = await historyStore.ListAsync(maxCount, ct);
        return history.Select(record => new UpgradeHistoryEntry
        {
            JobId = record.JobId,
            Label = $"v{record.PreviousVersion ?? "-"} -> v{record.TargetVersion ?? "-"}",
            InitiatedBy = string.IsNullOrWhiteSpace(record.InitiatedByName) ? record.InitiatedByUserId : record.InitiatedByName,
            Status = LegacyUpgradeJobMapper.ToMvcStatus(record.Status, record.OperationType),
            CompletedAt = record.CompletedAt?.UtcDateTime ?? record.StartedAt.UtcDateTime
        }).ToList();
    }
}

internal static class LegacyUpgradeJobMapper
{
    public static UpgradeJob ToMvcJob(Legacy.UpgradeJob job)
    {
        return new UpgradeJob
        {
            JobId = job.Id,
            FromVersion = job.CurrentVersion,
            ToVersion = job.TargetVersion,
            Status = ToMvcStatus(job.Status, job.UpgradeType),
            StartedAt = job.StartedAt.UtcDateTime,
            CompletedAt = job.CompletedAt?.UtcDateTime,
            InitiatedBy = job.InitiatedBy.DisplayName,
            ReleaseType = job.UpgradeType == Legacy.UpgradeJobType.CustomVersionOrPatch ? ReleaseType.CustomPatch : ReleaseType.Standard,
            RevertsJobId = job.UpgradeType == Legacy.UpgradeJobType.Rollback ? job.RollbackId ?? job.Id : null,
            RollbackJobId = job.RollbackAvailable ? null : job.RollbackId,
            Stages = job.Stages
                .Where(stage => ShouldRenderStage(stage, job.UpgradeType))
                .Select(ToMvcStage)
                .ToList()
        };
    }

    public static UpgradeJob ToMvcJob(Legacy.UpgradeHistoryRecord record)
    {
        return new UpgradeJob
        {
            JobId = record.JobId,
            FromVersion = record.PreviousVersion ?? string.Empty,
            ToVersion = record.TargetVersion ?? string.Empty,
            Status = ToMvcStatus(record.Status, record.OperationType),
            StartedAt = record.StartedAt.UtcDateTime,
            CompletedAt = record.CompletedAt?.UtcDateTime,
            InitiatedBy = string.IsNullOrWhiteSpace(record.InitiatedByName) ? record.InitiatedByUserId : record.InitiatedByName,
            ReleaseType = string.Equals(record.OperationType, Legacy.UpgradeHistoryOperationTypes.CustomVersionOrPatchUpgrade, StringComparison.OrdinalIgnoreCase)
                ? ReleaseType.CustomPatch
                : ReleaseType.Standard,
            RevertsJobId = record.ParentJobId
        };
    }

    public static UpgradeJobStatus ToMvcStatus(string status, string? operationType)
    {
        return status switch
        {
            "Succeeded" => UpgradeJobStatus.Completed,
            "Failed" => UpgradeJobStatus.Failed,
            "RollbackSucceeded" => UpgradeJobStatus.RolledBack,
            "RollbackFailed" => UpgradeJobStatus.Failed,
            "Running" when string.Equals(operationType, Legacy.UpgradeHistoryOperationTypes.ManualRollback, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(operationType, Legacy.UpgradeHistoryOperationTypes.AutomaticRollback, StringComparison.OrdinalIgnoreCase) => UpgradeJobStatus.RollingBack,
            "Running" => UpgradeJobStatus.Running,
            _ => UpgradeJobStatus.Pending
        };
    }

    private static UpgradeJobStatus ToMvcStatus(Legacy.UpgradeJobStatus status, Legacy.UpgradeJobType upgradeType)
    {
        return status switch
        {
            Legacy.UpgradeJobStatus.Succeeded => UpgradeJobStatus.Completed,
            Legacy.UpgradeJobStatus.Failed => UpgradeJobStatus.Failed,
            Legacy.UpgradeJobStatus.RollbackSucceeded => UpgradeJobStatus.RolledBack,
            Legacy.UpgradeJobStatus.RollbackFailed => UpgradeJobStatus.Failed,
            Legacy.UpgradeJobStatus.Running when upgradeType == Legacy.UpgradeJobType.Rollback => UpgradeJobStatus.RollingBack,
            Legacy.UpgradeJobStatus.Running => UpgradeJobStatus.Running,
            _ => UpgradeJobStatus.Pending
        };
    }

    private static bool ShouldRenderStage(Legacy.UpgradeJobStage stage, Legacy.UpgradeJobType upgradeType)
    {
        if (upgradeType == Legacy.UpgradeJobType.Rollback)
        {
            return stage.Name is Legacy.UpgradeJobStageName.RollbackDatabaseRestore
                or Legacy.UpgradeJobStageName.RollbackImageRevert
                or Legacy.UpgradeJobStageName.RollbackRolloutWait
                or Legacy.UpgradeJobStageName.RollbackHealthVerification
                or Legacy.UpgradeJobStageName.RollbackComplete;
        }

        return stage.Name is Legacy.UpgradeJobStageName.PreUpgradeValidation
            or Legacy.UpgradeJobStageName.DatabaseBackup
            or Legacy.UpgradeJobStageName.KubernetesImageUpgrade
            or Legacy.UpgradeJobStageName.PostUpgradeValidation;
    }

    private static JobStage ToMvcStage(Legacy.UpgradeJobStage stage)
    {
        return new JobStage
        {
            Name = ToMvcStageName(stage),
            Meta = stage.SafeErrorDetails ?? stage.Message,
            Status = ToMvcStageStatus(stage.Status),
            Duration = stage.StartedAt.HasValue && stage.CompletedAt.HasValue
                ? stage.CompletedAt.Value - stage.StartedAt.Value
                : null,
            Checks = stage.Operations.Select(operation => new StageCheckResult
            {
                Description = BuildOperationDescription(operation),
                Passed = operation.Status is Legacy.UpgradeJobStageStatus.Succeeded or Legacy.UpgradeJobStageStatus.Skipped
            }).ToList(),
            LogLines = stage.Operations
                .Where(operation => !string.IsNullOrWhiteSpace(operation.Message) || !string.IsNullOrWhiteSpace(operation.SafeErrorDetails))
                .Select(operation => $"{operation.Name}: {operation.SafeErrorDetails ?? operation.Message}")
                .ToList()
        };
    }

    private static string ToMvcStageName(Legacy.UpgradeJobStage stage)
    {
        return stage.Name switch
        {
            Legacy.UpgradeJobStageName.PreUpgradeValidation => "Pre-Upgrade Validation",
            Legacy.UpgradeJobStageName.DatabaseBackup => "Schema Backup",
            Legacy.UpgradeJobStageName.KubernetesImageUpgrade => "Upgrade",
            Legacy.UpgradeJobStageName.PostUpgradeValidation => "Post-Upgrade Validation",
            _ => stage.DisplayName
        };
    }

    private static StageStatus ToMvcStageStatus(Legacy.UpgradeJobStageStatus status)
    {
        return status switch
        {
            Legacy.UpgradeJobStageStatus.Running => StageStatus.Running,
            Legacy.UpgradeJobStageStatus.Succeeded => StageStatus.Passed,
            Legacy.UpgradeJobStageStatus.Failed => StageStatus.Failed,
            Legacy.UpgradeJobStageStatus.Skipped => StageStatus.Skipped,
            _ => StageStatus.Pending
        };
    }

    private static string BuildOperationDescription(Legacy.UpgradeJobOperation operation)
    {
        var status = operation.Status.ToString();
        var message = operation.SafeErrorDetails ?? operation.Message;
        return string.IsNullOrWhiteSpace(message)
            ? $"{operation.Name} - {status}"
            : $"{operation.Name} - {status}: {message}";
    }
}
