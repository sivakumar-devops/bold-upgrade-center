namespace BoldBI.UpgradeCenter.Services;

public sealed class PlaywrightExecutionOptions
{
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

public sealed class PlaywrightScriptExecutionOptions
{
    public string Name { get; set; } = "Playwright validation";

    public string? Executable { get; set; }

    public string? Arguments { get; set; }

    public string? WorkingDirectory { get; set; }

    public int? TimeoutSeconds { get; set; }
}
