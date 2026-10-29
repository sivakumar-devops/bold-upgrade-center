using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class ObjectStoragePlaywrightReportStore(
    IUpgradeCenterObjectStore storage,
    IOptions<PlaywrightExecutionOptions> options,
    ILogger<ObjectStoragePlaywrightReportStore> logger) : IPlaywrightReportStore
{
    private static readonly Regex SafeName = new("[^a-zA-Z0-9_.-]+", RegexOptions.Compiled);
    private readonly object syncRoot = new();

    public bool TrySaveFromLogs(string jobId, PlaywrightValidationMode mode, string logs)
    {
        if (string.IsNullOrWhiteSpace(logs)) return false;
        var begin = logs.LastIndexOf(PlaywrightReportStore.ReportBeginMarker, StringComparison.Ordinal);
        if (begin < 0) return false;
        begin += PlaywrightReportStore.ReportBeginMarker.Length;
        var end = logs.IndexOf(PlaywrightReportStore.ReportEndMarker, begin, StringComparison.Ordinal);
        if (end < 0) return false;
        try
        {
            using var content = new MemoryStream(Convert.FromBase64String(logs[begin..end]));
            return TrySaveHtml(jobId, mode, content, out _);
        }
        catch (FormatException)
        {
            logger.LogWarning("Playwright HTML report in logs was not valid base64. JobId: {JobId}. Stage: {Stage}.", jobId, mode);
            return false;
        }
    }

    public bool TrySaveHtml(string jobId, PlaywrightValidationMode mode, Stream htmlStream, out string? safeError)
    {
        safeError = null;
        try
        {
            lock (syncRoot)
            {
                storage.WriteAsync(ReportKey(jobId, mode), htmlStream).GetAwaiter().GetResult();
                PruneReports(SafeJob(jobId));
            }
            logger.LogInformation("Stored Playwright HTML report in object storage. JobId: {JobId}. Stage: {Stage}.", jobId, mode);
            return true;
        }
        catch (Exception)
        {
            safeError = "Playwright HTML report could not be stored. Verify Upgrade Center object-storage connectivity and permissions.";
            logger.LogWarning("Playwright HTML report could not be stored in object storage. JobId: {JobId}. Stage: {Stage}.", jobId, mode);
            return false;
        }
    }

    public bool Exists(string jobId, PlaywrightValidationMode mode)
    {
        // This interface is also used during recovery; storage errors must not abort the runner.
        try { return storage.ExistsAsync(ReportKey(jobId, mode)).GetAwaiter().GetResult(); }
        catch (Exception)
        {
            logger.LogWarning("Playwright HTML report availability could not be checked in object storage. JobId: {JobId}. Stage: {Stage}.", jobId, mode);
            return false;
        }
    }

    public bool TryOpenRead(string jobId, PlaywrightValidationMode mode, out Stream? stream) =>
        TryOpenRead(jobId, mode, null, out stream, out _);

    public bool TryOpenRead(string jobId, PlaywrightValidationMode mode, string? relativePath, out Stream? stream, out string contentType)
    {
        stream = null;
        contentType = "text/html; charset=utf-8";
        // Uploads are self-contained HTML; no arbitrary object paths are served.
        if (!string.IsNullOrEmpty(relativePath) && relativePath != "report.html") return false;
        try
        {
            var content = storage.ReadAsync(ReportKey(jobId, mode)).GetAwaiter().GetResult();
            if (content is null) return false;
            stream = new MemoryStream(content, writable: false);
            return true;
        }
        catch (Exception)
        {
            logger.LogWarning("Playwright HTML report could not be read from object storage. JobId: {JobId}. Stage: {Stage}.", jobId, mode);
            return false;
        }
    }

    private void PruneReports(string currentJob)
    {
        try
        {
            var reports = storage.ListAsync("playwright-reports/").GetAwaiter().GetResult();
            var jobs = reports.GroupBy(item => item.Key.Split('/')[1])
                .OrderByDescending(group => group.Max(item => item.Modified));
            foreach (var group in jobs.Skip(Math.Max(1, options.Value.ReportRetentionJobCount)))
            {
                if (group.Key == currentJob) continue;
                foreach (var report in group)
                    storage.DeleteReportAsync(report.Key).GetAwaiter().GetResult();
            }
        }
        catch (Exception)
        {
            // Retention failure must not reject an already persisted report or its upload token.
            logger.LogWarning("Old Upgrade Center reports could not be removed from object storage. Check prefix-scoped list/delete permissions.");
        }
    }

    private static string SafeJob(string jobId)
    {
        var safe = SafeName.Replace(jobId.Trim(), "-").Trim('-', '.', '_');
        return safe.Length == 0 ? "unknown" : safe;
    }

    private static string ReportKey(string jobId, PlaywrightValidationMode mode) =>
        $"playwright-reports/{SafeJob(jobId)}/{mode switch { PlaywrightValidationMode.Post => "post-upgrade", PlaywrightValidationMode.Cleanup => "cleanup", _ => "pre-upgrade" }}/report.html";
}
