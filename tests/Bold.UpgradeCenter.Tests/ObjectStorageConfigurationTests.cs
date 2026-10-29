using System.Net;
using System.Text;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Bold.UpgradeCenter.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Bold.UpgradeCenter.Tests;

public sealed class ObjectStorageConfigurationTests
{
    [Theory]
    [InlineData(null, StandaloneConfigurationSource.FileSystem)]
    [InlineData(" FileSystem ", StandaloneConfigurationSource.FileSystem)]
    [InlineData("amazons3", StandaloneConfigurationSource.AmazonS3)]
    [InlineData("AzureBlob", StandaloneConfigurationSource.AzureBlob)]
    [InlineData("OCIObjectStorage", StandaloneConfigurationSource.OCIObjectStorage)]
    public void SourceSelectionIsExplicitAndCaseInsensitive(string? source, StandaloneConfigurationSource expected) =>
        Assert.Equal(expected, StandaloneObjectStorageSettings.ParseSource(source));

    [Theory]
    [InlineData("")]
    [InlineData("Auto")]
    [InlineData("3")]
    [InlineData("unknown")]
    public void InvalidSourcesDoNotFallBack(string source) =>
        Assert.Throws<InvalidOperationException>(() => StandaloneObjectStorageSettings.ParseSource(source));

