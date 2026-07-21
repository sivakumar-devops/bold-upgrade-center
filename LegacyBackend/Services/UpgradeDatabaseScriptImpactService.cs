using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class UpgradeDatabaseScriptImpactService : IUpgradeDatabaseScriptImpactService
{
    private const string SqlIdentifierPattern = @"(?:""[^""]+""|`[^`]+`|\[[^\]]+\]|\{[^}]+\}|[\w]+)";
    private const string QualifiedTablePattern = SqlIdentifierPattern + @"(?:\s*\.\s*" + SqlIdentifierPattern + ")*";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex[] TablePatterns =
    [
        new(@"\bALTER\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?<table>" + QualifiedTablePattern + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bUPDATE\s+(?:ONLY\s+)?(?<table>" + QualifiedTablePattern + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bINSERT\s+INTO\s+(?<table>" + QualifiedTablePattern + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bDELETE\s+FROM\s+(?<table>" + QualifiedTablePattern + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bMERGE\s+INTO\s+(?<table>" + QualifiedTablePattern + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bCREATE\s+(?:UNIQUE\s+)?INDEX\b[\s\S]*?\bON\s+(?:ONLY\s+)?(?<table>" + QualifiedTablePattern + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bDROP\s+TABLE\s+(?:IF\s+EXISTS\s+)?(?<table>" + QualifiedTablePattern + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bCREATE\s+TABLE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<table>" + QualifiedTablePattern + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bTRUNCATE\s+(?:TABLE\s+)?(?<table>" + QualifiedTablePattern + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled),
        new(@"\bREINDEX\s+TABLE\s+(?:CONCURRENTLY\s+)?(?<table>" + QualifiedTablePattern + ")", RegexOptions.IgnoreCase | RegexOptions.Compiled)
    ];

    private readonly HttpClient httpClient;
    private readonly IReleaseVersionProvider releaseVersionProvider;
    private readonly UpgradeCenterOptions options;
    private readonly ILogger<UpgradeDatabaseScriptImpactService> logger;

    public UpgradeDatabaseScriptImpactService(
        HttpClient httpClient,
        IReleaseVersionProvider releaseVersionProvider,
        IOptions<UpgradeCenterOptions> options,
        ILogger<UpgradeDatabaseScriptImpactService> logger)
    {
        this.httpClient = httpClient;
        this.releaseVersionProvider = releaseVersionProvider;
        this.options = options.Value;
        this.logger = logger;
    }

    public async Task<UpgradeDatabaseScriptImpactResult> AnalyzeAsync(
        string currentVersion,
        string targetVersion,
        IReadOnlyList<DatabaseType> databaseTypes,
        CancellationToken cancellationToken = default)
    {
        var normalizedCurrentVersion = NormalizeVersion(currentVersion);
        var normalizedTargetVersion = NormalizeVersion(targetVersion);
        if (!ProductVersionComparer.TryParse(normalizedCurrentVersion, out var currentParts))
        {
            return Failed(currentVersion, targetVersion, "Installed version is not in a supported version format.");
        }

        if (!ProductVersionComparer.TryParse(normalizedTargetVersion, out var targetParts))
        {
            return Failed(currentVersion, targetVersion, "Target version is not in a supported version format.");
        }

        if (ProductVersionComparer.Compare(currentParts, targetParts) >= 0)
        {
            return new UpgradeDatabaseScriptImpactResult(
                true,
                "No database script analysis was required because the target version is not newer than the installed version.",
                normalizedCurrentVersion,
                normalizedTargetVersion,
                Array.Empty<string>(),
                FormatDatabaseTypes(databaseTypes),
                Array.Empty<string>(),
                new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
                null);
        }

        var distinctDatabaseTypes = databaseTypes
            .Distinct()
            .OrderBy(databaseType => databaseType.ToString(), StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (distinctDatabaseTypes.Count == 0)
        {
            return Failed(currentVersion, targetVersion, "No database types were discovered for database script analysis.");
        }

        foreach (var databaseType in distinctDatabaseTypes)
        {
            if (!TryGetDatabaseApiSegment(databaseType, out _))
            {
                return Failed(currentVersion, targetVersion, $"Database type '{databaseType}' is not supported for database script analysis.");
            }
        }

        var releaseSummary = await releaseVersionProvider.GetReleaseVersionsAsync(normalizedCurrentVersion, cancellationToken);
        if (!string.IsNullOrWhiteSpace(releaseSummary.ErrorMessage))
        {
            return Failed(currentVersion, targetVersion, releaseSummary.ErrorMessage);
        }

        var applicableVersions = releaseSummary.Versions
            .Where(version => ProductVersionComparer.TryParse(version.Version, out var versionParts) &&
                              ProductVersionComparer.Compare(versionParts, currentParts) > 0 &&
                              ProductVersionComparer.Compare(versionParts, targetParts) <= 0)
            .GroupBy(version => NormalizeVersion(version.Version), StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First().Version)
            .OrderBy(version => ProductVersionComparer.Parse(version), ProductVersionPartsComparer.Instance)
            .ToList();

        if (!applicableVersions.Any(version => ProductVersionComparer.Compare(version, normalizedTargetVersion) == 0))
        {
            return Failed(
                currentVersion,
                targetVersion,
                $"Target version {FormatVersion(normalizedTargetVersion)} was not found in the available release list returned by the Version Provider API.");
        }

        var affectedTables = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        var tableVersionMap = new Dictionary<string, SortedSet<string>>(StringComparer.OrdinalIgnoreCase);
        var scriptCache = new Dictionary<string, UpgradeDatabaseScriptResponse?>(StringComparer.OrdinalIgnoreCase);
        string? scriptPreview = null;

        foreach (var version in applicableVersions)
        {
            foreach (var databaseType in distinctDatabaseTypes)
            {
                _ = TryGetDatabaseApiSegment(databaseType, out var databaseApiSegment);
                var cacheKey = $"{version}|{databaseApiSegment}";
                if (!scriptCache.TryGetValue(cacheKey, out var scriptInfo))
                {
                    scriptInfo = await GetDatabaseScriptAsync(version, databaseApiSegment, cancellationToken);
                    scriptCache[cacheKey] = scriptInfo;
                }

                if (scriptInfo is null)
                {
                    return Failed(
                        currentVersion,
                        targetVersion,
                        $"Database upgrade script metadata could not be retrieved for version {FormatVersion(version)} and database type {databaseType}.",
                        applicableVersions);
                }

                if (!scriptInfo.HasScriptChanges)
                {
                    continue;
                }

                if (string.IsNullOrWhiteSpace(scriptInfo.ScriptQuery))
                {
                    return Failed(
                        currentVersion,
                        targetVersion,
                        $"Version {FormatVersion(version)} reports database script changes for {databaseType}, but no script query was returned.",
                        applicableVersions);
                }

                scriptPreview ??= CreateScriptPreview(scriptInfo.ScriptQuery);
                var scriptTables = ExtractAffectedTables(scriptInfo.ScriptQuery);
                if (scriptTables.Count == 0)
                {
                    return Failed(
                        currentVersion,
                        targetVersion,
                        $"Version {FormatVersion(version)} reports database script changes for {databaseType}, but affected table names could not be identified safely.",
                        applicableVersions);
                }

                foreach (var table in scriptTables)
                {
                    affectedTables.Add(table);
                    if (!tableVersionMap.TryGetValue(table, out var versions))
                    {
                        versions = new SortedSet<string>(Comparer<string>.Create((left, right) =>
                            ProductVersionComparer.Compare(ProductVersionComparer.Parse(left), ProductVersionComparer.Parse(right))));
                        tableVersionMap[table] = versions;
                    }

                    versions.Add(version);
                }
            }
        }

        var affectedTableVersions = tableVersionMap.ToDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase);
        var message = affectedTables.Count == 0
            ? $"No database script changes were reported for the upgrade range {FormatVersion(normalizedCurrentVersion)} to {FormatVersion(normalizedTargetVersion)}."
            : $"Identified {affectedTables.Count} unique affected table(s) across {applicableVersions.Count} applicable version(s).";

        return new UpgradeDatabaseScriptImpactResult(
            true,
            message,
            normalizedCurrentVersion,
            normalizedTargetVersion,
            applicableVersions,
            FormatDatabaseTypes(distinctDatabaseTypes),
            affectedTables.ToList(),
            affectedTableVersions,
            scriptPreview);
    }

    private async Task<UpgradeDatabaseScriptResponse?> GetDatabaseScriptAsync(string selectedVersion, string databaseApiSegment, CancellationToken cancellationToken)
    {
        var uri = BuildVersionApiUri(selectedVersion, "database", databaseApiSegment);
        logger.LogInformation("Fetching database upgrade script metadata for {SelectedVersion} and {DatabaseType}.", selectedVersion, databaseApiSegment);

        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.UserAgent.ParseAdd("Bold-Upgrade-Center/1.0");
        request.Headers.Accept.ParseAdd("application/json");

        using var response = await httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            logger.LogWarning(
                "Database upgrade script metadata request failed. SelectedVersion: {SelectedVersion}. DatabaseType: {DatabaseType}. StatusCode: {StatusCode}.",
                selectedVersion,
                databaseApiSegment,
                (int)response.StatusCode);
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonSerializer.DeserializeAsync<UpgradeDatabaseScriptResponse>(stream, JsonOptions, cancellationToken);
    }

    private string BuildVersionApiUri(string selectedVersion, params string[] segments)
    {
        var baseUri = options.ReleaseVersionsApiUrl.TrimEnd('/');
        var encodedSegments = new[] { NormalizeVersion(selectedVersion) }
            .Concat(segments)
            .Select(Uri.EscapeDataString);

        return $"{baseUri}/{string.Join("/", encodedSegments)}";
    }

    private static bool TryGetDatabaseApiSegment(DatabaseType databaseType, out string apiSegment)
    {
        apiSegment = databaseType switch
        {
            DatabaseType.PostgreSQL => "postgresql",
            DatabaseType.MySQL => "mysql",
            DatabaseType.MSSQL => "mssql",
            DatabaseType.Oracle => "oracle",
            _ => string.Empty
        };

        return !string.IsNullOrWhiteSpace(apiSegment);
    }

    private static IReadOnlyList<string> ExtractAffectedTables(string script)
    {
        var tables = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cleanedScript = RemoveSqlComments(script);

        foreach (var pattern in TablePatterns)
        {
            foreach (Match match in pattern.Matches(cleanedScript))
            {
                var table = NormalizeTableIdentifier(match.Groups["table"].Value);
                if (!string.IsNullOrWhiteSpace(table) && !IsSqlKeyword(table))
                {
                    tables.Add(table);
                }
            }
        }

        return tables.OrderBy(table => table, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private static string RemoveSqlComments(string script)
    {
        var withoutBlockComments = Regex.Replace(script, @"/\*[\s\S]*?\*/", string.Empty);
        return Regex.Replace(withoutBlockComments, @"--.*?$", string.Empty, RegexOptions.Multiline);
    }

    private static string NormalizeTableIdentifier(string value)
    {
        var trimmed = value.Trim().TrimEnd(';', ',');
        var parts = trimmed.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tableName = parts.Length == 0 ? trimmed : parts[^1];
        return tableName.Trim('"', '`', '[', ']');
    }

    private static bool IsSqlKeyword(string value)
    {
        return value.Equals("SET", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("SELECT", StringComparison.OrdinalIgnoreCase) ||
               value.Equals("WHERE", StringComparison.OrdinalIgnoreCase);
    }

    private static string CreateScriptPreview(string script)
    {
        const int maxLength = 1200;
        var trimmed = script.Trim();
        return trimmed.Length <= maxLength ? trimmed : trimmed[..maxLength] + Environment.NewLine + "...";
    }

    private static string NormalizeVersion(string value)
    {
        var trimmed = value.Trim();
        return trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)
            ? trimmed[1..]
            : trimmed;
    }

    private static string FormatVersion(string version)
    {
        return version.StartsWith("v", StringComparison.OrdinalIgnoreCase) ? version : $"v{version}";
    }

    private static IReadOnlyList<string> FormatDatabaseTypes(IReadOnlyList<DatabaseType> databaseTypes)
    {
        return databaseTypes
            .Select(databaseType => databaseType.ToString())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(databaseType => databaseType, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static UpgradeDatabaseScriptImpactResult Failed(
        string currentVersion,
        string targetVersion,
        string message,
        IReadOnlyList<string>? applicableVersions = null)
    {
        return new UpgradeDatabaseScriptImpactResult(
            false,
            message,
            NormalizeVersion(currentVersion),
            NormalizeVersion(targetVersion),
            applicableVersions ?? Array.Empty<string>(),
            Array.Empty<string>(),
            Array.Empty<string>(),
            new Dictionary<string, IReadOnlyList<string>>(StringComparer.OrdinalIgnoreCase),
            null);
    }
}
