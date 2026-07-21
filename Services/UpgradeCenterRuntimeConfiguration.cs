namespace BoldBI.UpgradeCenter.Services;

public sealed record UpgradeCenterRuntimeConfiguration(
    string? IdpBaseUrl,
    string? BiBaseUrl,
    string? BoldBiVersion,
    string? EncryptedMasterDatabaseConnectionString = null,
    int? MasterDatabaseServerType = null,
    string? MachineKeyDecryptionKey = null,
    string? InternalAppClientId = null,
    string? InternalAppClientSecret = null);

public interface IUpgradeCenterConfigurationProvider
{
    Task<UpgradeCenterRuntimeConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default);
}
