using System.Threading.RateLimiting;
using Bold.UpgradeCenter.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.RateLimiting;

namespace Bold.UpgradeCenter.Services;

public static class UpgradeCenterIntegrationServiceExtensions
{
    public static IServiceCollection AddUpgradeCenterIntegration(
        this IServiceCollection services,
        IConfiguration configuration,
        IHostEnvironment environment)
    {
        var mode = UpgradeCenterIntegrationModes.Parse(configuration["UpgradeCenter:IntegrationMode"]);
        if (mode == UpgradeCenterIntegrationMode.IdpIntegrated)
        {
            services.AddSingleton<IUpgradeCenterConfigurationProvider, IdpConfigurationProvider>();
            services.AddSingleton<IIdpPrivateKeyProvider, IdpPrivateKeyProvider>();
            services.AddAuthentication(UpgradeCenterAuthenticationDefaults.Scheme)
                .AddScheme<AuthenticationSchemeOptions, RemoteUmsAuthenticationHandler>(
                    UpgradeCenterAuthenticationDefaults.Scheme, _ => { });
        }
        else
        {
            var source = StandaloneObjectStorageSettings.ResolveSource(configuration);
            if (source == StandaloneConfigurationSource.FileSystem)
            {
                services.AddSingleton<FileUpgradeCenterConfigurationProvider>();
                services.AddSingleton<IUpgradeCenterConfigurationProvider>(provider => provider.GetRequiredService<FileUpgradeCenterConfigurationProvider>());
                services.AddSingleton<IIdpPrivateKeyProvider>(provider => provider.GetRequiredService<FileUpgradeCenterConfigurationProvider>());
            }
            else
            {
                services.AddSingleton(StandaloneObjectStorageSettings.FromConfiguration(configuration));
                services.AddSingleton<IStandaloneObjectReader>(provider =>
                {
                    try
                    {
                        return new StandaloneObjectReader(provider.GetRequiredService<StandaloneObjectStorageSettings>());
                    }
                    catch (Exception exception) when (exception is ArgumentException or FormatException or InvalidOperationException or Amazon.Runtime.AmazonClientException)
                    {
                        throw new InvalidOperationException("Standalone storage client could not be initialized. Verify the storage endpoint and protected authentication settings.");
                    }
                });
                services.AddSingleton<IStandaloneRuntimeConfigurationReader, StandaloneRuntimeConfigurationReader>();
                services.AddSingleton(TimeProvider.System);
                services.AddSingleton<ObjectStorageUpgradeCenterConfigurationProvider>();
                services.AddSingleton<IUpgradeCenterConfigurationProvider>(provider => provider.GetRequiredService<ObjectStorageUpgradeCenterConfigurationProvider>());
                services.AddSingleton<IIdpPrivateKeyProvider>(provider => provider.GetRequiredService<ObjectStorageUpgradeCenterConfigurationProvider>());
            }
            services.AddSingleton<StandaloneAdminCredentials>();
            services.AddHostedService<StandaloneConfigurationValidationService>();
            services.AddAuthentication(UpgradeCenterAuthenticationDefaults.StandaloneScheme)
                .AddCookie(UpgradeCenterAuthenticationDefaults.StandaloneScheme, options =>
                {
                    options.LoginPath = "/upgrade-center/login";
                    options.AccessDeniedPath = "/upgrade-center/unauthorized";
                    options.Cookie.Name = ".Bold.UpgradeCenter.Standalone";
                    options.Cookie.HttpOnly = true;
                    options.Cookie.SameSite = SameSiteMode.Strict;
                    options.Cookie.SecurePolicy = environment.IsDevelopment() ? CookieSecurePolicy.SameAsRequest : CookieSecurePolicy.Always;
                    options.ExpireTimeSpan = TimeSpan.FromMinutes(configuration.GetValue("UpgradeCenter:Authentication:Standalone:SessionMinutes", 30));
                    options.SlidingExpiration = false;
                    options.Events.OnValidatePrincipal = async context =>
                    {
                        var credentials = context.HttpContext.RequestServices.GetRequiredService<StandaloneAdminCredentials>();
                        if (!credentials.IsCurrentPrincipal(context.Principal))
                        {
                            context.RejectPrincipal();
                            await context.HttpContext.SignOutAsync(UpgradeCenterAuthenticationDefaults.StandaloneScheme);
                        }
                    };
                });
        }

        services.AddRateLimiter(options =>
        {
            options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
            options.AddPolicy("standalone-login", context => RateLimitPartition.GetFixedWindowLimiter(
                context.Connection.RemoteIpAddress?.ToString() ?? "unknown",
                _ => new FixedWindowRateLimiterOptions
                {
                    PermitLimit = 5,
                    Window = TimeSpan.FromMinutes(1),
                    QueueLimit = 0,
                    AutoReplenishment = true
                }));
        });
        return services;
    }
}

internal sealed class StandaloneConfigurationValidationService(
    IUpgradeCenterConfigurationProvider provider,
    StandaloneAdminCredentials credentials,
    ILogger<StandaloneConfigurationValidationService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await provider.GetConfigurationAsync(cancellationToken);
        logger.LogInformation("Standalone mode is ready. Product configuration, private key, and local administrator configuration were validated.");
        _ = credentials.Username;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
