using System.Net;
using System.Text.RegularExpressions;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Bold.UpgradeCenter.Services;

public sealed record UpgradeCenterStoredObject(string Key, DateTimeOffset Modified);

public interface IUpgradeCenterObjectStore
{
    Task<bool> ExistsAsync(string key);
    Task<byte[]?> ReadAsync(string key);
    Task WriteAsync(string key, Stream content, bool createOnly = false);
    Task<IReadOnlyList<UpgradeCenterStoredObject>> ListAsync(string prefix);
    Task DeleteReportAsync(string key);
}

// All keys are relative to the product root's dedicated app_data/upgrade-center/ prefix.
// Product configuration/private-key objects are never reachable through this writable client.
public sealed class UpgradeCenterObjectStore : IUpgradeCenterObjectStore, IDisposable
{
    private static readonly Regex ReportKey = new(@"\Aplaywright-reports/[A-Za-z0-9_.-]+/(pre-upgrade|post-upgrade|cleanup)/report\.html\z", RegexOptions.Compiled);
    private static readonly Regex KeyFile = new(@"\Adata-protection-keys/[a-f0-9]{32}\.xml\z", RegexOptions.Compiled);
    private readonly StandaloneObjectStorageSettings settings;
    private readonly IAmazonS3? s3;
    private readonly BlobContainerClient? blobs;
    private readonly bool ownsClient;

    public UpgradeCenterObjectStore(StandaloneObjectStorageSettings settings)
    {
        this.settings = settings;
        if (settings.Source == StandaloneConfigurationSource.AzureBlob)
            blobs = ProductObjectStorageClientFactory.CreateBlobs(settings);
        else
            s3 = ProductObjectStorageClientFactory.CreateS3(settings);
        ownsClient = true;
    }

    public UpgradeCenterObjectStore(StandaloneObjectStorageSettings settings, IAmazonS3 client) { this.settings = settings; s3 = client; }
    public UpgradeCenterObjectStore(StandaloneObjectStorageSettings settings, BlobContainerClient client) { this.settings = settings; blobs = client; }

    public async Task<bool> ExistsAsync(string key)
    {
        ValidateKey(key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            if (blobs is not null)
                await blobs.GetBlobClient(FullKey(key)).GetPropertiesAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            else
                await s3!.GetObjectMetadataAsync(new GetObjectMetadataRequest { BucketName = settings.BucketName, Key = FullKey(key) }, timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound && exception.ErrorCode != "NoSuchBucket") { return false; }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == "BlobNotFound") { return false; }
        catch (Exception) { throw StorageError(); }
    }

    public async Task<byte[]?> ReadAsync(string key)
    {
        ValidateKey(key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            Stream content;
            IDisposable response;
            if (blobs is not null)
            {
                var result = await blobs.GetBlobClient(FullKey(key)).DownloadStreamingAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
                response = content = result.Value.Content;
            }
            else
            {
                var result = await s3!.GetObjectAsync(new GetObjectRequest { BucketName = settings.BucketName, Key = FullKey(key) }, timeout.Token).ConfigureAwait(false);
                response = result;
                content = result.ResponseStream;
            }
            using (response)
            using (var buffer = new MemoryStream())
            {
                var bytes = new byte[81920];
                int count;
                while ((count = await content.ReadAsync(bytes, timeout.Token).ConfigureAwait(false)) > 0)
                {
                    if (KeyFile.IsMatch(key) && buffer.Length + count > 1024 * 1024)
                        throw new InvalidOperationException("Upgrade Center key object exceeds the allowed size.");
                    await buffer.WriteAsync(bytes.AsMemory(0, count), timeout.Token).ConfigureAwait(false);
                }
                return buffer.ToArray();
            }
        }
        catch (AmazonS3Exception exception) when (exception.StatusCode == HttpStatusCode.NotFound && exception.ErrorCode != "NoSuchBucket") { return null; }
        catch (RequestFailedException exception) when (exception.Status == 404 && exception.ErrorCode == "BlobNotFound") { return null; }
        catch (Exception) { throw StorageError(); }
    }

