namespace BoldBI.UpgradeCenter.Services;

public sealed class UpgradeCenterOptions
{
    public string ProductName { get; set; } = "Bold BI";

    public string ReleaseVersionsApiUrl { get; set; } = "https://boldbi-release-api.boldbidemo.com/version/json";

    public UpgradeCenterServiceOptions Services { get; set; } = new();

    public UpgradeCenterAuthenticationOptions Authentication { get; set; } = new();
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
}