    [Theory]
    [InlineData(null, StandaloneConfigurationSource.FileSystem)]
    [InlineData("0", StandaloneConfigurationSource.FileSystem)]
    [InlineData("1", StandaloneConfigurationSource.AzureBlob)]
    [InlineData("3", StandaloneConfigurationSource.AmazonS3)]
    [InlineData("4", StandaloneConfigurationSource.OCIObjectStorage)]
    [InlineData("AmazonS3", StandaloneConfigurationSource.AmazonS3)]
    public void ProductStorageTypeSelectsTheProviderWithoutExtraUpgradeCenterSettings(string? storageType, StandaloneConfigurationSource expected)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BOLD_SERVICES_STORAGETYPE"] = storageType,
            ["BOLD_SERVICES_PV_USED"] = "true"
        }).Build();
        Assert.Equal(expected, StandaloneObjectStorageSettings.ResolveSource(config));
    }

    [Theory]
    [InlineData("AmazonS3", "customer/root/app_data/configuration/config.json")]
    [InlineData("AzureBlob", "boldservices/app_data/configuration/config.json")]
    [InlineData("OCIObjectStorage", "customer/root/app_data/configuration/config.json")]
    public void ExistingProductSettingsResolveTheSameObjects(string source, string expectedKey)
    {
        var settings = Settings(source);
        Assert.Equal(expectedKey, settings.ConfigurationKey);
        Assert.Equal(expectedKey.Replace("config.json", "privatekeys.dat"), settings.PrivateKeyKey);
        Assert.Equal("existing-storage", settings.BucketName);
    }

    [Fact]
    public void DedicatedReadOnlySecretCanOverrideProductCredentials()
    {
        var config = Configuration("AmazonS3");
        config["UpgradeCenter:Configuration:ObjectStorage:AccessKey"] = "readonly-access";
        config["UpgradeCenter:Configuration:ObjectStorage:SecretKey"] = "readonly-secret";
        var settings = StandaloneObjectStorageSettings.FromConfiguration(config);
        Assert.Equal("readonly-access", settings.AccessKey);
        Assert.Equal("readonly-secret", settings.SecretKey);
        Assert.Equal("existing-storage", settings.BucketName);
    }

    [Fact]
    public void S3WorkloadIdentityDoesNotRequireStaticCredentials()
    {
        var config = Configuration("AmazonS3");
        config["BOLD_SERVICES_AWS_ACCESS_KEY_ID"] = null;
        config["BOLD_SERVICES_AWS_SECRET_ACCESS_KEY"] = null;
        Assert.Null(StandaloneObjectStorageSettings.FromConfiguration(config).AccessKey);
    }

    [Theory]
    [InlineData("AmazonS3")]
    [InlineData("AzureBlob")]
    public void ExplicitWorkloadIdentityDoesNotReuseProductAccessKeys(string source)
    {
        var config = Configuration(source);
        config["BOLD_SERVICES_AZUREBLOB_ACCESSKEY"] = "product-secret";
        config["UpgradeCenter:Configuration:ObjectStorage:UseWorkloadIdentity"] = "true";
        var settings = StandaloneObjectStorageSettings.FromConfiguration(config);
        Assert.Null(settings.AccessKey);
        Assert.Null(settings.SecretKey);
        Assert.Null(settings.ConnectionString);
    }

    [Fact]
    public void AzureHttpsUsesTheSameAccountEndpointAsTheProduct()
    {
        var config = Configuration("AzureBlob");
        config["BOLD_SERVICES_AZUREBLOB_CONNECTION_TYPE"] = "Https";
        config["BOLD_SERVICES_AZUREBLOB_STORAGE_URI"] = "https://unused-product-setting.example";
        Assert.Null(StandaloneObjectStorageSettings.FromConfiguration(config).ServiceUrl);
        config["BOLD_SERVICES_AZUREBLOB_CONNECTION_TYPE"] = "CustomEndPoint";
        Assert.Equal("https://unused-product-setting.example", StandaloneObjectStorageSettings.FromConfiguration(config).ServiceUrl);
        config["BOLD_SERVICES_AZUREBLOB_CONNECTION_TYPE"] = "Http";
        Assert.Null(StandaloneObjectStorageSettings.FromConfiguration(config).ServiceUrl);
    }

    [Theory]
    [InlineData("Http", "http://account.blob.core.windows.net")]
    [InlineData("Https", "https://account.blob.core.windows.net")]
    [InlineData("CustomEndPoint", "http://storage.example/account-prefix")]
    public void ExistingAzureConnectionFormatsDoNotRequireNewRestrictions(string connectionType, string endpoint)
    {
        var config = Configuration("AzureBlob");
        config["BOLD_SERVICES_AZUREBLOB_CONNECTION_TYPE"] = connectionType;
        config["BOLD_SERVICES_AZUREBLOB_STORAGE_URI"] = endpoint;
        config["BOLD_SERVICES_AZUREBLOB_ACCESSKEY"] = Convert.ToBase64String(Encoding.UTF8.GetBytes("test-only-key-material"));
        using var reader = new StandaloneObjectReader(StandaloneObjectStorageSettings.FromConfiguration(config));
    }

    [Fact]
    public void ExistingObjectRootAndEndpointValuesArePreserved()
    {
        var config = Configuration("AmazonS3");
        config["BOLD_SERVICES_AMAZON_ROOTFOLDER_NAME"] = "customer/../literal-folder";
        config["UpgradeCenter:Configuration:ObjectStorage:ServiceUrl"] = "http://storage.example/custom-path";
        var settings = StandaloneObjectStorageSettings.FromConfiguration(config);
        Assert.Equal("customer/../literal-folder/app_data/configuration/config.json", settings.ConfigurationKey);
        Assert.Equal("http://storage.example/custom-path", settings.ServiceUrl);
    }

    [Theory]
    [InlineData("BOLD_SERVICES_AMAZON_BUCKET_NAME", "")]
    [InlineData("BOLD_SERVICES_AWS_SECRET_ACCESS_KEY", "")]
    public void InvalidSettingsAreRejectedWithoutSecrets(string key, string value)
    {
        var config = Configuration("AmazonS3");
        config[key] = value;
        var error = Assert.Throws<InvalidOperationException>(() => StandaloneObjectStorageSettings.FromConfiguration(config));
        Assert.DoesNotContain("product-secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("AmazonS3")]
    [InlineData("AzureBlob")]
    [InlineData("OCIObjectStorage")]
    public async Task ObjectsProvideConfigurationAndKeyWithoutProductApis(string source)
    {
        using var fixture = new ProductConfigurationFixture();
        var reader = new ObjectReader(fixture, Settings(source));
        var runtime = new RuntimeReader();
        var provider = Provider(reader, runtime);
        Assert.Equal("https://product.example/platform", (await provider.GetConfigurationAsync()).IdpBaseUrl);
        Assert.Equal(await File.ReadAllTextAsync(fixture.KeyPath), await provider.GetEncryptedPrivateKeyAsync());
        Assert.Equal(4, reader.Requests.Count);
        Assert.Equal(ProductConfigurationFixture.MasterConnection, runtime.Connection);
        Assert.Equal(new string?[] { null, null, "config-v1", "key-v1" }, reader.Requests.Select(request => request.ETag));
    }

    [Fact]
    public async Task DatabaseRuntimeConfigurationOverridesStaleObjectSettings()
    {
        using var fixture = new ProductConfigurationFixture();
        var root = fixture.Root.DeepClone();
        root["InternalAppUrls"]!["Idp"] = "https://current.example/base";
        root["ShowDataHub"] = false;
        var provider = Provider(new ObjectReader(fixture, Settings()), new RuntimeReader { Json = root.ToJsonString() });
        var config = await provider.GetConfigurationAsync();
        Assert.Equal("https://current.example/base", config.IdpBaseUrl);
        Assert.False(config.ShowDataHub);
    }

    [Fact]
    public async Task DatabaseRuntimeCannotRedirectToAnotherMasterDatabase()
    {
        using var fixture = new ProductConfigurationFixture();
        var root = fixture.Root.DeepClone();
        root["SqlConfiguration"]!["ConnectionString"] = fixture.EncryptConnection(ProductConfigurationFixture.MasterConnection.Replace("uc_test_master", "other_master"));
        var provider = Provider(new ObjectReader(fixture, Settings()), new RuntimeReader { Json = root.ToJsonString() });
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetConfigurationAsync());
        Assert.Contains("different master database settings", error.Message);
        Assert.DoesNotContain("other_master", error.Message);
    }

    [Fact]
    public async Task ExpiredSnapshotIsNotReturnedWhenStorageOrDatabaseFails()
    {
        using var fixture = new ProductConfigurationFixture();
        var clock = new TestClock();
        var reader = new ObjectReader(fixture, Settings());
        var runtime = new RuntimeReader();
        var provider = Provider(reader, runtime, clock);
        await provider.GetConfigurationAsync();
        clock.Advance();
        reader.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetConfigurationAsync());
        reader.Fail = false;
        runtime.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => provider.GetConfigurationAsync());
    }

    [Fact]
    public async Task ObjectRotationDuringLoadingIsRejected()
    {
        using var fixture = new ProductConfigurationFixture();
        var reader = new ObjectReader(fixture, Settings()) { Rotate = true };
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(reader, new RuntimeReader()).GetConfigurationAsync());
        reader.Rotate = false;
        reader.IgnoreVersionCondition = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(reader, new RuntimeReader()).GetConfigurationAsync());
    }

    [Fact]
    public async Task StorageTimeoutHasAnActionableError()
    {
        using var fixture = new ProductConfigurationFixture();
        var reader = new ObjectReader(fixture, Settings()) { Timeout = true };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(reader, new RuntimeReader()).GetConfigurationAsync());
        Assert.Contains("timed out", error.Message);
        Assert.Null(error.InnerException);
    }

    [Fact]
    public async Task MismatchedKeysAndInvalidDatabaseJsonAreRejected()
    {
        using var fixture = new ProductConfigurationFixture();
        using var other = new ProductConfigurationFixture();
        var reader = new ObjectReader(fixture, Settings()) { PrivateKey = File.ReadAllText(other.KeyPath) };
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(reader, new RuntimeReader()).GetConfigurationAsync());
        Assert.Contains("decryption failed", error.Message);
        reader.PrivateKey = null;
        await Assert.ThrowsAsync<InvalidOperationException>(() => Provider(reader, new RuntimeReader { Json = "{bad" }).GetConfigurationAsync());
    }

    [Fact]
    public async Task CallerCancellationIsPropagated()
    {
        using var fixture = new ProductConfigurationFixture();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            Provider(new ObjectReader(fixture, Settings()), new RuntimeReader()).GetConfigurationAsync(cancellation.Token));
    }

    [Fact]
    public async Task S3ReaderOnlyIssuesGetObjectRequestsWithVersionConditions()
    {
        var settings = Settings();
        using var client = new S3Client();
        using var reader = new StandaloneObjectReader(settings, client);
        var result = await reader.ReadAsync(settings.ConfigurationKey, "expected", default);
        Assert.Equal("content", result.Content);
        Assert.Equal(settings.BucketName, client.Request!.BucketName);
        Assert.Equal(settings.ConfigurationKey, client.Request.Key);
        Assert.Equal("expected", client.Request.EtagToMatch);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync("other-object", null, default));
    }

    [Fact]
    public async Task AzureReaderOnlyDownloadsAndPreservesVersionConditions()
    {
        var settings = Settings("AzureBlob");
        var container = new BlobContainer();
        using var reader = new StandaloneObjectReader(settings, container);
        Assert.Equal("content", (await reader.ReadAsync(settings.PrivateKeyKey, "expected", default)).Content);
        Assert.Equal(settings.PrivateKeyKey, container.RequestedKey);
        Assert.Equal(new ETag("expected"), container.Blob.Options!.Conditions.IfMatch);
    }

    [Fact]
    public async Task SdkErrorsAndOversizedObjectsAreSanitized()
    {
        var settings = Settings();
        using var client = new S3Client { Failure = new AmazonS3Exception("product-secret signed-url") { StatusCode = HttpStatusCode.Forbidden } };
        using var reader = new StandaloneObjectReader(settings, client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(settings.ConfigurationKey, null, default));
        Assert.DoesNotContain("product-secret", error.ToString());
        Assert.Null(error.InnerException);
        client.Failure = null;
        client.Content = new string('a', 4 * 1024 * 1024 + 1);
        await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(settings.ConfigurationKey, null, default));
    }

    [Theory]
    [InlineData(HttpStatusCode.NotFound)]
    [InlineData(HttpStatusCode.PreconditionFailed)]
    public async Task MissingOrChangedS3ObjectsCannotFallBack(HttpStatusCode status)
    {
        var settings = Settings();
        using var client = new S3Client { Failure = new AmazonS3Exception("product-secret") { StatusCode = status } };
        using var reader = new StandaloneObjectReader(settings, client);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => reader.ReadAsync(settings.ConfigurationKey, "old", default));
        Assert.DoesNotContain("product-secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Theory]
    [InlineData("AmazonS3")]
    [InlineData("AzureBlob")]
    [InlineData("OCIObjectStorage")]
    public void DependencyInjectionSelectsObjectStorageWithoutChangingLocalAuthentication(string source)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton<IBoldConnectionStringDecryptor, BoldConnectionStringDecryptor>();
        var config = Configuration(source);
        config["UpgradeCenter:IntegrationMode"] = "Standalone";
        services.AddUpgradeCenterIntegration(config, new HostEnvironment());
        services.AddSingleton<IStandaloneObjectReader>(new UnusedReader());
        using var provider = services.BuildServiceProvider();
        Assert.IsType<ObjectStorageUpgradeCenterConfigurationProvider>(provider.GetRequiredService<IUpgradeCenterConfigurationProvider>());
        Assert.Same(provider.GetRequiredService<IUpgradeCenterConfigurationProvider>(), provider.GetRequiredService<IIdpPrivateKeyProvider>());
    }

    [Fact]
    public void IntegratedModeDoesNotInitializeStandaloneStorage()
    {
        var config = Configuration("invalid-source");
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddHttpClient();
        services.AddSingleton<IConfiguration>(config);
        services.AddOptions<UpgradeCenterOptions>();
        services.AddUpgradeCenterIntegration(config, new HostEnvironment());
        using var provider = services.BuildServiceProvider();
        Assert.IsType<IdpConfigurationProvider>(provider.GetRequiredService<IUpgradeCenterConfigurationProvider>());
        Assert.IsType<IdpPrivateKeyProvider>(provider.GetRequiredService<IIdpPrivateKeyProvider>());
        Assert.Null(provider.GetService<IStandaloneObjectReader>());
    }

    private static StandaloneObjectStorageSettings Settings(string source = "AmazonS3") => StandaloneObjectStorageSettings.FromConfiguration(Configuration(source));

    private static IConfigurationRoot Configuration(string source) => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
    {
        ["BOLD_SERVICES_STORAGETYPE"] = source,
        ["BOLD_SERVICES_AMAZON_BUCKET_NAME"] = "existing-storage", ["BOLD_SERVICES_AWS_REGION"] = "us-east-1",
        ["BOLD_SERVICES_AWS_ACCESS_KEY_ID"] = "product-access", ["BOLD_SERVICES_AWS_SECRET_ACCESS_KEY"] = "product-secret",
        ["BOLD_SERVICES_AMAZON_ROOTFOLDER_NAME"] = "/customer/root/",
        ["BOLD_SERVICES_OCI_BUCKET_NAME"] = "existing-storage", ["BOLD_SERVICES_OCI_REGION"] = "us-ashburn-1",
        ["BOLD_SERVICES_OCI_ACCESS_KEY"] = "product-access", ["BOLD_SERVICES_OCI_SECRET_KEY"] = "product-secret",
        ["BOLD_SERVICES_OCI_ROOTFOLDER_NAME"] = "customer\\root", ["BOLD_SERVICES_OCI_NAME_SPACE"] = "namespace",
        ["BOLD_SERVICES_AZUREBLOB_CONTAINER_NAME"] = "existing-storage", ["BOLD_SERVICES_AZUREBLOB_ACCOUNT_NAME"] = "account"
    }).Build();

    private static ObjectStorageUpgradeCenterConfigurationProvider Provider(ObjectReader reader, RuntimeReader runtime, TimeProvider? clock = null) =>
        new(reader.Settings, reader, runtime, new BoldConnectionStringDecryptor(), clock ?? TimeProvider.System,
            NullLogger<ObjectStorageUpgradeCenterConfigurationProvider>.Instance);

    private sealed class ObjectReader(ProductConfigurationFixture fixture, StandaloneObjectStorageSettings settings) : IStandaloneObjectReader
    {
        public StandaloneObjectStorageSettings Settings => settings;
        public List<(string Key, string? ETag)> Requests { get; } = [];
        public bool Fail { get; set; }
        public bool Rotate { get; set; }
        public bool IgnoreVersionCondition { get; set; }
        public bool Timeout { get; set; }
        public string? PrivateKey { get; set; }
        public Task<StandaloneStorageObject> ReadAsync(string key, string? expectedETag, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (Timeout) throw new OperationCanceledException();
            Requests.Add((key, expectedETag));
            if (Fail || (Rotate && expectedETag is not null)) throw new InvalidOperationException("Storage read rejected.");
            return Task.FromResult(key == settings.ConfigurationKey
                ? new StandaloneStorageObject(fixture.Root.ToJsonString(), IgnoreVersionCondition && expectedETag is not null ? "config-v2" : "config-v1")
                : new StandaloneStorageObject(PrivateKey ?? File.ReadAllText(fixture.KeyPath), "key-v1"));
        }
    }

    private sealed class RuntimeReader : IStandaloneRuntimeConfigurationReader
    {
        public string? Json { get; set; }
        public string? Connection { get; private set; }
        public bool Fail { get; set; }
        public Task<string?> ReadAsync(UpgradeCenterRuntimeConfiguration bootstrap, string connectionString, CancellationToken cancellationToken)
        {
            Connection = connectionString;
            return Fail ? throw new InvalidOperationException("Database read rejected.") : Task.FromResult(Json);
        }
    }

    private sealed class TestClock : TimeProvider
    {
        private DateTimeOffset now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => now;
        public void Advance() => now = now.AddMinutes(1);
    }

    private sealed class S3Client() : AmazonS3Client(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1)
    {
        public GetObjectRequest? Request { get; private set; }
        public Exception? Failure { get; set; }
        public string Content { get; set; } = "content";
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            Request = request;
            if (Failure is not null) throw Failure;
            return Task.FromResult(new GetObjectResponse { ETag = "etag", ResponseStream = new MemoryStream(Encoding.UTF8.GetBytes(Content)) });
        }
    }

    private sealed class BlobContainer : BlobContainerClient
    {
        public string? RequestedKey { get; private set; }
        public Blob Blob { get; } = new();
        public override BlobClient GetBlobClient(string blobName) { RequestedKey = blobName; return Blob; }
    }

    private sealed class Blob : BlobClient
    {
        public BlobDownloadOptions? Options { get; private set; }
        public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(BlobDownloadOptions? options = null, CancellationToken cancellationToken = default)
        {
            Options = options;
            return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadStreamingResult(
                content: new MemoryStream(Encoding.UTF8.GetBytes("content")), details: BlobsModelFactory.BlobDownloadDetails(eTag: new ETag("etag"))), null!));
        }
    }

    private sealed class UnusedReader : IStandaloneObjectReader
    {
        public Task<StandaloneStorageObject> ReadAsync(string key, string? expectedETag, CancellationToken cancellationToken) => throw new NotSupportedException();
    }

    private sealed class HostEnvironment : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = ".";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}
