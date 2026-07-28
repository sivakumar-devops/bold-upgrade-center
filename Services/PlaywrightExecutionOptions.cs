namespace BoldBI.UpgradeCenter.Services;

public sealed class PlaywrightExecutionOptions
{
    public bool UseKubernetesJob { get; set; }

    public string RunnerImage { get; set; } = "sivakumar2809/upgrade-center-qa:v0.1";

    public int JobTimeoutSeconds { get; set; } = 7200;

    public int JobPollSeconds { get; set; } = 5;

    public int BackoffLimit { get; set; }

    public int? CompletedJobTtlSeconds { get; set; } = 3600;

    public string? PassRateThreshold { get; set; } = "100";

    public string ReportPath { get; set; } = "/app/test-results/reports.html";

    public string PreJunitReportPath { get; set; } = "/app/pre-upgrade-test-results/junit-report.xml";

    public string PostJunitReportPath { get; set; } = "/app/post-upgrade-test-results/junit-report.xml";

    public string PreHtmlReportPath { get; set; } = "/app/pre-upgrade-test-results/reports.html";

    public string PostHtmlReportPath { get; set; } = "/app/post-upgrade-test-results/reports.html";

    public string PreHtmlReportDirectory { get; set; } = "/app/pre-upgrade-test-results";

    public string PostHtmlReportDirectory { get; set; } = "/app/post-upgrade-test-results";

    public string? ReportStoragePath { get; set; }

    public int ReportRetentionJobCount { get; set; } = 7;

    public string? AdminUsername { get; set; }

    public string? AdminPassword { get; set; }

    public string Executable { get; set; } = "node";

    public string Arguments { get; set; } = "node_modules/playwright/cli.js test PlaywrightScripts/sample-upgrade-poc.spec.ts --config PlaywrightScripts/playwright.config.ts --reporter=line";

    public string? WorkingDirectory { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    public PlaywrightScriptExecutionOptions PreUpgrade { get; set; } = new()
    {
        Name = "Pre-upgrade Playwright validation",
        Arguments = "node_modules/playwright/cli.js test PlaywrightScripts/sample-upgrade-poc.spec.ts --config PlaywrightScripts/playwright.config.ts --reporter=line"
    };

    public PlaywrightScriptExecutionOptions PostUpgrade { get; set; } = new()
    {
        Name = "Post-upgrade Playwright validation",
        Arguments = "node_modules/playwright/cli.js test PlaywrightScripts/sample-post-upgrade-validation.spec.ts --config PlaywrightScripts/playwright.config.ts --reporter=line"
    };
}

public enum PlaywrightValidationMode
{
    Pre,
    Post
}

public sealed record PlaywrightValidationProgress(
    string OperationId,
    UpgradeJobStageStatus Status,
    string Message);

public sealed class PlaywrightScriptExecutionOptions
{
    public string Name { get; set; } = "Playwright validation";

    public string? Executable { get; set; }

    public string? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    public int? TimeoutSeconds { get; set; }
}
