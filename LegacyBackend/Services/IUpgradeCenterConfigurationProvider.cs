namespace Bold.UpgradeCenter.Services;

public interface IUpgradeCenterConfigurationProvider
{
    Task<UpgradeCenterRuntimeConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);
}

