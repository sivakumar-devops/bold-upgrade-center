using UiJob = BoldBI.UpgradeCenter.Models.UpgradeJob;
using UiJobStage = BoldBI.UpgradeCenter.Models.JobStage;
using UiReleaseType = BoldBI.UpgradeCenter.Models.ReleaseType;
using UiStageCheckResult = BoldBI.UpgradeCenter.Models.StageCheckResult;
using UiStageStatus = BoldBI.UpgradeCenter.Models.StageStatus;
using UiUpgradeJobStatus = BoldBI.UpgradeCenter.Models.UpgradeJobStatus;

namespace BoldBI.UpgradeCenter.Services;

internal static class UpgradeJobViewModelMapper
{
    public static UiJob ToViewModel(UpgradeJob job)
    {
        return new UiJob
        {
            JobId = job.Id,
            FromVersion = job.CurrentVersion,
            ToVersion = job.TargetVersion,
            Status = ToViewModelStatus(job.Status, job.UpgradeType),
            StartedAt = job.StartedAt.UtcDateTime,
            CompletedAt = job.CompletedAt?.UtcDateTime,
            InitiatedBy = job.InitiatedBy.DisplayName,
            ReleaseType = job.UpgradeType == UpgradeJobType.CustomVersionOrPatch ? UiReleaseType.CustomPatch : UiReleaseType.Standard,
            RevertsJobId = job.UpgradeType == UpgradeJobType.Rollback ? job.RollbackId ?? job.Id : null,
            RollbackJobId = job.RollbackAvailable ? null : job.RollbackId,
            CancellationRequested = job.CancellationRequested,
            CancellationMessage = job.CancellationMessage,
            CompleteLogLines = [],
            Stages = job.Stages
                .Where(stage => ShouldRenderStage(stage, job.UpgradeType))
                .Select(ToViewModelStage)
                .ToList()
        };
    }

    public static UiJob ToViewModel(UpgradeHistoryRecord record)
    {
        return new UiJob
        {
            JobId = record.JobId,
            FromVersion = record.PreviousVersion ?? string.Empty,
            ToVersion = record.TargetVersion ?? string.Empty,
            Status = ToViewModelStatus(record.Status, record.OperationType),
            StartedAt = record.StartedAt.UtcDateTime,
            CompletedAt = record.CompletedAt?.UtcDateTime,
            InitiatedBy = string.IsNullOrWhiteSpace(record.InitiatedByName) ? record.InitiatedByUserId : record.InitiatedByName,
            ReleaseType = string.Equals(record.OperationType, UpgradeHistoryOperationTypes.CustomVersionOrPatchUpgrade, StringComparison.OrdinalIgnoreCase)
                ? UiReleaseType.CustomPatch
                : UiReleaseType.Standard,
            RevertsJobId = record.ParentJobId,
            CompleteLogLines = []
        };
    }

    public static UiUpgradeJobStatus ToViewModelStatus(string status, string? operationType)
    {
        return status switch
        {
            "Succeeded" => UiUpgradeJobStatus.Completed,
            "Failed" => UiUpgradeJobStatus.Failed,
            "Cancelled" => UiUpgradeJobStatus.Cancelled,
            "Cancelling" => UiUpgradeJobStatus.Cancelling,
            "RollbackSucceeded" => UiUpgradeJobStatus.RolledBack,
            "RollbackFailed" => UiUpgradeJobStatus.Failed,
            "Running" when string.Equals(operationType, UpgradeHistoryOperationTypes.ManualRollback, StringComparison.OrdinalIgnoreCase) ||
                           string.Equals(operationType, UpgradeHistoryOperationTypes.AutomaticRollback, StringComparison.OrdinalIgnoreCase) => UiUpgradeJobStatus.RollingBack,
            "Running" => UiUpgradeJobStatus.Running,
            _ => UiUpgradeJobStatus.Pending
        };
    }

