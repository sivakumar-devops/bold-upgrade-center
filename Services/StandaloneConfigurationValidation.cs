using System.Security.Cryptography;
using System.Text.Json;

namespace Bold.UpgradeCenter.Services;

internal static class StandaloneConfigurationValidation
{
    internal static UpgradeCenterRuntimeConfiguration Parse(string json)
    {
        try
        {
            using var document = JsonDocument.Parse(json);
            return UpgradeCenterConfigurationParser.Parse(document.RootElement);
        }
        catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
        {
            throw new InvalidOperationException("Standalone configuration is not a valid product configuration JSON object.");
        }
    }

    internal static string ValidateAndDecrypt(UpgradeCenterRuntimeConfiguration runtime, string privateKey, IBoldConnectionStringDecryptor decryptor)
    {
        if (!Uri.TryCreate(runtime.IdpBaseUrl, UriKind.Absolute, out var appUri) ||
            (appUri.Scheme != Uri.UriSchemeHttp && appUri.Scheme != Uri.UriSchemeHttps) ||
            !string.IsNullOrEmpty(appUri.UserInfo) || !string.IsNullOrEmpty(appUri.Query) || !string.IsNullOrEmpty(appUri.Fragment))
        {
            throw new InvalidOperationException("Standalone mode requires a valid application base URL in InternalAppUrls.Idp in the product configuration.");
        }

        if (string.IsNullOrWhiteSpace(runtime.EncryptedMasterDatabaseConnectionString) ||
            !runtime.MasterDatabaseServerType.HasValue || string.IsNullOrWhiteSpace(runtime.MachineKeyDecryptionKey))
        {
            throw new InvalidOperationException("Standalone configuration requires SqlConfiguration.ConnectionString, SqlConfiguration.ServerType, and MachineKey.DecryptionKey.");
        }

        if (!runtime.UseSingleTenantDb.HasValue)
        {
            throw new InvalidOperationException("Standalone configuration requires a valid UseSingleTenantDb setting to determine database eligibility safely.");
        }

        if (runtime.MasterDatabaseServerType is not (0 or 1 or 3 or 4))
        {
            throw new InvalidOperationException("Standalone SqlConfiguration.ServerType must identify SQL Server, MySQL, Oracle, or PostgreSQL. Existing upgrade eligibility restrictions still apply.");
        }

        try
        {
            var keyMaterial = decryptor.GetTokenKeyMaterial(runtime.MachineKeyDecryptionKey!, privateKey);
            var connection = decryptor.DecryptConnectionString(runtime.EncryptedMasterDatabaseConnectionString!, keyMaterial);
            if (string.IsNullOrWhiteSpace(connection))
            {
                throw new CryptographicException();
            }

            return connection;
        }
        catch (Exception exception) when (exception is CryptographicException or FormatException or InvalidOperationException or System.Xml.XmlException or ArgumentException)
        {
            throw new InvalidOperationException("Standalone database decryption failed. Verify that config.json and the private-key file belong to the same installation and contain a valid master database connection.");
        }
    }
}
