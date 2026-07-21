namespace Bold.UpgradeCenter.Services;

public interface IUpgradeDatabaseBackupService
{
    Task<UpgradeDatabaseBackupResult> BackupAffectedTablesAsync(string currentVersion, string selectedVersion, CancellationToken cancellationToken = default);

    Task<UpgradeDatabaseRestoreResult> RestoreAffectedTablesAsync(UpgradeDatabaseBackupResult backupResult, CancellationToken cancellationToken = default);

    Task<BackupCleanupResult> CleanupBackupDatabasesAsync(UpgradeDatabaseBackupResult backupResult, CancellationToken cancellationToken = default);
}
