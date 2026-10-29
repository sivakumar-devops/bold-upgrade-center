using Amazon;
using Amazon.ECS;
using Amazon.SecretsManager;
using Amazon.CloudWatchLogs;
using Amazon.ApplicationAutoScaling;
using Amazon.S3;

namespace Bold.UpgradeCenter.Services;

public static class EcsExecutionServiceExtensions
{
    public static IServiceCollection AddEcsUpgradeExecution(this IServiceCollection services, IConfiguration configuration)
    {
        var settings = configuration.GetSection("UpgradeCenter:Ecs").Get<EcsExecutionOptions>()!;
        var region = RegionEndpoint.GetBySystemName(settings.Region);
        services.Configure<EcsExecutionOptions>(configuration.GetSection("UpgradeCenter:Ecs"));
        services.AddSingleton<IAmazonECS>(_ => new AmazonECSClient(region));
        services.AddSingleton<IAmazonSecretsManager>(_ => new AmazonSecretsManagerClient(region));
        services.AddSingleton<IAmazonCloudWatchLogs>(_ => new AmazonCloudWatchLogsClient(region));
        services.AddSingleton<IAmazonApplicationAutoScaling>(_ => new AmazonApplicationAutoScalingClient(region));
        services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(region));
        services.AddHttpClient<IKubernetesUpgradeService, EcsUpgradeService>(client => client.Timeout = TimeSpan.FromSeconds(30));
        services.AddScoped<PlaywrightKubernetesJobService>();
        services.AddScoped<IPlaywrightRuntimeEnvironmentProvider>(provider => provider.GetRequiredService<PlaywrightKubernetesJobService>());
        services.AddScoped<IPlaywrightKubernetesJobService, EcsPlaywrightJobService>();
        // The existing runner contract dispatches external jobs through this legacy switch.
        // ECS must never fall back to health-only validation or skipped cleanup.
        services.PostConfigure<PlaywrightExecutionOptions>(options => options.UseKubernetesJob = true);
        return services;
    }
}
