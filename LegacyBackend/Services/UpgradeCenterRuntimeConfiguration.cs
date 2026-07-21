namespace Bold.UpgradeCenter.Services;

public sealed record UpgradeCenterRuntimeConfiguration(
    string? IdpBaseUrl,
    string? BiBaseUrl,
    string? BoldBiVersion,
    string? EncryptedMasterDatabaseConnectionString = null,
    int? MasterDatabaseServerType = null,
    string? MachineKeyDecryptionKey = null,
    string? InternalAppClientId = null,
    string? InternalAppClientSecret = null);
