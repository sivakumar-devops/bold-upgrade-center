using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Bold.UpgradeCenter.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bold.UpgradeCenter.Tests;

public sealed class StandaloneConfigurationTests
{
    [Theory]
    [InlineData(null, UpgradeCenterIntegrationMode.IdpIntegrated)]
    [InlineData("IdpIntegrated", UpgradeCenterIntegrationMode.IdpIntegrated)]
    [InlineData("idpintegrated", UpgradeCenterIntegrationMode.IdpIntegrated)]
    [InlineData("Standalone", UpgradeCenterIntegrationMode.Standalone)]
    [InlineData(" standalone ", UpgradeCenterIntegrationMode.Standalone)]
    public void ModeSelectionIsExplicitAndCaseInsensitive(string? mode, UpgradeCenterIntegrationMode expected) =>
        Assert.Equal(expected, UpgradeCenterIntegrationModes.Parse(mode));

    [Theory]
    [InlineData("")]
    [InlineData("standalon")]
    [InlineData("1")]
    public void InvalidModesAreRejected(string mode) =>
        Assert.Throws<InvalidOperationException>(() => UpgradeCenterIntegrationModes.Parse(mode));

    [Fact]
    public async Task MountedFilesSupportExistingMasterAndTenantDecryption()
    {
        using var fixture = new ProductConfigurationFixture();
        var provider = fixture.CreateProvider();
        var runtime = await provider.GetConfigurationAsync();
        Assert.Equal("custom_schema", runtime.MasterDatabaseSchemaName);
        Assert.Equal("CUSTOMTC_", runtime.MasterDatabaseTablePrefix);
        Assert.Equal("17.1.30", runtime.BoldBiVersion);
        Assert.Equal("15.1.10", runtime.BoldReportsVersion);
        Assert.False(runtime.UseSingleTenantDb);
        Assert.True(runtime.ShowDataHub);
        var discovery = new UpgradeDatabaseDiscoveryService(provider, provider, new BoldConnectionStringDecryptor(),
            new UpgradeProductContext(), NullLogger<UpgradeDatabaseDiscoveryService>.Instance);
        var master = await discovery.DiscoverMasterDatabaseAsync();
        Assert.Equal("uc_test_master", master.DatabaseName);
        Assert.Equal("custom_schema", master.SchemaName);
        Assert.Equal("CUSTOMTC_", master.TablePrefix);
        var decryptor = new BoldConnectionStringDecryptor();
        var material = decryptor.GetTokenKeyMaterial(runtime.MachineKeyDecryptionKey!, await provider.GetEncryptedPrivateKeyAsync());
        Assert.Equal(ProductConfigurationFixture.TenantConnection, decryptor.DecryptConnectionString(fixture.EncryptConnection(ProductConfigurationFixture.TenantConnection), material));
    }

