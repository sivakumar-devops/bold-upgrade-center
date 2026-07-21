using System.Text.Json;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class IdpConfigurationProvider : IUpgradeCenterConfigurationProvider
{
    public const string HttpClientName = "UpgradeCenterIdpConfiguration";

    private static readonly TimeSpan CacheDuration = TimeSpan.FromMinutes(5);

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IConfiguration configuration;
    private readonly IOptions<UpgradeCenterOptions> options;
    private readonly ILogger<IdpConfigurationProvider> logger;
    private readonly SemaphoreSlim refreshLock = new(1, 1);
    private UpgradeCenterRuntimeConfiguration? cachedConfiguration;
    private UpgradeCenterRuntimeConfiguration? lastKnownGoodConfiguration;
    private DateTimeOffset cacheExpiresAt;

    public IdpConfigurationProvider(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IOptions<UpgradeCenterOptions> options,
        ILogger<IdpConfigurationProvider> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.configuration = configuration;
        this.options = options;
        this.logger = logger;
    }

    public async Task<UpgradeCenterRuntimeConfiguration> GetConfigurationAsync(CancellationToken cancellationToken = default)
    {
        if (cachedConfiguration is not null && cacheExpiresAt > DateTimeOffset.UtcNow)
        {
            return cachedConfiguration;
        }

        await refreshLock.WaitAsync(cancellationToken);
        try
        {
            if (cachedConfiguration is not null && cacheExpiresAt > DateTimeOffset.UtcNow)
            {
                return cachedConfiguration;
            }

            cachedConfiguration = await LoadConfigurationAsync(cancellationToken);
            cacheExpiresAt = DateTimeOffset.UtcNow.Add(CacheDuration);
            return cachedConfiguration;
        }
        finally
        {
            refreshLock.Release();
        }
    }

    private async Task<UpgradeCenterRuntimeConfiguration> LoadConfigurationAsync(CancellationToken cancellationToken)
    {
        var idpApiUrl = ResolveIdpApiUrl();
        if (string.IsNullOrWhiteSpace(idpApiUrl))
        {
            logger.LogWarning("IDP Configuration API URL is not configured. Upgrade Center will use only explicit fallback values.");
            return UseLastKnownGoodOrFallback("IDP Configuration API URL is not configured.");
        }

        try
        {
            var requestUri = CombineUrl(idpApiUrl, "configuration");
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.GetAsync(requestUri, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning(
                    "IDP Configuration API request failed. StatusCode: {StatusCode}. Endpoint: {Endpoint}.",
                    (int)response.StatusCode,
                    requestUri);
                return UseLastKnownGoodOrFallback("IDP Configuration API request failed.");
            }

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            var configurationJson = ExtractConfigurationJson(document.RootElement);
            if (string.IsNullOrWhiteSpace(configurationJson))
            {
                logger.LogWarning("IDP Configuration API response did not contain configuration data.");
                return UseLastKnownGoodOrFallback("IDP Configuration API response did not contain configuration data.");
            }

            using var configurationDocument = JsonDocument.Parse(configurationJson);
            var root = configurationDocument.RootElement;
            var idpUrl = GetProperty(root, "InternalAppUrls", "Idp");
            var biUrl = GetProperty(root, "InternalAppUrls", "Bi");
            var version = GetBoldBiVersion(root);
            var encryptedMasterConnectionString = GetProperty(root, "SqlConfiguration", "ConnectionString");
            var masterDatabaseServerType = GetIntegerProperty(root, "SqlConfiguration", "ServerType");
            var machineKeyDecryptionKey = GetProperty(root, "MachineKey", "DecryptionKey");
            var internalAppClientId = GetRootProperty(root, "InternalAppClientId");
            var internalAppClientSecret = GetRootProperty(root, "InternalAppClientSecret");
            var fallback = CreateFallbackConfiguration();
            var runtimeConfiguration = new UpgradeCenterRuntimeConfiguration(
                FirstConfiguredValue(idpUrl, fallback.IdpBaseUrl)?.TrimEnd('/'),
                FirstConfiguredValue(biUrl, fallback.BiBaseUrl)?.TrimEnd('/'),
                FirstConfiguredValue(version, fallback.BoldBiVersion),
                encryptedMasterConnectionString,
                masterDatabaseServerType,
                machineKeyDecryptionKey,
                internalAppClientId,
                internalAppClientSecret);

            if (!IsValidRuntimeConfiguration(runtimeConfiguration))
            {
                logger.LogWarning("IDP Configuration API returned a configuration response, but none of the required Upgrade Center values were available.");
                return UseLastKnownGoodOrFallback("IDP Configuration API returned incomplete configuration data.");
            }

            lastKnownGoodConfiguration = runtimeConfiguration;

            logger.LogInformation(
                "Loaded Upgrade Center runtime configuration from IDP Configuration API. HasIdpUrl: {HasIdpUrl}. HasBiUrl: {HasBiUrl}. HasBoldBiVersion: {HasBoldBiVersion}. HasMasterDatabaseConfiguration: {HasMasterDatabaseConfiguration}. HasInternalAppCredentials: {HasInternalAppCredentials}.",
                !string.IsNullOrWhiteSpace(idpUrl),
                !string.IsNullOrWhiteSpace(biUrl),
                !string.IsNullOrWhiteSpace(version),
                !string.IsNullOrWhiteSpace(encryptedMasterConnectionString) && masterDatabaseServerType.HasValue && !string.IsNullOrWhiteSpace(machineKeyDecryptionKey),
                !string.IsNullOrWhiteSpace(internalAppClientId) && !string.IsNullOrWhiteSpace(internalAppClientSecret));

            return runtimeConfiguration;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Unable to load Upgrade Center runtime configuration from IDP Configuration API.");
            return UseLastKnownGoodOrFallback("Unable to load Upgrade Center runtime configuration from IDP Configuration API.");
        }
    }

    private UpgradeCenterRuntimeConfiguration UseLastKnownGoodOrFallback(string reason)
    {
        if (lastKnownGoodConfiguration is not null)
        {
            logger.LogWarning("Using last-known-good Upgrade Center runtime configuration. Reason: {Reason}", reason);
            return lastKnownGoodConfiguration;
        }

        logger.LogWarning("No last-known-good Upgrade Center runtime configuration is available. Using explicit fallback configuration only. Reason: {Reason}", reason);
        return CreateFallbackConfiguration();
    }

    private static bool IsValidRuntimeConfiguration(UpgradeCenterRuntimeConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration.IdpBaseUrl) ||
               !string.IsNullOrWhiteSpace(configuration.BiBaseUrl) ||
               !string.IsNullOrWhiteSpace(configuration.BoldBiVersion) ||
               (!string.IsNullOrWhiteSpace(configuration.EncryptedMasterDatabaseConnectionString) &&
                configuration.MasterDatabaseServerType.HasValue &&
                !string.IsNullOrWhiteSpace(configuration.MachineKeyDecryptionKey));
    }

    private string? ResolveIdpApiUrl()
    {
        return FirstConfiguredValue(
            options.Value.Services.IdpApi,
            configuration["UpgradeCenter:Services:IdpApi"],
            configuration["UPGRADE_CENTER_IDP_API_INTERNAL_URL"],
            configuration["IDP_API_INTERNAL_URL"])?.TrimEnd('/');
    }

    private UpgradeCenterRuntimeConfiguration CreateFallbackConfiguration()
    {
        var idpUrl = FirstConfiguredValue(
            options.Value.Authentication.IdpPublicUrlOverride,
            configuration["UpgradeCenter:Authentication:IdpPublicUrlOverride"],
            configuration["UpgradeCenter:Authentication:IdpUrl"],
            configuration["UPGRADE_CENTER_IDP_PUBLIC_URL"],
            configuration["BOLD_UPGRADE_CENTER_IDP_URL"])?.TrimEnd('/');

        return new UpgradeCenterRuntimeConfiguration(idpUrl, null, null);
    }

    private static string? ExtractConfigurationJson(JsonElement root)
    {
        if (root.ValueKind == JsonValueKind.Object &&
            TryGetProperty(root, "data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object &&
                TryGetProperty(data, "config", out var config))
            {
                return config.ValueKind == JsonValueKind.String ? config.GetString() : config.GetRawText();
            }

            return data.ValueKind == JsonValueKind.String ? data.GetString() : data.GetRawText();
        }

        return root.GetRawText();
    }

    private static string? GetProperty(JsonElement root, string parentName, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, parentName, out var parent) ||
            parent.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(parent, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static string? GetRootProperty(JsonElement root, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, propertyName, out var value))
        {
            return null;
        }

        return value.ValueKind == JsonValueKind.String ? value.GetString() : value.GetRawText();
    }

    private static int? GetIntegerProperty(JsonElement root, string parentName, string propertyName)
    {
        if (root.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(root, parentName, out var parent) ||
            parent.ValueKind != JsonValueKind.Object ||
            !TryGetProperty(parent, propertyName, out var value))
        {
            return null;
        }

        if (value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number))
        {
            return number;
        }

        if (value.ValueKind == JsonValueKind.String && int.TryParse(value.GetString(), out number))
        {
            return number;
        }

        return value.ValueKind == JsonValueKind.String ? MapDatabaseTypeName(value.GetString()) : null;
    }

    private static int? MapDatabaseTypeName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = value.Replace(" ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace("-", string.Empty, StringComparison.OrdinalIgnoreCase)
            .ToLowerInvariant();

        return normalized switch
        {
            "mssql" or "sqlserver" or "microsoftsqlserver" => 0,
            "mysql" or "mariadb" => 1,
            "mssqlce" => 2,
            "oracle" or "ora" => 3,
            "postgresql" or "postgres" or "pgsql" => 4,
            _ => null
        };
    }

    private static string? GetBoldBiVersion(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object || !TryGetProperty(root, "BoldProducts", out var boldProducts))
        {
            return null;
        }

        if (TryReadBoldBiVersion(boldProducts, out var version))
        {
            return version;
        }

        if (boldProducts.ValueKind == JsonValueKind.Object &&
            TryGetProperty(boldProducts, "BoldProduct", out var boldProduct) &&
            TryReadBoldBiVersion(boldProduct, out version))
        {
            return version;
        }

        return null;
    }

    private static bool TryReadBoldBiVersion(JsonElement element, out string? version)
    {
        version = null;
        if (element.ValueKind == JsonValueKind.Object)
        {
            version = IsBoldBiProduct(element) && TryGetProperty(element, "Version", out var versionElement)
                ? versionElement.GetString()
                : null;
            return !string.IsNullOrWhiteSpace(version);
        }

        if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                if (IsBoldBiProduct(item) && TryGetProperty(item, "Version", out var versionElement))
                {
                    version = versionElement.GetString();
                    return !string.IsNullOrWhiteSpace(version);
                }
            }
        }

        return false;
    }

    private static bool IsBoldBiProduct(JsonElement element)
    {
        return element.ValueKind == JsonValueKind.Object &&
               TryGetProperty(element, "Name", out var nameElement) &&
               string.Equals(nameElement.GetString(), "BoldBI", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryGetProperty(JsonElement element, string propertyName, out JsonElement value)
    {
        if (element.TryGetProperty(propertyName, out value))
        {
            return true;
        }

        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var property in element.EnumerateObject())
            {
                if (string.Equals(property.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }
            }
        }

        value = default;
        return false;
    }

    private static string CombineUrl(string baseUrl, string relativePath)
    {
        return $"{baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";
    }

    private static string? FirstConfiguredValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
