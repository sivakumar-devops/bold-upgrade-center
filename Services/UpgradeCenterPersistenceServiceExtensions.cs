using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Bold.UpgradeCenter.Services;

public static class UpgradeCenterPersistenceServiceExtensions
{
    public static IServiceCollection AddUpgradeCenterPersistence(this IServiceCollection services, IConfiguration configuration, IHostEnvironment environment)
    {
        var protection = services.AddDataProtection().SetApplicationName("Bold.UpgradeCenter");
        if (!string.Equals(configuration["BOLD_SERVICES_PV_USED"], "false", StringComparison.OrdinalIgnoreCase))
        {
            var keysPath = Path.Combine(environment.ContentRootPath, "App_Data", "DataProtectionKeys");
            Directory.CreateDirectory(keysPath);
            protection.PersistKeysToFileSystem(new DirectoryInfo(keysPath));
            services.AddSingleton<IPlaywrightReportStore, PlaywrightReportStore>();
            return services;
        }

        if (StandaloneObjectStorageSettings.ResolveSource(configuration) == StandaloneConfigurationSource.FileSystem)
            throw new InvalidOperationException("Upgrade Center without a product PV requires the product's existing object-storage settings. No data PVC is created automatically.");

        services.TryAddSingleton(_ => StandaloneObjectStorageSettings.FromConfiguration(configuration));
        services.AddSingleton<IUpgradeCenterObjectStore>(provider =>
        {
            try { return new UpgradeCenterObjectStore(provider.GetRequiredService<StandaloneObjectStorageSettings>()); }
            catch (Exception) { throw new InvalidOperationException("Upgrade Center object-storage persistence could not be initialized. Verify the existing product storage settings."); }
        });
        services.AddSingleton<ObjectStorageXmlRepository>();
        services.AddOptions<KeyManagementOptions>().Configure<ObjectStorageXmlRepository>((options, repository) => options.XmlRepository = repository);
        services.AddSingleton<IPlaywrightReportStore, ObjectStoragePlaywrightReportStore>();
        services.AddHostedService<UpgradeCenterPersistenceValidationService>();
        return services;
    }
}

internal sealed class UpgradeCenterPersistenceValidationService(IDataProtectionProvider protection) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Validate durable key access before accepting logins; never fall back to ephemeral keys.
        try
        {
            var protector = protection.CreateProtector("Bold.UpgradeCenter.PersistenceValidation");
            var probe = Guid.NewGuid().ToString("N");
            if (protector.Unprotect(protector.Protect(probe)) != probe) throw new InvalidOperationException();
        }
        catch (Exception)
        {
            throw new InvalidOperationException("Upgrade Center authentication-key persistence is unavailable. Verify read/list/write access to its dedicated object-storage prefix.");
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
