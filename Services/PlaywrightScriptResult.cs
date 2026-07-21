namespace BoldBI.UpgradeCenter.Services;

public sealed record PlaywrightScriptResult(
    bool Succeeded,
    int? ExitCode,
    bool TimedOut,
    DateTimeOffset StartedAt,
    DateTimeOffset FinishedAt,
    string Command,
    string StandardOutput,
    string StandardError,
    string Message)
{
    public TimeSpan Duration => FinishedAt - StartedAt;
}
