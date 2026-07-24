using BoldBI.UpgradeCenter.Models;

namespace BoldBI.UpgradeCenter.Services;

public sealed class ProductVersionInstallationInfoProvider : IInstallationInfoProvider
{
    private readonly IProductVersionProvider productVersionProvider;
    private readonly IReleaseVersionProvider releaseVersionProvider;
    private readonly IUpgradeJobStore jobStore;

    public ProductVersionInstallationInfoProvider(
        IProductVersionProvider productVersionProvider,
        IReleaseVersionProvider releaseVersionProvider,
        IUpgradeJobStore jobStore)
    {
        this.productVersionProvider = productVersionProvider;
        this.releaseVersionProvider = releaseVersionProvider;
        this.jobStore = jobStore;
    }

    public async Task<InstallationInfo> GetInstallationInfoAsync(CancellationToken ct = default)
    {
        var actualCurrentVersion = await productVersionProvider.GetCurrentVersionAsync(ct);
        var displayVersion = ResolveDisplayVersion(actualCurrentVersion.Version);
        var releases = await releaseVersionProvider.GetReleaseVersionsAsync(displayVersion, ct);

        return new InstallationInfo
        {
            InstalledVersion = displayVersion,
            LatestAvailableVersion = releases.LatestVersion?.Version ?? displayVersion,
            NewerVersionCount = releases.Versions.Count,
            DeploymentTarget = "On-premise / Kubernetes"
        };
    }

    private string ResolveDisplayVersion(string actualCurrentVersion)
    {
        if (jobStore.TryGetLatestActive(out var activeJob))
        {
            return ResolveInProgressDisplayVersion(activeJob, actualCurrentVersion);
        }

        if (!jobStore.TryGetLatest(out var latestJob))
        {
            return actualCurrentVersion;
        }

        return latestJob.Status switch
        {
            UpgradeJobStatus.Succeeded when latestJob.UpgradeType != UpgradeJobType.Rollback => actualCurrentVersion,
            UpgradeJobStatus.RollbackSucceeded => latestJob.UpgradeType == UpgradeJobType.Rollback
                ? latestJob.TargetVersion
                : latestJob.CurrentVersion,
            UpgradeJobStatus.Failed or UpgradeJobStatus.RollbackFailed => latestJob.CurrentVersion,
            _ => actualCurrentVersion
        };
    }

    private static string ResolveInProgressDisplayVersion(UpgradeJob activeJob, string actualCurrentVersion)
    {
        if (activeJob.UpgradeType == UpgradeJobType.Rollback)
        {
            return activeJob.CurrentVersion;
        }

        return activeJob.Status is UpgradeJobStatus.Queued or UpgradeJobStatus.Running
            ? activeJob.CurrentVersion
            : actualCurrentVersion;
    }
}
