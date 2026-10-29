using System.Net;
using System.Text;
using System.Xml.Linq;
using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;
using Bold.UpgradeCenter.Services;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Bold.UpgradeCenter.Tests;

public sealed class ObjectStoragePersistenceTests
{
    [Theory]
    [InlineData(StandaloneConfigurationSource.AmazonS3, "customer/root/app_data/upgrade-center/")]
    [InlineData(StandaloneConfigurationSource.AzureBlob, "boldservices/app_data/upgrade-center/")]
    [InlineData(StandaloneConfigurationSource.OCIObjectStorage, "customer/root/app_data/upgrade-center/")]
    public void StateIsSeparatedFromProductConfiguration(StandaloneConfigurationSource source, string expected)
    {
        var settings = Settings(source);
        Assert.Equal(expected, settings.UpgradeCenterPrefix);
        Assert.False(settings.ConfigurationKey.StartsWith(expected));
        Assert.False(settings.PrivateKeyKey.StartsWith(expected));
    }

    [Theory]
    [InlineData("../configuration/config.json")]
    [InlineData("app_data/configuration/privatekeys.dat")]
    [InlineData("playwright-reports/../pre-upgrade/report.html")]
    [InlineData("playwright-reports/job/../../config.json")]
    [InlineData("data-protection-keys/not-a-key.xml")]
    public async Task WritesAndDeletesCannotReachProductFiles(string key)
    {
        using var client = new S3();
        using var store = new UpgradeCenterObjectStore(Settings(), client);
        using var input = new MemoryStream([1]);
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(key, input));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteReportAsync(key));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync(key));
        Assert.Empty(client.Requests);
    }

    [Theory]
    [InlineData(StandaloneConfigurationSource.AmazonS3)]
    [InlineData(StandaloneConfigurationSource.OCIObjectStorage)]
    public async Task S3CompatibleOperationsUseOnlyTheDedicatedPrefix(StandaloneConfigurationSource source)
    {
        var settings = Settings(source);
        using var client = new S3();
        using var store = new UpgradeCenterObjectStore(settings, client);
        const string key = "playwright-reports/job/pre-upgrade/report.html";
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("report"));
        await store.WriteAsync(key, content);
        Assert.True(await store.ExistsAsync(key));
        Assert.Equal("report", Encoding.UTF8.GetString((await store.ReadAsync(key))!));
        Assert.Single(await store.ListAsync("playwright-reports/"));
        await store.DeleteReportAsync(key);
        Assert.Null(await store.ReadAsync(key));
        Assert.False(await store.ExistsAsync(key));
        Assert.All(client.Requests, item => Assert.StartsWith(settings.UpgradeCenterPrefix, item));
    }

    [Fact]
    public async Task S3ListingIncludesAllPages()
    {
        var settings = Settings();
        using var client = new S3 { Paginate = true };
        client.Objects[settings.UpgradeCenterPrefix + "playwright-reports/old/pre-upgrade/report.html"] = [1];
        client.Objects[settings.UpgradeCenterPrefix + "playwright-reports/new/pre-upgrade/report.html"] = [2];
        using var store = new UpgradeCenterObjectStore(settings, client);
        Assert.Equal(2, (await store.ListAsync("playwright-reports/")).Count);
        Assert.Equal(2, client.ListCalls);
    }

    [Fact]
    public async Task KeyFilesCannotBeOverwrittenOrDeleted()
    {
        using var client = new S3();
        using var store = new UpgradeCenterObjectStore(Settings(), client);
        var key = $"data-protection-keys/{Guid.NewGuid():N}.xml";
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("<key />"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(key, content));
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteReportAsync(key));
        await store.WriteAsync(key, content, createOnly: true);
        Assert.Equal("*", client.LastPut!.IfNoneMatch);
        content.Position = 0;
        await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync(key, content, createOnly: true));
        Assert.Single(client.Objects);
    }

    [Fact]
    public async Task AzureOperationsAreScopedAndKeyCreationIsConditional()
    {
        var settings = Settings(StandaloneConfigurationSource.AzureBlob);
        var container = new Blobs();
        using var store = new UpgradeCenterObjectStore(settings, container);
        var key = $"data-protection-keys/{Guid.NewGuid():N}.xml";
        using var content = new MemoryStream(Encoding.UTF8.GetBytes("<key />"));
        await store.WriteAsync(key, content, createOnly: true);
        Assert.Equal(ETag.All, container.Blob.Options!.Conditions.IfNoneMatch);
        Assert.True(await store.ExistsAsync(key));
        Assert.NotNull(await store.ReadAsync(key));
        Assert.Single(await store.ListAsync("data-protection-keys/"));
        await store.DeleteReportAsync("playwright-reports/job/pre-upgrade/report.html");
        Assert.All(container.Requests, item => Assert.StartsWith(settings.UpgradeCenterPrefix, item));
    }

    [Fact]
    public async Task StorageErrorsDoNotLeakCredentialsOrFallBack()
    {
        using var client = new S3 { Failure = new AmazonS3Exception("secret signed-url") { StatusCode = HttpStatusCode.Forbidden } };
        using var store = new UpgradeCenterObjectStore(Settings(), client);
        using var stream = new MemoryStream([1]);
        var read = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ReadAsync("playwright-reports/job/pre-upgrade/report.html"));
        var write = await Assert.ThrowsAsync<InvalidOperationException>(() => store.WriteAsync("playwright-reports/job/pre-upgrade/report.html", stream));
        var list = await Assert.ThrowsAsync<InvalidOperationException>(() => store.ListAsync("data-protection-keys/"));
        var delete = await Assert.ThrowsAsync<InvalidOperationException>(() => store.DeleteReportAsync("playwright-reports/job/pre-upgrade/report.html"));
        foreach (var error in new[] { read, write, list, delete })
        {
            Assert.DoesNotContain("secret signed-url", error.ToString());
            Assert.Null(error.InnerException);
        }
    }

    [Fact]
    public void AuthenticationKeysSurviveAServiceRestartWithoutLocalFiles()
    {
        var storage = new MemoryUpgradeCenterObjectStore();
        string encrypted;
        using (var first = Persistence(storage))
            encrypted = first.GetRequiredService<IDataProtectionProvider>().CreateProtector("test-cookie").Protect("session");
        using (var second = Persistence(storage))
        {
            Assert.IsType<ObjectStoragePlaywrightReportStore>(second.GetRequiredService<IPlaywrightReportStore>());
            Assert.IsType<ObjectStorageXmlRepository>(second.GetRequiredService<IOptions<KeyManagementOptions>>().Value.XmlRepository);
            Assert.Equal("session", second.GetRequiredService<IDataProtectionProvider>().CreateProtector("test-cookie").Unprotect(encrypted));
        }
        Assert.NotEmpty(storage.Objects);
        Assert.All(storage.Objects.Keys, key => Assert.StartsWith("data-protection-keys/", key));
    }

    [Fact]
    public void MissingOrMalformedKeyObjectsDoNotProduceAnEmptyKeyRing()
    {
        var storage = new MemoryUpgradeCenterObjectStore();
        var key = $"data-protection-keys/{Guid.NewGuid():N}.xml";
        storage.Objects[key] = Encoding.UTF8.GetBytes("<key");
        Assert.Throws<InvalidOperationException>(() => new ObjectStorageXmlRepository(storage).GetAllElements());
        storage.ReturnMissing = true;
        Assert.Throws<InvalidOperationException>(() => new ObjectStorageXmlRepository(storage).GetAllElements());
    }

    [Fact]
    public void KeyRepositoryPersistsXmlWithoutChangingProductFiles()
    {
        var storage = new MemoryUpgradeCenterObjectStore();
        var repository = new ObjectStorageXmlRepository(storage);
        repository.StoreElement(XElement.Parse("<key id='test' />"), "../../config.json");
        Assert.Equal("test", repository.GetAllElements().Single().Attribute("id")!.Value);
        Assert.All(storage.Objects.Keys, key => Assert.StartsWith("data-protection-keys/", key));
    }

    [Fact]
    public void ReportsSurviveRestartAndRetentionDoesNotDeleteKeysOrTheCurrentJob()
    {
        var storage = new MemoryUpgradeCenterObjectStore();
        var key = $"data-protection-keys/{Guid.NewGuid():N}.xml";
        storage.Objects[key] = Encoding.UTF8.GetBytes("key");
        var first = Reports(storage, 1);
        using var old = new MemoryStream(Encoding.UTF8.GetBytes("old report"));
        Assert.True(first.TrySaveHtml("old", PlaywrightValidationMode.Pre, old, out _));
        using var current = new MemoryStream(Encoding.UTF8.GetBytes("current report"));
        Assert.True(Reports(storage, 1).TrySaveHtml("current", PlaywrightValidationMode.Post, current, out _));
        var restarted = Reports(storage, 1);
        Assert.False(restarted.Exists("old", PlaywrightValidationMode.Pre));
        Assert.True(restarted.TryOpenRead("current", PlaywrightValidationMode.Post, out var content));
        using (content) using (var reader = new StreamReader(content!)) Assert.Equal("current report", reader.ReadToEnd());
        Assert.True(storage.Objects.ContainsKey(key));
        Assert.All(storage.Deleted, item => Assert.StartsWith("playwright-reports/old/", item));
        Assert.False(restarted.TryOpenRead("current", PlaywrightValidationMode.Post, "../../config.json", out _, out _));
    }

    [Fact]
    public void LogReportsAndRetentionErrorsPreserveUploadSuccess()
    {
        var storage = new MemoryUpgradeCenterObjectStore { FailDelete = true };
        storage.Objects["playwright-reports/old/pre-upgrade/report.html"] = [1];
        var logs = PlaywrightReportStore.ReportBeginMarker + Convert.ToBase64String(Encoding.UTF8.GetBytes("report")) + PlaywrightReportStore.ReportEndMarker;
        var reports = Reports(storage, 1);
        Assert.True(reports.TrySaveFromLogs("new", PlaywrightValidationMode.Cleanup, logs));
        Assert.True(reports.Exists("new", PlaywrightValidationMode.Cleanup));
        storage.Fail = true;
        using var stream = new MemoryStream([1]);
        Assert.False(reports.TrySaveHtml("new", PlaywrightValidationMode.Post, stream, out var error));
        Assert.DoesNotContain("secret", error!);
        Assert.False(reports.Exists("new", PlaywrightValidationMode.Post));
    }

    [Fact]
    public void NoPvWithoutObjectStorageFailsInsteadOfCreatingLocalState()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["BOLD_SERVICES_PV_USED"] = "false" }).Build();
        Assert.Throws<InvalidOperationException>(() => new ServiceCollection().AddUpgradeCenterPersistence(configuration, new Host()));
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DurableKeyStorageFailureBlocksStartup(bool failRead, bool failWrite)
    {
        using var services = Persistence(new MemoryUpgradeCenterObjectStore { Fail = failRead, FailWrite = failWrite });
        var startup = services.GetServices<IHostedService>().Single(service => service.GetType().Name == "UpgradeCenterPersistenceValidationService");
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => startup.StartAsync(default));
        Assert.DoesNotContain("secret", error.ToString());
        Assert.Null(error.InnerException);
    }

    [Fact]
    public void PvDeploymentsKeepTheExistingFilesystemReportAndKeyStores()
    {
        var root = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
        try
        {
            var services = new ServiceCollection();
            services.AddLogging();
            services.AddUpgradeCenterPersistence(new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["BOLD_SERVICES_PV_USED"] = "true" }).Build(), new Host { ContentRootPath = root });
            Assert.Contains(services, item => item.ServiceType == typeof(IPlaywrightReportStore) && item.ImplementationType == typeof(PlaywrightReportStore));
            Assert.DoesNotContain(services, item => item.ServiceType == typeof(IUpgradeCenterObjectStore));
            Assert.True(Directory.Exists(Path.Combine(root, "App_Data", "DataProtectionKeys")));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }

    private static ServiceProvider Persistence(MemoryUpgradeCenterObjectStore store)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["BOLD_SERVICES_PV_USED"] = "false", ["BOLD_SERVICES_STORAGETYPE"] = "3",
            ["BOLD_SERVICES_AMAZON_BUCKET_NAME"] = "product", ["BOLD_SERVICES_AWS_REGION"] = "us-east-1"
        }).Build();
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddOptions<PlaywrightExecutionOptions>();
        services.AddUpgradeCenterPersistence(configuration, new Host());
        services.AddSingleton<IUpgradeCenterObjectStore>(store);
        return services.BuildServiceProvider();
    }

    private static ObjectStoragePlaywrightReportStore Reports(MemoryUpgradeCenterObjectStore store, int retained) =>
        new(store, Options.Create(new PlaywrightExecutionOptions { ReportRetentionJobCount = retained }), NullLogger<ObjectStoragePlaywrightReportStore>.Instance);

    private static StandaloneObjectStorageSettings Settings(StandaloneConfigurationSource source = StandaloneConfigurationSource.AmazonS3) =>
        new() { Source = source, BucketName = "product", RootFolderName = "customer/root", Region = "us-east-1" };

    private sealed class S3() : AmazonS3Client(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1)
    {
        public Dictionary<string, byte[]> Objects { get; } = [];
        public List<string> Requests { get; } = [];
        public PutObjectRequest? LastPut { get; private set; }
        public Exception? Failure { get; set; }
        public bool Paginate { get; set; }
        public int ListCalls { get; private set; }
        public override Task<GetObjectResponse> GetObjectAsync(GetObjectRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Key);
            if (Failure is not null) throw Failure;
            if (!Objects.TryGetValue(request.Key, out var data)) throw new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound };
            return Task.FromResult(new GetObjectResponse { ResponseStream = new MemoryStream(data) });
        }
        public override Task<GetObjectMetadataResponse> GetObjectMetadataAsync(GetObjectMetadataRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Key);
            if (Failure is not null) throw Failure;
            if (!Objects.ContainsKey(request.Key)) throw new AmazonS3Exception("missing") { StatusCode = HttpStatusCode.NotFound };
            return Task.FromResult(new GetObjectMetadataResponse());
        }
        public override async Task<PutObjectResponse> PutObjectAsync(PutObjectRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Key); LastPut = request;
            if (Failure is not null) throw Failure;
            if (request.IfNoneMatch == "*" && Objects.ContainsKey(request.Key)) throw new AmazonS3Exception("exists") { StatusCode = HttpStatusCode.PreconditionFailed };
            using var buffer = new MemoryStream();
            await request.InputStream.CopyToAsync(buffer, cancellationToken);
            Objects[request.Key] = buffer.ToArray();
            return new PutObjectResponse();
        }
        public override Task<ListObjectsV2Response> ListObjectsV2Async(ListObjectsV2Request request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Prefix); ListCalls++;
            if (Failure is not null) throw Failure;
            var all = Objects.Keys.Where(key => key.StartsWith(request.Prefix)).ToList();
            var skip = request.ContinuationToken is null ? 0 : int.Parse(request.ContinuationToken);
            var page = Paginate ? all.Skip(skip).Take(1).ToList() : all;
            var next = skip + page.Count;
            return Task.FromResult(new ListObjectsV2Response { S3Objects = page.Select(key => new S3Object { Key = key, LastModified = DateTime.UtcNow }).ToList(), IsTruncated = Paginate && next < all.Count, NextContinuationToken = next.ToString() });
        }
        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default)
        {
            Requests.Add(request.Key);
            if (Failure is not null) throw Failure;
            Objects.Remove(request.Key);
            return Task.FromResult(new DeleteObjectResponse());
        }
    }

    private sealed class Blobs : BlobContainerClient
    {
        public List<string> Requests { get; } = [];
        public Blob Blob { get; } = new();
        public override BlobClient GetBlobClient(string blobName) { Requests.Add(blobName); return Blob; }
        public override AsyncPageable<BlobItem> GetBlobsAsync(BlobTraits traits, BlobStates states, string prefix, CancellationToken cancellationToken)
        {
            Requests.Add(prefix);
            return AsyncPageable<BlobItem>.FromPages([Page<BlobItem>.FromValues([BlobsModelFactory.BlobItem(name: prefix + new string('a', 32) + ".xml", properties: BlobsModelFactory.BlobItemProperties(false, lastModified: DateTimeOffset.UtcNow))], null, null!)]);
        }
    }

    private sealed class Blob : BlobClient
    {
        public BlobUploadOptions? Options { get; private set; }
        public override Task<Response<BlobContentInfo>> UploadAsync(Stream content, BlobUploadOptions options, CancellationToken cancellationToken = default)
        { Options = options; return Task.FromResult(Response.FromValue(BlobsModelFactory.BlobContentInfo(new ETag("test"), DateTimeOffset.UtcNow, [], "version", 0), null!)); }
        public override Task<Response<BlobDownloadStreamingResult>> DownloadStreamingAsync(BlobDownloadOptions? options = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(BlobsModelFactory.BlobDownloadStreamingResult(content: new MemoryStream([1]), details: BlobsModelFactory.BlobDownloadDetails()), null!));
        public override Task<Response<bool>> DeleteIfExistsAsync(DeleteSnapshotsOption snapshotsOption = DeleteSnapshotsOption.IncludeSnapshots, BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(true, null!));
        public override Task<Response<BlobProperties>> GetPropertiesAsync(BlobRequestConditions? conditions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(Response.FromValue(BlobsModelFactory.BlobProperties(), null!));
    }

    private sealed class Host : IHostEnvironment
    {
        public string EnvironmentName { get; set; } = "Development";
        public string ApplicationName { get; set; } = "Tests";
        public string ContentRootPath { get; set; } = "not-used-for-object-storage";
        public Microsoft.Extensions.FileProviders.IFileProvider ContentRootFileProvider { get; set; } = new Microsoft.Extensions.FileProviders.NullFileProvider();
    }
}

