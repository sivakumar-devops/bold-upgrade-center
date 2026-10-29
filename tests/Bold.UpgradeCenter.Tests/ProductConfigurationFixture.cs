using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Bold.UpgradeCenter.Services;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Tests;

internal sealed class ProductConfigurationFixture : IDisposable
{
    public const string Username = "upgrade-test-admin";
    public const string Password = "Test-only-passphrase-43!";
    public const string MasterConnection = "Host=127.0.0.1;Database=uc_test_master;Username=test;Password=test;";
    public const string TenantConnection = "Host=127.0.0.1;Database=uc_test_tenant;Username=test;Password=test;";
    public const string MachineKey = "test-only-machine-decryption-key";
    private readonly byte[] tokenKey = RandomNumberGenerator.GetBytes(32);
    private readonly byte[] tokenIv = RandomNumberGenerator.GetBytes(16);

    public string DirectoryPath { get; } = Path.Combine(Path.GetTempPath(), "bold-uc-tests", Guid.NewGuid().ToString("N"));
    public string ConfigPath => Path.Combine(DirectoryPath, "config.json");
    public string KeyPath => Path.Combine(DirectoryPath, "privatekeys.dat");
    public JsonObject Root { get; }

    public ProductConfigurationFixture()
    {
        Directory.CreateDirectory(DirectoryPath);
        Root = JsonSerializer.SerializeToNode(new
        {
            InternalAppUrls = new { Idp = "https://product.example/platform", Bi = "https://product.example/platform/bi", Reports = "https://product.example/platform/reporting" },
            SqlConfiguration = new { ConnectionString = EncryptConnection(MasterConnection), ServerType = 4, SchemaName = "custom_schema", Prefix = "CUSTOMTC_" },
            MachineKey = new { DecryptionKey = MachineKey },
            InternalAppClientId = "test-client-id",
            InternalAppClientSecret = "test-client-secret",
            UseSingleTenantDb = false,
            ShowDataHub = true,
            BoldProducts = new[] { new { Name = "BoldBI", Version = "17.1.30" }, new { Name = "BoldReports", Version = "15.1.10" } }
        })!.AsObject();
        SaveConfig();

        var protectionKey = MachineKey;
        protectionKey += EncryptPrivateKeyValue(MachineKey, MachineKey);
        protectionKey += EncryptPrivateKeyValue(protectionKey, MachineKey);
        var keyXml = $"<CryptoServiceKeys><TokenCryptoServiceKeys><KeyBytes>{Convert.ToBase64String(tokenKey)}</KeyBytes><InitVectorBytes>{Convert.ToBase64String(tokenIv)}</InitVectorBytes></TokenCryptoServiceKeys></CryptoServiceKeys>";
        File.WriteAllText(KeyPath, EncryptPrivateKeyValue(protectionKey, keyXml));
    }

    public void SaveConfig() => File.WriteAllText(ConfigPath, Root.ToJsonString());

    public UpgradeCenterOptions Options => new()
    {
        IntegrationMode = "Standalone",
        Configuration = new() { ConfigFilePath = ConfigPath, PrivateKeyFilePath = KeyPath }
    };

    public FileUpgradeCenterConfigurationProvider CreateProvider(UpgradeCenterOptions? options = null) =>
        new(Microsoft.Extensions.Options.Options.Create(options ?? Options),
            new BoldConnectionStringDecryptor(), NullLogger<FileUpgradeCenterConfigurationProvider>.Instance);

    public string EncryptConnection(string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = tokenKey;
        aes.IV = tokenIv;
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plaintext), tokenIv));
    }

    private static string EncryptPrivateKeyValue(string key, string plaintext)
    {
        using var aes = Aes.Create();
        aes.Key = MD5.HashData(Encoding.Unicode.GetBytes(key));
        var iv = new byte[16];
        return Convert.ToBase64String(aes.EncryptCbc(Encoding.UTF8.GetBytes(plaintext), iv));
    }

    public void Dispose() => Directory.Delete(DirectoryPath, recursive: true);
}
