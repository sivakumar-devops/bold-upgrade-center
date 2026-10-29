using System.Text.RegularExpressions;

namespace Bold.UpgradeCenter.Services;

public enum UpgradeExecutionEnvironment { Kubernetes, Ecs }

public static class UpgradeExecutionEnvironments
{
    public static UpgradeExecutionEnvironment Parse(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        null or "kubernetes" => UpgradeExecutionEnvironment.Kubernetes,
        "ecs" => UpgradeExecutionEnvironment.Ecs,
        _ => throw new InvalidOperationException("UpgradeCenter:ExecutionEnvironment must be Kubernetes or ECS.")
    };
}

public sealed class EcsExecutionOptions
{
    public string Region { get; set; } = "";
    public string Cluster { get; set; } = "";
    public Dictionary<string, EcsServiceMapping> Services { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public string RunnerTaskDefinition { get; set; } = "";
    public string RunnerContainer { get; set; } = "playwright";
    public List<string> Subnets { get; set; } = [];
    public List<string> SecurityGroups { get; set; } = [];
    public string UploadBaseUrl { get; set; } = "";
    public string SharedStateDirectory { get; set; } = "/app/playwright-shared-state";
    public string? StateBucket { get; set; }
    public string StatePrefix { get; set; } = "app_data/upgrade-center/playwright-state";
    public string RuntimeSecretPrefix { get; set; } = "bold-upgrade-center/playwright";
    public int RolloutTimeoutSeconds { get; set; } = 900;
    public int PollSeconds { get; set; } = 5;
    public bool MaintenanceWindowConfirmed { get; set; }

    public void Validate(IConfiguration configuration)
    {
        if (UpgradeCenterIntegrationModes.Parse(configuration["UpgradeCenter:IntegrationMode"]) != UpgradeCenterIntegrationMode.Standalone ||
            !string.Equals(configuration["UpgradeCenter:DeploymentMode"], "BoldBI", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("ECS currently supports Standalone integration and BoldBI deployment mode only.");
        if (string.IsNullOrWhiteSpace(Region) || string.IsNullOrWhiteSpace(Cluster) || Services.Count == 0 ||
            string.IsNullOrWhiteSpace(RunnerTaskDefinition) || string.IsNullOrWhiteSpace(RunnerContainer) || Subnets.Count == 0 || SecurityGroups.Count == 0 ||
            !Uri.TryCreate(UploadBaseUrl, UriKind.Absolute, out var url) || url.Scheme != "https" ||
            PollSeconds < 1 || RolloutTimeoutSeconds < 1 || !Regex.IsMatch(RuntimeSecretPrefix, @"\A[a-zA-Z0-9/_-]+\z"))
            throw new InvalidOperationException("ECS requires region, cluster, explicit service/container mappings, runner definition, private subnets/security groups, and an HTTPS upload URL.");
        if (Services.Values.Any(item => string.IsNullOrWhiteSpace(item.Service) || string.IsNullOrWhiteSpace(item.Container)) ||
            Services.Values.Select(item => item.Service).Distinct(StringComparer.Ordinal).Count() != Services.Count ||
            Services.Keys.Any(key => !UpgradeProductDefinitions.BoldBi.ExpectedDeployments.Contains(key)))
            throw new InvalidOperationException("ECS service mappings must be unique approved BI components with explicit service and container names.");
        if (StateBucket is not null && (string.IsNullOrWhiteSpace(StateBucket) || StatePrefix.Split('/').Any(part => part is "" or "." or "..")))
            throw new InvalidOperationException("ECS S3 validation state requires a bucket and an isolated prefix.");
    }
}

public sealed class EcsServiceMapping
{
    public string Service { get; set; } = "";
    public string Container { get; set; } = "";
}

public sealed record EcsScalingState(string ResourceId, int Min, int Max, bool InSuspended, bool OutSuspended, bool ScheduledSuspended);
public sealed record EcsServiceRevision(string Component, string Service, string Container, string PreviousTaskDefinition, string TargetTaskDefinition, int DesiredCount, EcsScalingState? Scaling = null);
public sealed record EcsDeploymentPlan(string Cluster, IReadOnlyList<EcsServiceRevision> Services);
public sealed record EcsValidationExecution(string TaskDefinition, string SecretArn, string ClientToken, DateTimeOffset StartedAt, string? TaskArn = null,
    PlaywrightScriptResult? Result = null, PlaywrightValidationResultSummary? Summary = null);

public interface IUpgradeExecutionSafety
{
    Task ValidateBeforeBackupAsync(UpgradeJob job, CancellationToken cancellationToken);
    Task PrepareDatabaseRestoreAsync(KubernetesUpgradeResult result, CancellationToken cancellationToken);
}

public interface IPlaywrightRuntimeEnvironmentProvider
{
    Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(CancellationToken cancellationToken);
}
