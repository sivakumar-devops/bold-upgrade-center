using Amazon.Runtime;
using Amazon.S3;
using Amazon.S3.Model;
using Azure;
using Azure.Identity;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace Bold.UpgradeCenter.Services;

public sealed record StandaloneStorageObject(string Content, string ETag);

public interface IStandaloneObjectReader
{
    Task<StandaloneStorageObject> ReadAsync(string key, string? expectedETag, CancellationToken cancellationToken);
}

public sealed class StandaloneObjectReader : IStandaloneObjectReader, IDisposable
{
    private const int MaximumObjectBytes = 4 * 1024 * 1024;
    private readonly StandaloneObjectStorageSettings settings;
    private readonly IAmazonS3? s3;
    private readonly BlobContainerClient? blobs;

    public StandaloneObjectReader(StandaloneObjectStorageSettings settings)
    {
        this.settings = settings;
        if (settings.Source == StandaloneConfigurationSource.AzureBlob)
        {
            blobs = ProductObjectStorageClientFactory.CreateBlobs(settings);
        }
        else
        {
            s3 = ProductObjectStorageClientFactory.CreateS3(settings);
        }
    }

    public StandaloneObjectReader(StandaloneObjectStorageSettings settings, IAmazonS3 client)
    {
        this.settings = settings;
        s3 = client;
    }

    public StandaloneObjectReader(StandaloneObjectStorageSettings settings, BlobContainerClient client)
    {
        this.settings = settings;
        blobs = client;
    }

    public async Task<StandaloneStorageObject> ReadAsync(string key, string? expectedETag, CancellationToken cancellationToken)
    {
        if (key != settings.ConfigurationKey && key != settings.PrivateKeyKey)
        {
            throw new InvalidOperationException("Standalone storage access is restricted to product configuration and private-key objects.");
        }

        try
        {
            if (blobs is not null)
            {
                var options = new BlobDownloadOptions
                {
                    Conditions = expectedETag is null ? null : new BlobRequestConditions { IfMatch = new ETag(expectedETag) }
                };
                var response = await blobs.GetBlobClient(key).DownloadStreamingAsync(options, cancellationToken);
                await using var content = response.Value.Content;
                return new StandaloneStorageObject(await ReadBoundedAsync(content, cancellationToken), response.Value.Details.ETag.ToString());
            }

            using var result = await s3!.GetObjectAsync(new GetObjectRequest
            {
                BucketName = settings.BucketName,
                Key = key,
                EtagToMatch = expectedETag
            }, cancellationToken);
            return new StandaloneStorageObject(await ReadBoundedAsync(result.ResponseStream, cancellationToken), result.ETag);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception) when (exception is AmazonServiceException or AmazonClientException or RequestFailedException or AuthenticationFailedException or
            HttpRequestException or IOException or InvalidOperationException or ArgumentException or OperationCanceledException)
        {
            // SDK errors may include signed URLs, credentials, and response bodies.
            throw new InvalidOperationException("Standalone object storage could not read a required object. Verify the provider, location, read permissions, network access, and that configuration/key objects are unchanged during loading.");
        }
    }

    private static async Task<string> ReadBoundedAsync(Stream source, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var bytes = new byte[8192];
        int count;
        while ((count = await source.ReadAsync(bytes, cancellationToken)) > 0)
        {
            if (buffer.Length + count > MaximumObjectBytes)
            {
                throw new InvalidOperationException("Standalone configuration object exceeds the allowed size.");
            }

            await buffer.WriteAsync(bytes.AsMemory(0, count), cancellationToken);
        }

        buffer.Position = 0;
        using var reader = new StreamReader(buffer);
        return await reader.ReadToEndAsync(cancellationToken);
    }

    public void Dispose() => s3?.Dispose();
}