internal sealed class MemoryUpgradeCenterObjectStore : IUpgradeCenterObjectStore
{
    public Dictionary<string, byte[]> Objects { get; } = [];
    public List<string> Deleted { get; } = [];
    private readonly Dictionary<string, DateTimeOffset> modified = [];
    public bool Fail { get; set; }
    public bool FailDelete { get; set; }
    public bool FailWrite { get; set; }
    public bool ReturnMissing { get; set; }
    public Task<bool> ExistsAsync(string key) => Fail ? throw new InvalidOperationException("secret") : Task.FromResult(!ReturnMissing && Objects.ContainsKey(key));
    public Task<byte[]?> ReadAsync(string key) => Fail ? throw new InvalidOperationException("secret") : Task.FromResult(ReturnMissing ? null : Objects.GetValueOrDefault(key));
    public async Task WriteAsync(string key, Stream content, bool createOnly = false)
    {
        if (Fail || FailWrite || (createOnly && Objects.ContainsKey(key))) throw new InvalidOperationException("secret");
        using var buffer = new MemoryStream();
        await content.CopyToAsync(buffer);
        Objects[key] = buffer.ToArray();
        modified[key] = DateTimeOffset.UtcNow;
    }
    public Task<IReadOnlyList<UpgradeCenterStoredObject>> ListAsync(string prefix) => Fail ? throw new InvalidOperationException("secret") :
        Task.FromResult<IReadOnlyList<UpgradeCenterStoredObject>>(Objects.Keys.Where(key => key.StartsWith(prefix)).Select(key => new UpgradeCenterStoredObject(key, modified.GetValueOrDefault(key))).ToList());
    public Task DeleteReportAsync(string key)
    {
        if (Fail || FailDelete) throw new InvalidOperationException("secret");
        Deleted.Add(key); Objects.Remove(key); return Task.CompletedTask;
    }
}
