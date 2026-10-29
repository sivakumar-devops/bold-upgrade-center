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
            cacheExpiresAt = DateTimeOffset.UtcNow.Add(
                HasMasterDatabaseConfiguration(cachedConfiguration)
                    ? CacheDuration
                    : TimeSpan.FromSeconds(15));
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
            logger.LogWarning("IDP Configuration API URL is not configured. Upgrade Center authentication will use explicit fallback values only.");
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
                    "IDP Configuration API request failed. StatusCode: {StatusCode}.",
                    (int)response.StatusCode);
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
            var runtimeConfiguration = UpgradeCenterConfigurationParser.Parse(root, CreateFallbackConfiguration());
            if (!runtimeConfiguration.ShowDataHub.HasValue &&
                UpgradeCenterConfigurationParser.TryGetProperty(root, "ShowDataHub", out _))
            {
                logger.LogWarning("IDP configuration contains ShowDataHub, but the value is not a valid boolean. The Playwright runner will use its safe default.");
            }

            if (!IsValidRuntimeConfiguration(runtimeConfiguration))
            {
                logger.LogWarning("IDP Configuration API returned a configuration response, but none of the required Upgrade Center values were available.");
                return UseLastKnownGoodOrFallback("IDP Configuration API returned incomplete configuration data.");
            }

            lastKnownGoodConfiguration = runtimeConfiguration;
            logger.LogInformation(
                "Loaded Upgrade Center runtime configuration from IDP Configuration API. HasIdpUrl: {HasIdpUrl}. HasBiUrl: {HasBiUrl}. HasBoldBiVersion: {HasBoldBiVersion}. HasMasterDatabaseConfiguration: {HasMasterDatabaseConfiguration}. HasInternalAppCredentials: {HasInternalAppCredentials}. HasTenantDatabaseMode: {HasTenantDatabaseMode}. HasShowDataHub: {HasShowDataHub}.",
                !string.IsNullOrWhiteSpace(runtimeConfiguration.IdpBaseUrl),
                !string.IsNullOrWhiteSpace(runtimeConfiguration.BiBaseUrl),
                !string.IsNullOrWhiteSpace(runtimeConfiguration.BoldBiVersion),
                HasMasterDatabaseConfiguration(runtimeConfiguration),
                !string.IsNullOrWhiteSpace(runtimeConfiguration.InternalAppClientId) && !string.IsNullOrWhiteSpace(runtimeConfiguration.InternalAppClientSecret),
                runtimeConfiguration.UseSingleTenantDb.HasValue,
                runtimeConfiguration.ShowDataHub.HasValue);
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

        logger.LogWarning("Using explicit fallback Upgrade Center runtime configuration. Reason: {Reason}", reason);
        return CreateFallbackConfiguration();
    }

    private static bool IsValidRuntimeConfiguration(UpgradeCenterRuntimeConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration.IdpBaseUrl) ||
               !string.IsNullOrWhiteSpace(configuration.BiBaseUrl) ||
               !string.IsNullOrWhiteSpace(configuration.ReportsBaseUrl) ||
               !string.IsNullOrWhiteSpace(configuration.BoldBiVersion) ||
               !string.IsNullOrWhiteSpace(configuration.BoldReportsVersion) ||
               HasMasterDatabaseConfiguration(configuration);
    }

    private static bool HasMasterDatabaseConfiguration(UpgradeCenterRuntimeConfiguration configuration)
    {
        return !string.IsNullOrWhiteSpace(configuration.EncryptedMasterDatabaseConnectionString) &&
               configuration.MasterDatabaseServerType.HasValue &&
               !string.IsNullOrWhiteSpace(configuration.MachineKeyDecryptionKey);
    }

    private string? ResolveIdpApiUrl()
    {
        return FirstConfiguredValue(
            options.Value.Services.IdpApi,
            configuration["UpgradeCenter:Services:IdpApi"],
            configuration["UPGRADE_CENTER_IDP_API_INTERNAL_URL"],
            configuration["IDP_API_INTERNAL_URL"],
            configuration["ID_API_SERVICE_URL"])?.TrimEnd('/');
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
            UpgradeCenterConfigurationParser.TryGetProperty(root, "data", out var data))
        {
            if (data.ValueKind == JsonValueKind.Object &&
                UpgradeCenterConfigurationParser.TryGetProperty(data, "config", out var config))
            {
                return config.ValueKind == JsonValueKind.String ? config.GetString() : config.GetRawText();
            }

            return data.ValueKind == JsonValueKind.String ? data.GetString() : data.GetRawText();
        }

        return root.GetRawText();
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
