using System.Formats.Tar;
using System.IO.Compression;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace BoldBI.UpgradeCenter.Services;

public sealed class PlaywrightReportStore : IPlaywrightReportStore
{
    public const string ReportBeginMarker = "BOLD_UPGRADE_CENTER_PLAYWRIGHT_REPORT_BEGIN";
    public const string ReportEndMarker = "BOLD_UPGRADE_CENTER_PLAYWRIGHT_REPORT_END";
    public const string ReportArchiveBeginMarker = "BOLD_UPGRADE_CENTER_PLAYWRIGHT_REPORT_ARCHIVE_BEGIN";
    public const string ReportArchiveEndMarker = "BOLD_UPGRADE_CENTER_PLAYWRIGHT_REPORT_ARCHIVE_END";

    private const string ReportFileName = "report.html";
    private const string MonocartReportFileName = "reports.html";
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

        try
        {
            var jobDirectory = GetJobDirectory(jobId);
            var stageDirectory = GetStageDirectory(jobId, mode);

            lock (syncRoot)
            {
                var encodedArchive = ExtractEncodedBlock(logs, ReportArchiveBeginMarker, ReportArchiveEndMarker);
                if (!string.IsNullOrWhiteSpace(encodedArchive))
                {
                    SaveArchive(encodedArchive, stageDirectory);
                }
                else
                {
                    var encodedReport = ExtractEncodedBlock(logs, ReportBeginMarker, ReportEndMarker);
                    if (string.IsNullOrWhiteSpace(encodedReport))
                    {
                        return false;
                    }

                    Directory.CreateDirectory(stageDirectory);
                    File.WriteAllBytes(Path.Combine(stageDirectory, ReportFileName), Convert.FromBase64String(encodedReport));
                }

                EnsureDefaultReportFile(stageDirectory);
                Directory.SetLastWriteTimeUtc(jobDirectory, DateTime.UtcNow);
                PruneOldReports(jobDirectory);
            }

            logger.LogInformation(
                "Stored Playwright HTML report. JobId: {JobId}. Stage: {Stage}. Path: {ReportPath}.",
                jobId,
                ToStageSegment(mode),
                stageDirectory);
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
        return TryOpenRead(jobId, mode, null, out stream, out _);
    }

    public bool TryOpenRead(string jobId, PlaywrightValidationMode mode, string? relativePath, out Stream? stream, out string contentType)
    {
        stream = null;
        contentType = "application/octet-stream";

        var stageDirectory = Path.GetFullPath(GetStageDirectory(jobId, mode));
        var requestedPath = string.IsNullOrWhiteSpace(relativePath)
            ? ReportFileName
            : relativePath.Replace('\\', '/').TrimStart('/');
        if (Path.IsPathRooted(requestedPath) || requestedPath.Split('/').Any(segment => segment == ".."))
        {
            return false;
        }

        var reportPath = Path.GetFullPath(Path.Combine(stageDirectory, requestedPath));
        if (!reportPath.StartsWith(stageDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(reportPath, stageDirectory, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!File.Exists(reportPath))
        {
            return false;
        }

        contentType = GetContentType(reportPath);
        stream = new FileStream(reportPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return true;
    }

    private static string ResolvePath(string contentRootPath, string configuredPath)
    {
        return Path.IsPathRooted(configuredPath)
            ? configuredPath
            : Path.GetFullPath(Path.Combine(contentRootPath, configuredPath));
    }

    private static string ExtractEncodedBlock(string logs, string beginMarker, string endMarker)
    {
        var begin = logs.LastIndexOf(beginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return string.Empty;
        }

        begin += beginMarker.Length;
        var end = logs.IndexOf(endMarker, begin, StringComparison.Ordinal);
        if (end < 0)
        {
            return string.Empty;
        }

        return logs[begin..end]
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Replace("\n", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static void SaveArchive(string encodedArchive, string stageDirectory)
    {
        if (Directory.Exists(stageDirectory))
        {
            Directory.Delete(stageDirectory, recursive: true);
        }

        Directory.CreateDirectory(stageDirectory);

        using var archiveStream = new MemoryStream(Convert.FromBase64String(encodedArchive));
        using var gzipStream = new GZipStream(archiveStream, CompressionMode.Decompress);
        using var reader = new TarReader(gzipStream);
        TarEntry? entry;
        while ((entry = reader.GetNextEntry()) is not null)
        {
            if (entry.EntryType is TarEntryType.Directory)
            {
                continue;
            }

            var relativeName = entry.Name.Replace('\\', '/').TrimStart('/');
            if (string.IsNullOrWhiteSpace(relativeName) ||
                Path.IsPathRooted(relativeName) ||
                relativeName.Split('/').Any(segment => segment == ".."))
            {
                continue;
            }

            var destinationPath = Path.GetFullPath(Path.Combine(stageDirectory, relativeName));
            var stageRoot = Path.GetFullPath(stageDirectory);
            if (!destinationPath.StartsWith(stageRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
            if (entry.DataStream is null)
            {
                File.WriteAllBytes(destinationPath, Array.Empty<byte>());
                continue;
            }

            using var output = File.Create(destinationPath);
            entry.DataStream.CopyTo(output);
        }
    }

    private static void EnsureDefaultReportFile(string stageDirectory)
    {
        var defaultPath = Path.Combine(stageDirectory, ReportFileName);
        if (File.Exists(defaultPath))
        {
            return;
        }

        var monocartPath = Path.Combine(stageDirectory, MonocartReportFileName);
        if (File.Exists(monocartPath))
        {
            File.Copy(monocartPath, defaultPath, overwrite: true);
            return;
        }

        var indexPath = Directory.EnumerateFiles(stageDirectory, "index.html", SearchOption.AllDirectories)
            .FirstOrDefault();
        if (!string.IsNullOrWhiteSpace(indexPath))
        {
            File.Copy(indexPath, defaultPath, overwrite: true);
        }
    }

    private string GetReportPath(string jobId, PlaywrightValidationMode mode)
    {
        return Path.Combine(GetStageDirectory(jobId, mode), ReportFileName);
    }

    private string GetStageDirectory(string jobId, PlaywrightValidationMode mode)
    {
        var stage = ToStageSegment(mode);
        return Path.Combine(GetJobDirectory(jobId), stage);
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

    private static string GetContentType(string path)
    {
        return Path.GetExtension(path).ToLowerInvariant() switch
        {
            ".html" or ".htm" => "text/html; charset=utf-8",
            ".css" => "text/css; charset=utf-8",
            ".js" => "application/javascript; charset=utf-8",
            ".json" => "application/json; charset=utf-8",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".gif" => "image/gif",
            ".svg" => "image/svg+xml",
            ".webp" => "image/webp",
            ".zip" => "application/zip",
            ".txt" or ".log" => "text/plain; charset=utf-8",
            ".xml" => "application/xml; charset=utf-8",
            ".webm" => "video/webm",
            ".mp4" => "video/mp4",
            _ => "application/octet-stream"
        };
    }
}
