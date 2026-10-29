namespace Bold.UpgradeCenter.Services;

public enum StandaloneConfigurationSource
{
    FileSystem,
    AmazonS3,
    AzureBlob,
    OCIObjectStorage
}

public sealed class StandaloneObjectStorageSettings
{
    public StandaloneConfigurationSource Source { get; init; }
    public string BucketName { get; init; } = string.Empty;
    public string Region { get; init; } = string.Empty;
    public string RootFolderName { get; init; } = string.Empty;
    public string? AccessKey { get; init; }
    public string? SecretKey { get; init; }
    public string? SessionToken { get; init; }
    public string? Namespace { get; init; }
    public string? AccountName { get; init; }
    public string? ServiceUrl { get; init; }
    public string? ConnectionString { get; init; }
    public string? ConnectionType { get; init; }

    public string ConfigurationKey => ObjectKey("config.json");
    public string PrivateKeyKey => ObjectKey("privatekeys.dat");

    private string ProductRoot => Source == StandaloneConfigurationSource.AzureBlob ? "boldservices" : RootFolderName;

    public string UpgradeCenterPrefix => string.Join('/', new[] { ProductRoot, "app_data", "upgrade-center" }.Where(part => part.Length > 0)) + "/";

    public static StandaloneConfigurationSource ParseSource(string? source) => source?.Trim().ToLowerInvariant() switch
    {
        null or "filesystem" => StandaloneConfigurationSource.FileSystem,
        "amazons3" => StandaloneConfigurationSource.AmazonS3,
        "azureblob" => StandaloneConfigurationSource.AzureBlob,
        "ociobjectstorage" => StandaloneConfigurationSource.OCIObjectStorage,
        _ => throw new InvalidOperationException("UpgradeCenter:Configuration:Source must be FileSystem, AmazonS3, AzureBlob, or OCIObjectStorage.")
    };

    public static StandaloneConfigurationSource ResolveSource(IConfiguration configuration)
    {
        // Preserve explicit application settings for older deployments; new charts use the product setting.
        if (configuration["UpgradeCenter:Configuration:Source"] is { } configuredSource)
        {
            return ParseSource(configuredSource);
        }

        return configuration["BOLD_SERVICES_STORAGETYPE"]?.Trim().ToLowerInvariant() switch
        {
            null or "" or "0" or "filestorage" or "filesystem" => StandaloneConfigurationSource.FileSystem,
            "1" or "azureblob" => StandaloneConfigurationSource.AzureBlob,
            "3" or "amazons3" => StandaloneConfigurationSource.AmazonS3,
            "4" or "ociobjectstorage" => StandaloneConfigurationSource.OCIObjectStorage,
            _ => throw new InvalidOperationException("Standalone configuration requires the product's FileStorage, AzureBlob, AmazonS3, or OCIObjectStorage setting in BOLD_SERVICES_STORAGETYPE.")
        };
    }

