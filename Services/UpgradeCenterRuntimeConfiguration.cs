namespace BoldBI.UpgradeCenter.Services;

public sealed record UpgradeCenterRuntimeConfiguration(
    string? IdpBaseUrl,
    string? BiBaseUrl,
    string? BoldBiVersion);

public interface IUpgradeCenterConfigurationProvider
{
    Task<UpgradeCenterRuntimeConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);
}
