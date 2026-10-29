using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class FileUpgradeCenterConfigurationProvider : IUpgradeCenterConfigurationProvider, IIdpPrivateKeyProvider
{
    private readonly UpgradeCenterFileConfigurationOptions options;
    private readonly IBoldConnectionStringDecryptor decryptor;
    private readonly ILogger<FileUpgradeCenterConfigurationProvider> logger;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private FileConfigurationSnapshot? snapshot;

    public FileUpgradeCenterConfigurationProvider(
        IOptions<UpgradeCenterOptions> options,
        IBoldConnectionStringDecryptor decryptor,
        ILogger<FileUpgradeCenterConfigurationProvider> logger)
    {
        this.options = options.Value.Configuration;
        this.decryptor = decryptor;
        this.logger = logger;
    }

    public async Task<UpgradeCenterRuntimeConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        return (await LoadSnapshotAsync(cancellationToken)).Configuration;
    }

    public async Task<string> GetEncryptedPrivateKeyAsync(CancellationToken cancellationToken = default)
    {
        return (await LoadSnapshotAsync(cancellationToken)).EncryptedPrivateKey;
    }

    private async Task<FileConfigurationSnapshot> LoadSnapshotAsync(CancellationToken cancellationToken)
    {
        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            var configJson = await ReadRequiredFileAsync(options.ConfigFilePath, "ConfigFilePath", cancellationToken);
            var privateKey = await ReadRequiredFileAsync(options.PrivateKeyFilePath, "PrivateKeyFilePath", cancellationToken);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(configJson + "\0" + privateKey)));
            if (snapshot?.Fingerprint == fingerprint)
            {
                return snapshot;
            }

            var runtime = StandaloneConfigurationValidation.Parse(configJson);
            StandaloneConfigurationValidation.ValidateAndDecrypt(runtime, privateKey, decryptor);

            snapshot = new FileConfigurationSnapshot(fingerprint, runtime, privateKey);
            logger.LogInformation("Loaded and validated Upgrade Center configuration and private key from the standalone file mount.");
            return snapshot;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private static async Task<string> ReadRequiredFileAsync(string path, string setting, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new InvalidOperationException($"Standalone mode requires UpgradeCenter:Configuration:{setting}.");
        }

        string content;
        try
        {
            content = await File.ReadAllTextAsync(path, cancellationToken);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            throw new InvalidOperationException($"Standalone {setting} could not be read. Mount the existing product configuration directory read-only and check the configured path and read permissions.");
        }

        if (string.IsNullOrWhiteSpace(content))
        {
            throw new InvalidOperationException($"Standalone {setting} is empty. Supply the existing product file.");
        }

        return content;
    }

    private sealed record FileConfigurationSnapshot(
        string Fingerprint,
        UpgradeCenterRuntimeConfiguration Configuration,
        string EncryptedPrivateKey);
}
