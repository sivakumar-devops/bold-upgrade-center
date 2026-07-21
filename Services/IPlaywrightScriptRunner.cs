namespace BoldBI.UpgradeCenter.Services;

public interface IPlaywrightScriptRunner
{
    Task<PlaywrightScriptResult> ExecutePreUpgradeAsync(CancellationToken cancellationToken = default);

    Task<PlaywrightScriptResult> ExecutePostUpgradeAsync(CancellationToken cancellationToken = default);
}
