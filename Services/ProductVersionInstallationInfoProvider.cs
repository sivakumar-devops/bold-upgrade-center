using BoldBI.UpgradeCenter.Models;

namespace BoldBI.UpgradeCenter.Services;

public sealed class ProductVersionInstallationInfoProvider : IInstallationInfoProvider
{
    private readonly IProductVersionProvider productVersionProvider;
    private readonly IReleaseVersionProvider releaseVersionProvider;

    public ProductVersionInstallationInfoProvider(
        IProductVersionProvider productVersionProvider,
        IReleaseVersionProvider releaseVersionProvider)
    {
        this.productVersionProvider = productVersionProvider;
        this.releaseVersionProvider = releaseVersionProvider;
    }

    public async Task<InstallationInfo> GetInstallationInfoAsync(CancellationToken ct = default)
    {
        var currentVersion = await productVersionProvider.GetCurrentVersionAsync(ct);
        var releases = await releaseVersionProvider.GetReleaseVersionsAsync(currentVersion.Version, ct);

        return new InstallationInfo
        {
            InstalledVersion = currentVersion.Version,
            LatestAvailableVersion = releases.LatestVersion?.Version ?? currentVersion.Version,
            NewerVersionCount = releases.Versions.Count,
            DeploymentTarget = "On-premise / Kubernetes"
        };
    }
}
