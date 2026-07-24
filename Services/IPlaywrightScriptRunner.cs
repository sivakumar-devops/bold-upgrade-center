namespace BoldBI.UpgradeCenter.Services;

public interface IPlaywrightScriptRunner
{
    Task<PlaywrightScriptResult> ExecutePreUpgradeAsync(CancellationToken cancellationToken = default);

    Task<PlaywrightScriptResult> ExecutePostUpgradeAsync(CancellationToken cancellationToken = default);

    Task<PlaywrightScriptResult> ExecutePreUpgradeAsync(
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        CancellationToken cancellationToken = default);

    Task<PlaywrightScriptResult> ExecutePostUpgradeAsync(
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        CancellationToken cancellationToken = default);
}