    public async Task WriteAsync(string key, Stream content, bool createOnly = false)
    {
        ValidateKey(key);
        if (KeyFile.IsMatch(key) && !createOnly)
            throw new InvalidOperationException("Upgrade Center authentication keys cannot be overwritten.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            if (blobs is not null)
            {
                await blobs.GetBlobClient(FullKey(key)).UploadAsync(content, new BlobUploadOptions
                {
                    Conditions = createOnly ? new BlobRequestConditions { IfNoneMatch = ETag.All } : null,
                    HttpHeaders = new BlobHttpHeaders { ContentType = createOnly ? "application/xml" : "text/html; charset=utf-8" }
                }, timeout.Token).ConfigureAwait(false);
            }
            else
            {
                await s3!.PutObjectAsync(new PutObjectRequest
                {
                    BucketName = settings.BucketName,
                    Key = FullKey(key),
                    InputStream = content,
                    AutoCloseStream = false,
                    ContentType = createOnly ? "application/xml" : "text/html; charset=utf-8",
                    IfNoneMatch = createOnly ? "*" : null
                }, timeout.Token).ConfigureAwait(false);
            }
        }
        catch (Exception) { throw StorageError(); }
    }

    public async Task<IReadOnlyList<UpgradeCenterStoredObject>> ListAsync(string prefix)
    {
        if (prefix is not ("data-protection-keys/" or "playwright-reports/"))
            throw new InvalidOperationException("Upgrade Center listing is restricted to its own reports and keys.");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        var objects = new List<UpgradeCenterStoredObject>();
        try
        {
            if (blobs is not null)
            {
                await foreach (var blob in blobs.GetBlobsAsync(BlobTraits.None, BlobStates.None, FullKey(prefix), timeout.Token).ConfigureAwait(false))
                    Add(blob.Name, blob.Properties.LastModified ?? DateTimeOffset.MinValue);
            }
            else
            {
                string? token = null;
                do
                {
                    var page = await s3!.ListObjectsV2Async(new ListObjectsV2Request
                    {
                        BucketName = settings.BucketName, Prefix = FullKey(prefix), ContinuationToken = token
                    }, timeout.Token).ConfigureAwait(false);
                    foreach (var item in page.S3Objects ?? [])
                        Add(item.Key, item.LastModified is { } modified ? new DateTimeOffset(DateTime.SpecifyKind(modified, DateTimeKind.Utc)) : DateTimeOffset.MinValue);
                    if (page.IsTruncated != true) break;
                    if (string.IsNullOrEmpty(page.NextContinuationToken) || page.NextContinuationToken == token)
                        throw new InvalidOperationException("Storage pagination did not advance.");
                    token = page.NextContinuationToken;
                } while (true);
            }
            return objects;
        }
        catch (Exception) { throw StorageError(); }

        void Add(string fullKey, DateTimeOffset modified)
        {
            if (!fullKey.StartsWith(FullKey(prefix), StringComparison.Ordinal))
                throw new InvalidOperationException("Storage returned an object outside the requested prefix.");
            var key = fullKey[settings.UpgradeCenterPrefix.Length..];
            ValidateKey(key);
            objects.Add(new(key, modified));
        }
    }

    public async Task DeleteReportAsync(string key)
    {
        if (!ReportKey.IsMatch(key))
            throw new InvalidOperationException("Upgrade Center can delete only its own Playwright reports.");
        ValidateKey(key);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(60));
        try
        {
            if (blobs is not null)
                await blobs.GetBlobClient(FullKey(key)).DeleteIfExistsAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
            else
                await s3!.DeleteObjectAsync(new DeleteObjectRequest { BucketName = settings.BucketName, Key = FullKey(key) }, timeout.Token).ConfigureAwait(false);
        }
        catch (Exception) { throw StorageError(); }
    }

    private static void ValidateKey(string key)
    {
        if ((!ReportKey.IsMatch(key) && !KeyFile.IsMatch(key)) || key.Split('/').Any(part => part is "." or ".."))
            throw new InvalidOperationException("Upgrade Center storage access is restricted to its own reports and keys.");
    }

    private string FullKey(string key) => settings.UpgradeCenterPrefix + key;
    private static InvalidOperationException StorageError() => new("Upgrade Center object storage could not complete the operation. Verify connectivity and read/list/write permissions for its dedicated prefix, and delete permission for report retention. No local-storage fallback is used.");
    public void Dispose() { if (ownsClient) s3?.Dispose(); }
}