    [Fact]
    public async Task FileAndIdpProvidersParseTheSameConfiguration()
    {
        using var fixture = new ProductConfigurationFixture();
        var handler = new ConfigurationHandler(fixture.Root.ToJsonString());
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["UpgradeCenter:Services:IdpApi"] = "https://idp.example/api" }).Build();
        var idp = new IdpConfigurationProvider(new ClientFactory(handler), config,
            Options.Create(new UpgradeCenterOptions()), NullLogger<IdpConfigurationProvider>.Instance);
        Assert.Equal(await idp.GetConfigurationAsync(), await fixture.CreateProvider().GetConfigurationAsync());
        Assert.Equal("/api/configuration", handler.RequestPath);
    }

    [Fact]
    public async Task MountedApplicationBaseUrlPreservesBasePath()
    {
        using var fixture = new ProductConfigurationFixture();
        fixture.Root["InternalAppUrls"]!["Idp"] = "https://public.example/customer/";
        fixture.SaveConfig();
        Assert.Equal("https://public.example/customer", (await fixture.CreateProvider().GetConfigurationAsync()).IdpBaseUrl);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("/relative")]
    [InlineData("ftp://product.example")]
    [InlineData("https://user:password@product.example")]
    [InlineData("https://product.example?secret=value")]
    [InlineData("https://product.example#fragment")]
    public async Task MissingOrInvalidMountedApplicationUrlIsRejected(string? url)
    {
        using var fixture = new ProductConfigurationFixture();
        fixture.Root["InternalAppUrls"]!["Idp"] = url;
        fixture.SaveConfig();
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateProvider().GetConfigurationAsync());
        Assert.Contains("InternalAppUrls.Idp", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task LegacyUrlOverridesCannotReplaceMountedApplicationUrl()
    {
        using var fixture = new ProductConfigurationFixture();
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["UpgradeCenter:Configuration:AppBaseUrl"] = "https://override.example",
            ["APP_BASE_URL"] = "https://override.example",
            ["app_base"] = "https://override.example"
        }).Build();
        var options = fixture.Options;
        configuration.GetSection("UpgradeCenter").Bind(options);
        Assert.Equal("https://product.example/platform", (await fixture.CreateProvider(options).GetConfigurationAsync()).IdpBaseUrl);
    }

    [Fact]
    public async Task RootAliasesAndCaseInsensitiveSettingsArePreserved()
    {
        using var fixture = new ProductConfigurationFixture();
        fixture.Root["SystemSettings"] = new JsonObject { ["UseSingleTenantDb"] = "true" };
        fixture.Root.Remove("UseSingleTenantDb");
        fixture.Root["SqlConfiguration"]!["ServerType"] = "PostgreSQL";
        fixture.Root["internalappurls"] = fixture.Root["InternalAppUrls"]!.DeepClone();
        fixture.Root.Remove("InternalAppUrls");
        var provider = fixture.CreateProvider();
        fixture.SaveConfig();
        var runtime = await provider.GetConfigurationAsync();
        Assert.True(runtime.UseSingleTenantDb);
        Assert.Equal(4, runtime.MasterDatabaseServerType);
        Assert.Equal("https://product.example/platform", runtime.IdpBaseUrl);
    }

    [Fact]
    public async Task MissingFileDoesNotFallBackToIdpOrOldSnapshot()
    {
        using var fixture = new ProductConfigurationFixture();
        var provider = fixture.CreateProvider();
        await provider.GetConfigurationAsync();
        File.Delete(fixture.KeyPath);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetEncryptedPrivateKeyAsync());
        Assert.Contains("PrivateKeyFilePath", error.Message);
    }

    [Fact]
    public async Task InvalidJsonDoesNotExposeContentsOrReturnStaleConfiguration()
    {
        using var fixture = new ProductConfigurationFixture();
        var provider = fixture.CreateProvider();
        await provider.GetConfigurationAsync();
        File.WriteAllText(fixture.ConfigPath, "{\"secret\":\"do-not-log-this\"");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetConfigurationAsync());
        Assert.DoesNotContain("do-not-log-this", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task MismatchedKeysAreRejectedWithoutSecretDetails()
    {
        using var fixture = new ProductConfigurationFixture();
        using var other = new ProductConfigurationFixture();
        File.Copy(other.KeyPath, fixture.KeyPath, overwrite: true);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateProvider().GetConfigurationAsync());
        Assert.Contains("decryption failed", error.Message);
        Assert.DoesNotContain(ProductConfigurationFixture.MachineKey, error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("UseSingleTenantDb")]
    [InlineData("MachineKey")]
    [InlineData("SqlConfiguration")]
    public async Task MissingRequiredConfigurationBlocksStandalone(string property)
    {
        using var fixture = new ProductConfigurationFixture();
        fixture.Root.Remove(property);
        fixture.SaveConfig();
        await Assert.ThrowsAsync<InvalidOperationException>(() => fixture.CreateProvider().GetConfigurationAsync());
    }

    [Fact]
    public async Task ValidConfigurationChangesAreReloaded()
    {
        using var fixture = new ProductConfigurationFixture();
        var provider = fixture.CreateProvider();
        await provider.GetConfigurationAsync();
        fixture.Root["ShowDataHub"] = false;
        fixture.SaveConfig();
        Assert.False((await provider.GetConfigurationAsync()).ShowDataHub);
    }

    [Theory]
    [InlineData("BI", "/platform/bi/api/product-version", "17.1.30")]
    [InlineData("Reports", "/platform/reporting/api/product-version", "15.1.10")]
    public async Task ProductVersionDiscoveryUsesMountedUrlsForBothProducts(string product, string expectedPath, string version)
    {
        using var fixture = new ProductConfigurationFixture();
        var context = new UpgradeProductContext();
        context.SetCurrent(product);
        var handler = new VersionHandler(HttpStatusCode.OK, "{\"Status\":true,\"Data\":\"" + version + "\"}");
        var provider = new ProductVersionProvider(context, fixture.CreateProvider(), new ClientFactory(handler), NullLogger<ProductVersionProvider>.Instance);
        var result = await provider.GetCurrentVersionAsync();
        Assert.Equal(version, result.Version);
        Assert.Equal(expectedPath, handler.RequestPath);
        Assert.Contains("Product Version API", result.Source);
    }

    [Theory]
    [InlineData("BI", "17.1.30")]
    [InlineData("Reports", "15.1.10")]
    public async Task VersionFallbackIdentifiesMountedConfiguration(string product, string expectedVersion)
    {
        using var fixture = new ProductConfigurationFixture();
        var context = new UpgradeProductContext();
        context.SetCurrent(product);
        var provider = new ProductVersionProvider(context, fixture.CreateProvider(),
            new ClientFactory(new VersionHandler(HttpStatusCode.ServiceUnavailable, "")), NullLogger<ProductVersionProvider>.Instance);
        var result = await provider.GetCurrentVersionAsync();
        Assert.Equal(expectedVersion, result.Version);
        Assert.Equal("Mounted product configuration", result.Source);
    }

    [Fact]
    public async Task CancellationDoesNotBecomeAConfigurationFallback()
    {
        using var fixture = new ProductConfigurationFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.CreateProvider().GetConfigurationAsync(cancellation.Token));
    }

    private sealed class VersionHandler(HttpStatusCode status, string content) : HttpMessageHandler
    {
        public string? RequestPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(content, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class ConfigurationHandler(string json) : HttpMessageHandler
    {
        public string? RequestPath { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            RequestPath = request.RequestUri!.AbsolutePath;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(new JsonObject { ["data"] = new JsonObject { ["config"] = json } }.ToJsonString(), Encoding.UTF8, "application/json")
            });
        }
    }

    private sealed class ClientFactory(HttpMessageHandler handler) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(handler, disposeHandler: false);
    }
}
