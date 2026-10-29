namespace Bold.UpgradeCenter.Services;

public sealed class UpgradeCenterOptions
{
    public string ExecutionEnvironment { get; set; } = "Kubernetes";
    public string ProductName { get; set; } = "Bold Upgrade Center";

    public string IntegrationMode { get; set; } = "IdpIntegrated";

    public string DeploymentMode { get; set; } = "Auto";

    public string BiReleaseVersionsApiUrl { get; set; } = "https://releases.boldbi.com/version/json";

    public string ReleaseVersionsApiUrl { get; set; } = string.Empty;

    public string ReportsReleaseVersionsApiUrl { get; set; } = "https://releases.boldbi.com/reports/version/json";

    public UpgradeCenterServiceOptions Services { get; set; } = new();

    public UpgradeCenterAuthenticationOptions Authentication { get; set; } = new();

    public UpgradeCenterFileConfigurationOptions Configuration { get; set; } = new();
}

public enum UpgradeCenterIntegrationMode
{
    IdpIntegrated,
    Standalone
}

public static class UpgradeCenterIntegrationModes
{
    public static UpgradeCenterIntegrationMode Parse(string? value)
    {
        if (value is null)
        {
            return UpgradeCenterIntegrationMode.IdpIntegrated;
        }

        if (string.Equals(value.Trim(), "IdpIntegrated", StringComparison.OrdinalIgnoreCase))
        {
            return UpgradeCenterIntegrationMode.IdpIntegrated;
        }

        if (string.Equals(value.Trim(), "Standalone", StringComparison.OrdinalIgnoreCase))
        {
            return UpgradeCenterIntegrationMode.Standalone;
        }

        throw new InvalidOperationException("UpgradeCenter:IntegrationMode must be IdpIntegrated or Standalone.");
    }
}

public sealed class UpgradeCenterFileConfigurationOptions
{
    public string ConfigFilePath { get; set; } = "/app/app_data/config.json";

    public string PrivateKeyFilePath { get; set; } = "/app/app_data/privatekeys.dat";

}

public sealed class UpgradeCenterServiceOptions
{
    public string? IdpApi { get; set; }

    public string? Ums { get; set; }
}

public sealed class UpgradeCenterAuthenticationOptions
{
    public string LoginPath { get; set; } = "/accounts/login";

    public string SessionValidationPath { get; set; } = "/upgrade-center/auth/session";

    public string AdminValidationPath { get; set; } = "/upgrade-center/authorization/admin";

    public string? IdpPublicUrlOverride { get; set; }

    public UpgradeCenterStandaloneAuthenticationOptions Standalone { get; set; } = new();

    public List<string> AllowedForwardedPrefixes { get; set; } =
    [
        "/upgrade-center",
        "/platform",
        "/platform/upgrade-center"
    ];
}

public sealed class UpgradeCenterStandaloneAuthenticationOptions
{
    public string? Username { get; set; }

    public string? Password { get; set; }

    public int SessionMinutes { get; set; } = 30;
}
