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
            CompleteLogLines = BuildCompleteLogLines(job),
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
            CompleteLogLines =
            [
                $"Job ID: {record.JobId}",
                $"Operation type: {record.OperationType}",
                $"Started: {FormatTimestamp(record.StartedAt)}",
                $"Completed: {FormatTimestamp(record.CompletedAt)}",
                $"Final status: {record.Status}",
                string.IsNullOrWhiteSpace(record.FailureStage) ? "Failure stage: None" : $"Failure stage: {record.FailureStage}"
            ]
        };
    }

    public static UiUpgradeJobStatus ToViewModelStatus(string status, string? operationType)
    {
        return status switch
        {
            "Succeeded" => UiUpgradeJobStatus.Completed,
            "Failed" => UiUpgradeJobStatus.Failed,
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

    private static List<string> BuildCompleteLogLines(UpgradeJob job)
    {
        var lines = new List<string>
        {
            $"Job ID: {job.Id}",
            $"Operation type: {job.UpgradeType}",
            $"Current version: v{job.CurrentVersion}",
            $"Target version: v{job.TargetVersion}",
            $"Started by: {job.InitiatedBy.DisplayName}",
            $"Started: {FormatTimestamp(job.StartedAt)}"
        };

        if (job.CompletedAt.HasValue)
        {
            lines.Add($"Completed: {FormatTimestamp(job.CompletedAt)}");
        }

        lines.Add($"Final status: {job.Status}");

        if (!string.IsNullOrWhiteSpace(job.SafeErrorDetails))
        {
            lines.Add($"Final safe error: {job.SafeErrorDetails}");
        }

        foreach (var stage in job.Stages.Where(stage => ShouldRenderStage(stage, job.UpgradeType)))
        {
            lines.Add(string.Empty);
            lines.Add($"Stage: {ToViewModelStageName(stage)}");
            lines.Add($"Stage status: {stage.Status}");

            if (stage.StartedAt.HasValue)
            {
                lines.Add($"Stage started: {FormatTimestamp(stage.StartedAt)}");
            }

            if (stage.CompletedAt.HasValue)
            {
                lines.Add($"Stage completed: {FormatTimestamp(stage.CompletedAt)}");
            }

            if (!string.IsNullOrWhiteSpace(stage.Message))
            {
                lines.Add($"Stage message: {stage.Message}");
            }

            if (!string.IsNullOrWhiteSpace(stage.SafeErrorDetails))
            {
                lines.Add($"Stage safe error: {stage.SafeErrorDetails}");
            }

            foreach (var operation in stage.Operations)
            {
                lines.Add($"- {BuildOperationDescription(operation)}");

                if (operation.StartedAt.HasValue)
                {
                    lines.Add($"  Started: {FormatTimestamp(operation.StartedAt)}");
                }

                if (operation.CompletedAt.HasValue)
                {
                    lines.Add($"  Completed: {FormatTimestamp(operation.CompletedAt)}");
                }
            }
        }

        return lines;
    }

    private static string FormatTimestamp(DateTimeOffset? value)
    {
        return value?.UtcDateTime.ToString("dd MMM yyyy, HH:mm:ss 'UTC'") ?? "Not available";
    }
}