    private static UiUpgradeJobStatus ToViewModelStatus(UpgradeJobStatus status, UpgradeJobType upgradeType)
    {
        return status switch
        {
            UpgradeJobStatus.Succeeded => UiUpgradeJobStatus.Completed,
            UpgradeJobStatus.Failed => UiUpgradeJobStatus.Failed,
            UpgradeJobStatus.Cancelled => UiUpgradeJobStatus.Cancelled,
            UpgradeJobStatus.Cancelling => UiUpgradeJobStatus.Cancelling,
            UpgradeJobStatus.RollbackSucceeded => UiUpgradeJobStatus.RolledBack,
            UpgradeJobStatus.RollbackFailed => UiUpgradeJobStatus.Failed,
            UpgradeJobStatus.Running when upgradeType == UpgradeJobType.Rollback => UiUpgradeJobStatus.RollingBack,
            UpgradeJobStatus.Running => UiUpgradeJobStatus.Running,
            _ => UiUpgradeJobStatus.Pending
        };
    }

    private static bool ShouldRenderStage(UpgradeJobStage stage, UpgradeJobType upgradeType)
    {
        if (upgradeType == UpgradeJobType.Rollback)
        {
            return stage.Name is UpgradeJobStageName.RollbackDatabaseRestore
                or UpgradeJobStageName.RollbackImageRevert
                or UpgradeJobStageName.RollbackRolloutWait
                or UpgradeJobStageName.RollbackHealthVerification
                or UpgradeJobStageName.RollbackComplete;
        }

        return stage.Name is UpgradeJobStageName.PreUpgradeValidation
            or UpgradeJobStageName.DatabaseBackup
            or UpgradeJobStageName.KubernetesImageUpgrade
            or UpgradeJobStageName.PostUpgradeValidation;
    }

    private static UiJobStage ToViewModelStage(UpgradeJobStage stage)
    {
        return new UiJobStage
        {
            Name = ToViewModelStageName(stage),
            Meta = stage.SafeErrorDetails ?? stage.Message,
            Status = ToViewModelStageStatus(stage.Status),
            Duration = stage.StartedAt.HasValue && stage.CompletedAt.HasValue
                ? stage.CompletedAt.Value - stage.StartedAt.Value
                : null,
            Checks = stage.Operations.Select(operation => new UiStageCheckResult
            {
                Description = BuildOperationDescription(operation),
                Passed = operation.Status is UpgradeJobStageStatus.Succeeded or UpgradeJobStageStatus.Skipped
            }).ToList(),
            LogLines = stage.Operations
                .Where(operation => !string.IsNullOrWhiteSpace(operation.Message) || !string.IsNullOrWhiteSpace(operation.SafeErrorDetails))
                .Select(operation => $"{operation.Name}: {operation.SafeErrorDetails ?? operation.Message}")
                .ToList()
        };
    }

    private static string ToViewModelStageName(UpgradeJobStage stage)
    {
        return stage.Name switch
        {
            UpgradeJobStageName.PreUpgradeValidation => "Pre-Upgrade Validation",
            UpgradeJobStageName.DatabaseBackup => "Schema Backup",
            UpgradeJobStageName.KubernetesImageUpgrade => "Upgrade",
            UpgradeJobStageName.PostUpgradeValidation => "Post-Upgrade Validation",
            _ => stage.DisplayName
        };
    }

    private static UiStageStatus ToViewModelStageStatus(UpgradeJobStageStatus status)
    {
        return status switch
        {
            UpgradeJobStageStatus.Running => UiStageStatus.Running,
            UpgradeJobStageStatus.Succeeded => UiStageStatus.Passed,
            UpgradeJobStageStatus.Failed => UiStageStatus.Failed,
            UpgradeJobStageStatus.Cancelled => UiStageStatus.Cancelled,
            UpgradeJobStageStatus.Skipped => UiStageStatus.Skipped,
            _ => UiStageStatus.Pending
        };
    }

    private static string BuildOperationDescription(UpgradeJobOperation operation)
    {
        var status = operation.Status.ToString();
        var message = operation.SafeErrorDetails ?? operation.Message;
        return string.IsNullOrWhiteSpace(message)
            ? $"{operation.Name} - {status}"
            : $"{operation.Name} - {status}: {message}";
    }

    private static string FormatTimestamp(DateTimeOffset? value) =>
        value?.UtcDateTime.ToString("dd MMM yyyy, HH:mm:ss 'UTC'") ?? "Not available";
}
