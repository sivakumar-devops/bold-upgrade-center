using Amazon;
using Amazon.Runtime;
using Amazon.S3;
using Azure.Identity;
using Azure.Storage;
using Azure.Storage.Blobs;

namespace Bold.UpgradeCenter.Services;

internal static class ProductObjectStorageClientFactory
{
    public static BlobContainerClient CreateBlobs(StandaloneObjectStorageSettings settings)
    {
        var options = new BlobClientOptions();
        options.Retry.MaxRetries = 2;
        options.Retry.NetworkTimeout = TimeSpan.FromSeconds(10);
        if (!string.IsNullOrWhiteSpace(settings.ConnectionString))
        {
            return new BlobContainerClient(settings.ConnectionString, settings.BucketName, options);
        }

        var protocol = string.Equals(settings.ConnectionType, "Http", StringComparison.OrdinalIgnoreCase) || settings.ConnectionType == "1" ? "http" : "https";
        var uri = new Uri(settings.ServiceUrl ?? $"{protocol}://{settings.AccountName}.blob.core.windows.net");
        return !string.IsNullOrWhiteSpace(settings.AccessKey)
            ? new BlobServiceClient(uri, new StorageSharedKeyCredential(settings.AccountName, settings.AccessKey), options).GetBlobContainerClient(settings.BucketName)
            : new BlobServiceClient(uri, new DefaultAzureCredential(), options).GetBlobContainerClient(settings.BucketName);
    }

    public static IAmazonS3 CreateS3(StandaloneObjectStorageSettings settings)
    {
        var options = new AmazonS3Config { MaxErrorRetry = 2, Timeout = TimeSpan.FromSeconds(10) };
        if (settings.Source == StandaloneConfigurationSource.OCIObjectStorage)
        {
            options.ServiceURL = $"https://{settings.Namespace}.compat.objectstorage.{settings.Region}.oraclecloud.com";
            options.AuthenticationRegion = settings.Region;
            options.ForcePathStyle = true;
        }
        else if (!string.IsNullOrWhiteSpace(settings.ServiceUrl))
        {
            options.ServiceURL = settings.ServiceUrl;
            options.AuthenticationRegion = settings.Region;
            options.ForcePathStyle = true;
        }
        else
        {
            options.RegionEndpoint = RegionEndpoint.GetBySystemName(settings.Region);
        }

        return string.IsNullOrWhiteSpace(settings.AccessKey)
            ? new AmazonS3Client(options)
            : new AmazonS3Client(string.IsNullOrWhiteSpace(settings.SessionToken)
                ? new BasicAWSCredentials(settings.AccessKey, settings.SecretKey)
                : new SessionAWSCredentials(settings.AccessKey, settings.SecretKey, settings.SessionToken), options);
    }
}
