using System.Data.Common;
using Microsoft.Data.SqlClient;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace Bold.UpgradeCenter.Services;

public sealed class ObjectStorageUpgradeCenterConfigurationProvider(
    StandaloneObjectStorageSettings settings,
    IStandaloneObjectReader reader,
    IStandaloneRuntimeConfigurationReader runtimeReader,
    IBoldConnectionStringDecryptor decryptor,
    TimeProvider clock,
    ILogger<ObjectStorageUpgradeCenterConfigurationProvider> logger) : IUpgradeCenterConfigurationProvider, IIdpPrivateKeyProvider
{
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private Snapshot? snapshot;

    public async Task<UpgradeCenterRuntimeConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).Configuration;

    public async Task<string> GetEncryptedPrivateKeyAsync(CancellationToken cancellationToken = default) =>
        (await LoadAsync(cancellationToken)).PrivateKey;

    private async Task<Snapshot> LoadAsync(CancellationToken cancellationToken)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (snapshot is not null && snapshot.ExpiresAt > clock.GetUtcNow())
            {
                return snapshot;
            }

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(60));
            var token = timeout.Token;
            var config = await reader.ReadAsync(settings.ConfigurationKey, null, token);
            var key = await reader.ReadAsync(settings.PrivateKeyKey, null, token);
            if (string.IsNullOrWhiteSpace(config.ETag) || string.IsNullOrWhiteSpace(key.ETag) || string.IsNullOrWhiteSpace(key.Content))
            {
                throw new InvalidOperationException("Standalone storage returned empty configuration/key content or missing object version information.");
            }

            var bootstrap = StandaloneConfigurationValidation.Parse(config.Content);
            var connection = StandaloneConfigurationValidation.ValidateAndDecrypt(bootstrap, key.Content, decryptor);
            var currentJson = await runtimeReader.ReadAsync(bootstrap, connection, token);
            var runtime = bootstrap;
            if (currentJson is not null)
            {
                runtime = StandaloneConfigurationValidation.Parse(currentJson);
                var currentConnection = StandaloneConfigurationValidation.ValidateAndDecrypt(runtime, key.Content, decryptor);
                if (runtime.MasterDatabaseServerType != bootstrap.MasterDatabaseServerType ||
                    !CreateConnectionBuilder(bootstrap.MasterDatabaseServerType!.Value, connection).EquivalentTo(
                        CreateConnectionBuilder(runtime.MasterDatabaseServerType!.Value, currentConnection)) ||
                    !string.Equals(runtime.MasterDatabaseSchemaName, bootstrap.MasterDatabaseSchemaName, StringComparison.Ordinal) ||
                    !string.Equals(runtime.MasterDatabaseTablePrefix, bootstrap.MasterDatabaseTablePrefix, StringComparison.Ordinal))
                {
                    throw new InvalidOperationException("Standalone stored configuration and database runtime configuration identify different master database settings. Refresh the product storage configuration before upgrading.");
                }
            }

            // Reject an object pair that rotated while database settings were being resolved.
            var confirmedConfig = await reader.ReadAsync(settings.ConfigurationKey, config.ETag, token);
            var confirmedKey = await reader.ReadAsync(settings.PrivateKeyKey, key.ETag, token);
            if (confirmedConfig != config || confirmedKey != key)
            {
                throw new InvalidOperationException("Standalone configuration/key objects changed during loading. Retry after the product configuration is stable.");
            }
            snapshot = new Snapshot(runtime, key.Content, clock.GetUtcNow().AddSeconds(30));
            logger.LogInformation("Validated standalone configuration from {StorageProvider}. Runtime configuration source: {RuntimeSource}.",
                settings.Source, currentJson is null ? "legacy object configuration" : "master database");
            return snapshot;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new InvalidOperationException("Standalone configuration loading timed out. Verify object-storage and master database connectivity.");
        }
        catch (Exception exception) when (exception is DbException or ArgumentException)
        {
            throw new InvalidOperationException("Standalone configuration could not be validated. Verify the master database settings and storage configuration.");
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private static DbConnectionStringBuilder CreateConnectionBuilder(int type, string connection) => type switch
    {
        0 => new SqlConnectionStringBuilder(connection),
        1 => new MySqlConnectionStringBuilder(connection),
        3 => new OracleConnectionStringBuilder(connection),
        4 => new NpgsqlConnectionStringBuilder(connection),
        _ => throw new InvalidOperationException("Unsupported standalone master database type.")
    };

    private sealed record Snapshot(UpgradeCenterRuntimeConfiguration Configuration, string PrivateKey, DateTimeOffset ExpiresAt);
}