    public static StandaloneObjectStorageSettings FromConfiguration(IConfiguration configuration)
    {
        var source = ResolveSource(configuration);
        string? Setting(string name, string? productVariable = null) =>
            configuration[$"UpgradeCenter:Configuration:ObjectStorage:{name}"] is { Length: > 0 } value
                ? value : productVariable is null ? null : configuration[productVariable];
        var identitySetting = Setting("UseWorkloadIdentity");
        if (identitySetting is not null && !bool.TryParse(identitySetting, out _))
        {
            throw new InvalidOperationException("Standalone ObjectStorage:UseWorkloadIdentity must be true or false.");
        }
        var useIdentity = bool.TryParse(identitySetting, out var identity) && identity;
        var connectionType = Setting("ConnectionType", "BOLD_SERVICES_AZUREBLOB_CONNECTION_TYPE");
        var prefix = source switch
        {
            StandaloneConfigurationSource.AmazonS3 => "BOLD_SERVICES_AMAZON_",
            StandaloneConfigurationSource.OCIObjectStorage => "BOLD_SERVICES_OCI_",
            _ => "BOLD_SERVICES_AZUREBLOB_"
        };
        var settings = new StandaloneObjectStorageSettings
        {
            Source = source,
            BucketName = Setting("BucketName", prefix + (source == StandaloneConfigurationSource.AzureBlob ? "CONTAINER_NAME" : "BUCKET_NAME")) ?? string.Empty,
            Region = Setting("Region", source == StandaloneConfigurationSource.AmazonS3 ? "BOLD_SERVICES_AWS_REGION" : "BOLD_SERVICES_OCI_REGION") ?? string.Empty,
            RootFolderName = (Setting("RootFolderName", prefix + "ROOTFOLDER_NAME") ?? string.Empty).Replace('\\', '/').Trim('/'),
            AccessKey = useIdentity ? null : Setting("AccessKey", source switch
            {
                StandaloneConfigurationSource.AmazonS3 => "BOLD_SERVICES_AWS_ACCESS_KEY_ID",
                StandaloneConfigurationSource.OCIObjectStorage => "BOLD_SERVICES_OCI_ACCESS_KEY",
                _ => "BOLD_SERVICES_AZUREBLOB_ACCESSKEY"
            }),
            SecretKey = useIdentity ? null : Setting("SecretKey", source == StandaloneConfigurationSource.AmazonS3 ? "BOLD_SERVICES_AWS_SECRET_ACCESS_KEY" : "BOLD_SERVICES_OCI_SECRET_KEY"),
            SessionToken = useIdentity ? null : Setting("SessionToken", "AWS_SESSION_TOKEN"),
            Namespace = Setting("Namespace", "BOLD_SERVICES_OCI_NAME_SPACE"),
            AccountName = Setting("AccountName", "BOLD_SERVICES_AZUREBLOB_ACCOUNT_NAME"),
            ServiceUrl = Setting("ServiceUrl", source == StandaloneConfigurationSource.AzureBlob &&
                !string.Equals(connectionType, "Https", StringComparison.OrdinalIgnoreCase) && connectionType != "0" &&
                !string.Equals(connectionType, "Http", StringComparison.OrdinalIgnoreCase) && connectionType != "1"
                ? "BOLD_SERVICES_AZUREBLOB_STORAGE_URI" : null),
            ConnectionString = useIdentity ? null : Setting("ConnectionString"),
            ConnectionType = connectionType
        };
        settings.Validate();
        return settings;
    }

    private string ObjectKey(string name)
    {
        return string.Join('/', new[] { ProductRoot, "app_data", "configuration", name }.Where(part => part.Length > 0));
    }

    private void Validate()
    {
        if (Source == StandaloneConfigurationSource.FileSystem)
        {
            throw new InvalidOperationException("Object storage requires an explicit object-storage configuration source.");
        }

        if (string.IsNullOrWhiteSpace(BucketName))
        {
            throw new InvalidOperationException("Standalone object storage requires the product bucket/container setting.");
        }

        if (Source is StandaloneConfigurationSource.AmazonS3 or StandaloneConfigurationSource.OCIObjectStorage)
        {
            if (string.IsNullOrWhiteSpace(Region) || string.IsNullOrWhiteSpace(AccessKey) != string.IsNullOrWhiteSpace(SecretKey))
            {
                throw new InvalidOperationException("Standalone object storage requires a region and either both access credentials or a supported workload identity.");
            }
        }

        if (Source == StandaloneConfigurationSource.OCIObjectStorage &&
            (string.IsNullOrWhiteSpace(Namespace) || string.IsNullOrWhiteSpace(AccessKey) || string.IsNullOrWhiteSpace(SecretKey)))
        {
            throw new InvalidOperationException("Standalone OCI storage requires a namespace and S3-compatible access credentials.");
        }

        if (Source == StandaloneConfigurationSource.AzureBlob && string.IsNullOrWhiteSpace(ConnectionString) &&
            string.IsNullOrWhiteSpace(AccountName) && string.IsNullOrWhiteSpace(ServiceUrl))
        {
            throw new InvalidOperationException("Standalone Azure storage requires an account name, service URL, or protected connection string.");
        }

        if (Source == StandaloneConfigurationSource.AzureBlob &&
            !string.IsNullOrWhiteSpace(AccessKey) && string.IsNullOrWhiteSpace(AccountName))
        {
            throw new InvalidOperationException("Standalone Azure storage requires the product account name when shared-key authentication is used.");
        }
    }
}
