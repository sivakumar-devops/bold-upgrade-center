using Bold.UpgradeCenter.Models;

namespace Bold.UpgradeCenter.Services;

public sealed class ProductVersionInstallationInfoProvider : IInstallationInfoProvider
{
    private readonly IProductVersionProvider productVersionProvider;
    private readonly IReleaseVersionProvider releaseVersionProvider;
    private readonly IUpgradeJobStore jobStore;
    private readonly IUpgradeProductContext productContext;
    private readonly IConfiguration? configuration;

    public ProductVersionInstallationInfoProvider(
        IProductVersionProvider productVersionProvider,
        IReleaseVersionProvider releaseVersionProvider,
        IUpgradeJobStore jobStore,
        IUpgradeProductContext productContext,
        IConfiguration? configuration = null)
    {
        this.productVersionProvider = productVersionProvider;
        this.releaseVersionProvider = releaseVersionProvider;
        this.jobStore = jobStore;
        this.productContext = productContext;
        this.configuration = configuration;
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
            DeploymentTarget = UpgradeExecutionEnvironments.Parse(configuration?["UpgradeCenter:ExecutionEnvironment"]) == UpgradeExecutionEnvironment.Ecs ? "AWS / ECS Fargate" : "On-premise / Kubernetes"
        };
    }

    private string ResolveDisplayVersion(string actualCurrentVersion)
    {
        if (jobStore.TryGetLatestActive(out var activeJob) &&
            string.Equals(
                UpgradeProductDefinitions.Resolve(activeJob.ProductKey).Key,
                productContext.Current.Key,
                StringComparison.OrdinalIgnoreCase))
        {
            return ResolveInProgressDisplayVersion(activeJob, actualCurrentVersion);
        }

        return actualCurrentVersion;
    }

    private static string ResolveInProgressDisplayVersion(UpgradeJob activeJob, string actualCurrentVersion)
    {
        if (activeJob.UpgradeType == UpgradeJobType.Rollback)
        {
            return activeJob.CurrentVersion;
        }

        return activeJob.Status is UpgradeJobStatus.Queued or UpgradeJobStatus.Running or UpgradeJobStatus.Cancelling
            ? activeJob.CurrentVersion
            : actualCurrentVersion;
    }
}
