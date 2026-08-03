namespace BoldBI.UpgradeCenter.Services;

public interface IPlaywrightValidationResultStore
{
    void Save(string jobId, PlaywrightValidationMode mode, PlaywrightValidationResultSummary summary);

    bool TryGet(string jobId, PlaywrightValidationMode mode, out PlaywrightValidationResultSummary? summary);
}

public sealed record PlaywrightValidationResultSummary(
    string Mode,
    int Total,
    int Passed,
    int Failed,
    int Skipped,
    double PassPercentage,
    int Threshold,
    string Status,
    int? CommandExitCode,
    string? Reason,
    IReadOnlyList<PlaywrightFailedTestSummary>? FailedTests)
{
    public string ToMessage(string prefix)
    {
        var reason = string.IsNullOrWhiteSpace(Reason) ? string.Empty : $" Reason: {Reason}.";
        return $"{prefix} Total: {Total}. Passed: {Passed}. Failed: {Failed}. Skipped: {Skipped}. Pass percentage: {PassPercentage:0.##}%. Threshold: {Threshold}%. Final status: {Status}.{reason}";
    }
}

public sealed record PlaywrightFailedTestSummary(
    string Name,
    string? ClassName,
    string? Message);
