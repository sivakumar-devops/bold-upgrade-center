namespace BoldBI.UpgradeCenter.Services;

public interface IPlaywrightKubernetesJobService
{
    Task<PlaywrightScriptResult> RunAsync(
        PlaywrightValidationMode mode,
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task CleanupSharedStateAsync(string upgradeJobId, CancellationToken cancellationToken = default);
}
