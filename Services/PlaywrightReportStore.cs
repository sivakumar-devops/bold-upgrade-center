using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace BoldBI.UpgradeCenter.Services;

public sealed class PlaywrightReportStore : IPlaywrightReportStore
{
    public const string ReportBeginMarker = "BOLD_UPGRADE_CENTER_PLAYWRIGHT_REPORT_BEGIN";
    public const string ReportEndMarker = "BOLD_UPGRADE_CENTER_PLAYWRIGHT_REPORT_END";

    private const string ReportFileName = "report.html";
    private static readonly Regex SafeNamePattern = new("[^a-zA-Z0-9_.-]+", RegexOptions.Compiled);

    private readonly string rootPath;
    private readonly int retentionJobCount;
    private readonly ILogger<PlaywrightReportStore> logger;
    private readonly object syncRoot = new();

    public PlaywrightReportStore(
        IWebHostEnvironment environment,
        IOptions<PlaywrightExecutionOptions> options,
        ILogger<PlaywrightReportStore> logger)
    {
        var configuredPath = options.Value.ReportStoragePath;
        rootPath = string.IsNullOrWhiteSpace(configuredPath)
            ? Path.Combine(Path.GetTempPath(), "boldbi-upgrade-center", "playwright-reports")
            : ResolvePath(environment.ContentRootPath, configuredPath);
        retentionJobCount = Math.Max(1, options.Value.ReportRetentionJobCount);
        this.logger = logger;
    }

    public bool TrySaveFromLogs(string jobId, PlaywrightValidationMode mode, string logs)
    {
        if (string.IsNullOrWhiteSpace(logs))
        {
            return false;
        }

        var encodedReport = ExtractEncodedReport(logs);
        if (string.IsNullOrWhiteSpace(encodedReport))
        {
            return false;
        }

        try
        {
            var reportBytes = Convert.FromBase64String(encodedReport);
            var jobDirectory = GetJobDirectory(jobId);
            var reportPath = GetReportPath(jobId, mode);
            var stageDirectory = Path.GetDirectoryName(reportPath)!;

            lock (syncRoot)
            {
                Directory.CreateDirectory(stageDirectory);
                File.WriteAllBytes(reportPath, reportBytes);
                Directory.SetLastWriteTimeUtc(jobDirectory, DateTime.UtcNow);
                PruneOldReports(jobDirectory);
            }

            logger.LogInformation(
                "Stored Playwright HTML report. JobId: {JobId}. Stage: {Stage}. Path: {ReportPath}.",
                jobId,
                ToStageSegment(mode),
                reportPath);
            return true;
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "Playwright HTML report could not be stored. JobId: {JobId}. Stage: {Stage}.",
                jobId,
                ToStageSegment(mode));
            return false;
        }
    }

    public bool Exists(string jobId, PlaywrightValidationMode mode)
    {
        return File.Exists(GetReportPath(jobId, mode));
    }

    public bool TryOpenRead(string jobId, PlaywrightValidationMode mode, out Stream? stream)
    {
        var reportPath = GetReportPath(jobId, mode);
        if (!File.Exists(reportPath))
        {
            stream = null;
            return false;
        }

        stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return true;
    }

    private static string ResolvePath(string contentRootPath, string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
    }

    private static string ExtractEncodedReport(string logs)
    {
        var begin = logs.LastIndexOf(ReportBeginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return string.Empty;
        }

        begin += ReportBeginMarker.Length;
        var end = logs.IndexOf(ReportEndMarker, begin, StringComparison.Ordinal);
        if (end < 0)
        {
            return string.Empty;
        }

        return logs[begin..end]
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private string GetReportPath(string jobId, PlaywrightValidationMode mode)
    {
        var stage = ToStageSegment(mode);
        return Path.Combine(GetJobDirectory(jobId), stage, ReportFileName);
    }

    private string GetJobDirectory(string jobId)
    {
        return Path.Combine(rootPath, SafeSegment(jobId));
    }

    private void PruneOldReports(string currentJobDirectory)
    {
        var root = new DirectoryInfo(rootPath);
        if (!root.Exists)
        {
            return;
        }

        var currentRoot = Path.GetFullPath(currentJobDirectory);
        var jobs = root.GetDirectories()
            .OrderByDescending(directory => directory.LastWriteTimeUtc)
            .ToList();

        foreach (var directory in jobs.Skip(retentionJobCount))
        {
            var fullName = Path.GetFullPath(directory.FullName);
            if (string.Equals(fullName, currentRoot, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                directory.Delete(recursive: true);
                logger.LogInformation("Deleted old Playwright report directory. Path: {ReportDirectory}.", directory.FullName);
            }
            catch (Exception exception)
            {
                logger.LogWarning(exception, "Old Playwright report directory could not be deleted. Path: {ReportDirectory}.", directory.FullName);
            }
        }
    }

    private static string SafeSegment(string value)
    {
        var safe = SafeNamePattern.Replace(value.Trim(), "-").Trim('-', '.', '_');
        return string.IsNullOrWhiteSpace(safe) ? "unknown" : safe;
    }

    private static string ToStageSegment(PlaywrightValidationMode mode)
    {
        return mode == PlaywrightValidationMode.Pre ? "pre-upgrade" : "post-upgrade";
    }
}
