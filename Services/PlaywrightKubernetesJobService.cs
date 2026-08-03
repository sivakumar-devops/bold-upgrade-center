using System.Net.Http.Headers;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using MySqlConnector;
using Npgsql;
using Oracle.ManagedDataAccess.Client;

namespace BoldBI.UpgradeCenter.Services;

public sealed class PlaywrightKubernetesJobService : IPlaywrightKubernetesJobService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly Regex SensitiveLinePattern = new(
        @"(?<key>password|pwd|secret|token|authorization|api[_-]?key|connectionstring)\s*[:=]\s*[""']?[^,""'\r\n]+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly PlaywrightExecutionOptions playwrightOptions;
    private readonly KubernetesUpgradeOptions kubernetesOptions;
    private readonly IConfiguration configuration;
    private readonly IUpgradeCenterConfigurationProvider configurationProvider;
    private readonly IUpgradeDatabaseDiscoveryService databaseDiscoveryService;
    private readonly IPlaywrightReportStore reportStore;
    private readonly IPlaywrightReportUploadTokenStore reportUploadTokenStore;
    private readonly IPlaywrightValidationResultStore validationResultStore;
    private readonly IUpgradeOperationLogStore operationLogStore;
    private readonly ILogger<PlaywrightKubernetesJobService> logger;

    public PlaywrightKubernetesJobService(
        IOptions<PlaywrightExecutionOptions> playwrightOptions,
        IOptions<KubernetesUpgradeOptions> kubernetesOptions,
        IConfiguration configuration,
        IUpgradeCenterConfigurationProvider configurationProvider,
        IUpgradeDatabaseDiscoveryService databaseDiscoveryService,
        IPlaywrightReportStore reportStore,
        IPlaywrightReportUploadTokenStore reportUploadTokenStore,
        IPlaywrightValidationResultStore validationResultStore,
        IUpgradeOperationLogStore operationLogStore,
        ILogger<PlaywrightKubernetesJobService> logger)
    {
        this.playwrightOptions = playwrightOptions.Value;
        this.kubernetesOptions = kubernetesOptions.Value;
        this.configuration = configuration;
        this.configurationProvider = configurationProvider;
        this.databaseDiscoveryService = databaseDiscoveryService;
        this.reportStore = reportStore;
        this.reportUploadTokenStore = reportUploadTokenStore;
        this.validationResultStore = validationResultStore;
        this.operationLogStore = operationLogStore;
        this.logger = logger;
    }

    public async Task<PlaywrightScriptResult> RunAsync(
        PlaywrightValidationMode mode,
        string upgradeJobId,
        Action<PlaywrightValidationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var startedAt = DateTimeOffset.UtcNow;
        var modeText = mode == PlaywrightValidationMode.Pre ? "pre" : "post";
        var displayName = mode == PlaywrightValidationMode.Pre
            ? "Pre-upgrade Playwright validation"
            : "Post-upgrade Playwright validation";

        progress?.Invoke(new PlaywrightValidationProgress("playwright-prepare", UpgradeJobStageStatus.Running, $"Preparing {modeText}-upgrade validation."));

        var context = ResolveContext();
        if (!context.Succeeded)
        {
            return CreateResult(false, null, false, startedAt, displayName, string.Empty, context.Message, context.Message);
        }

        if (string.IsNullOrWhiteSpace(playwrightOptions.RunnerImage))
        {
            const string message = "Playwright runner image is not configured.";
            return CreateResult(false, null, false, startedAt, displayName, string.Empty, message, message);
        }

        RuntimeConfiguration runtimeConfiguration;
        try
        {
            runtimeConfiguration = await ResolveRuntimeConfigurationAsync(cancellationToken);
            var reportUploadToken = reportUploadTokenStore.Register(upgradeJobId, mode);
            runtimeConfiguration = runtimeConfiguration.WithPlaywrightUploads(
                BuildReportUploadUrl(upgradeJobId, mode),
                BuildResultUploadUrl(upgradeJobId, mode),
                reportUploadToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            var message = $"Playwright runtime configuration could not be resolved: {exception.Message}";
            logger.LogWarning(exception, "Playwright runtime configuration could not be resolved. No sensitive values were logged.");
            return CreateResult(false, null, false, startedAt, displayName, string.Empty, message, message);
        }

        var nameSuffix = CreateResourceNameSuffix(upgradeJobId);
        var resourceNonce = Guid.NewGuid().ToString("N")[..8];
        var secretName = $"upgrade-playwright-{modeText}-{nameSuffix}-{resourceNonce}";
        var jobName = $"upgrade-playwright-{modeText}-{nameSuffix}-{resourceNonce}";
        var sharedStatePvcName = GetSharedStatePvcName(upgradeJobId);
        var labels = new Dictionary<string, string>
        {
            ["app.kubernetes.io/name"] = "boldbi-upgrade-center-playwright",
            ["upgrade-center.boldbi.com/job-id"] = nameSuffix,
            ["upgrade-center.boldbi.com/validation"] = modeText
        };

        using var client = CreateKubernetesHttpClient(context);
        var secretCreated = false;
        PlaywrightScriptResult? executionResult = null;
        try
        {
            if (playwrightOptions.SharedStateVolumeEnabled)
            {
                progress?.Invoke(new PlaywrightValidationProgress("playwright-shared-state", UpgradeJobStageStatus.Running, "Preparing shared Playwright validation state volume."));
                await CreateOrReuseSharedStatePvcAsync(client, context.Namespace!, sharedStatePvcName, labels, cancellationToken);
                progress?.Invoke(new PlaywrightValidationProgress("playwright-shared-state", UpgradeJobStageStatus.Succeeded, "Shared Playwright validation state volume is ready."));
                runtimeConfiguration = runtimeConfiguration.WithSharedStateDirectory(GetSharedStateMountPath());
            }

            progress?.Invoke(new PlaywrightValidationProgress("playwright-secret", UpgradeJobStageStatus.Running, "Creating temporary Playwright runtime Secret."));
            await CreateSecretAsync(client, context.Namespace!, secretName, labels, runtimeConfiguration.Environment, cancellationToken);
            secretCreated = true;
            progress?.Invoke(new PlaywrightValidationProgress("playwright-secret", UpgradeJobStageStatus.Succeeded, "Temporary runtime Secret created."));

            progress?.Invoke(new PlaywrightValidationProgress("playwright-job", UpgradeJobStageStatus.Running, "Creating Playwright validation Job."));
            await CreateJobAsync(client, context.Namespace!, jobName, secretName, sharedStatePvcName, labels, modeText, runtimeConfiguration.Environment.Keys, cancellationToken);
            progress?.Invoke(new PlaywrightValidationProgress("playwright-job", UpgradeJobStageStatus.Succeeded, "Playwright validation Job created."));

            progress?.Invoke(new PlaywrightValidationProgress("playwright-runner", UpgradeJobStageStatus.Running, "Waiting for Playwright runner pod."));
            executionResult = await MonitorJobAsync(
                client,
                context.Namespace!,
                jobName,
                labels,
                mode,
                upgradeJobId,
                runtimeConfiguration.RedactionValues,
                startedAt,
                displayName,
                progress,
                cancellationToken);

            return executionResult;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            var message = $"Playwright validation infrastructure failed: {exception.Message}";
            logger.LogWarning(exception, "Playwright Kubernetes Job execution failed. JobName: {JobName}. No sensitive values were logged.", jobName);
            return CreateResult(false, null, false, startedAt, displayName, string.Empty, message, message);
        }
        finally
        {
            reportUploadTokenStore.Revoke(upgradeJobId, mode);
            if (cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await DeleteJobAsync(client, context.Namespace!, jobName, CancellationToken.None);
                    logger.LogInformation("Playwright validation Job deleted after cancellation. JobName: {JobName}.", jobName);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Playwright validation Job cleanup failed after cancellation. JobName: {JobName}.", jobName);
                }
            }

            if (ShouldDeleteSharedStatePvc(mode, executionResult))
            {
                try
                {
                    await DeletePersistentVolumeClaimAsync(client, context.Namespace!, sharedStatePvcName, CancellationToken.None);
                    logger.LogInformation("Temporary Playwright shared state PVC deleted. PersistentVolumeClaimName: {PersistentVolumeClaimName}.", sharedStatePvcName);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Temporary Playwright shared state PVC cleanup failed. PersistentVolumeClaimName: {PersistentVolumeClaimName}.", sharedStatePvcName);
                }
            }

            if (secretCreated)
            {
                try
                {
                    await DeleteSecretAsync(client, context.Namespace!, secretName, CancellationToken.None);
                    logger.LogInformation("Temporary Playwright Secret deleted. SecretName: {SecretName}.", secretName);
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "Temporary Playwright Secret cleanup failed. SecretName: {SecretName}.", secretName);
                }
            }
        }
    }

    public async Task CleanupSharedStateAsync(string upgradeJobId, CancellationToken cancellationToken = default)
    {
        if (!playwrightOptions.SharedStateVolumeEnabled)
        {
            return;
        }

        var context = ResolveContext();
        if (!context.Succeeded)
        {
            logger.LogWarning(
                "Playwright shared state cleanup skipped because Kubernetes context could not be resolved. JobId: {JobId}. Message: {Message}",
                upgradeJobId,
                context.Message);
            return;
        }

        using var client = CreateKubernetesHttpClient(context);
        var pvcName = GetSharedStatePvcName(upgradeJobId);
        await DeletePersistentVolumeClaimAsync(client, context.Namespace!, pvcName, cancellationToken);
    }

    private async Task DeleteJobAsync(HttpClient client, string kubernetesNamespace, string jobName, CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync(
            $"/apis/batch/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/jobs/{Uri.EscapeDataString(jobName)}?propagationPolicy=Foreground",
            cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private async Task<RuntimeConfiguration> ResolveRuntimeConfigurationAsync(CancellationToken cancellationToken)
    {
        var runtime = await configurationProvider.GetConfigurationAsync(cancellationToken);
        var masterDatabase = await databaseDiscoveryService.DiscoverMasterDatabaseAsync(cancellationToken);
        var databaseValues = ParseDatabaseValues(masterDatabase);
        var adminUsername = FirstConfiguredValue(
            playwrightOptions.AdminUsername,
            configuration["UpgradeCenter:Playwright:AdminUsername"],
            configuration["BOLDBI_ADMIN_USERNAME"]);
        var adminPassword = FirstConfiguredValue(
            playwrightOptions.AdminPassword,
            configuration["UpgradeCenter:Playwright:AdminPassword"],
            configuration["BOLDBI_ADMIN_PASSWORD"]);
        var passRateThreshold = ResolvePassRateThreshold();

        var values = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["BOLDBI_BASE_URL"] = Required(runtime.IdpBaseUrl, "IDP public base URL"),
            ["BOLDBI_ADMIN_USERNAME"] = Required(adminUsername, "Playwright admin username"),
            ["BOLDBI_ADMIN_PASSWORD"] = Required(adminPassword, "Playwright admin password"),
            ["BOLDBI_API_KEY_ID"] = Required(runtime.InternalAppClientId, "internal app client ID"),
            ["BOLDBI_API_KEY_SECRET"] = Required(runtime.InternalAppClientSecret, "internal app client secret"),
            ["BOLDBI_DB_HOST"] = Required(databaseValues.Host, "database host"),
            ["BOLDBI_DB_PORT"] = Required(databaseValues.Port, "database port"),
            ["BOLDBI_DB_NAME"] = Required(databaseValues.DatabaseName, "database name"),
            ["BOLDBI_DB_TYPE"] = databaseValues.DatabaseType,
            ["BOLDBI_DB_USERNAME"] = Required(databaseValues.Username, "database username"),
            ["BOLDBI_DB_PASSWORD"] = Required(databaseValues.Password, "database password"),
            ["BOLDBI_DB_SSL"] = databaseValues.SslEnabled ? "true" : "false",
            ["BOLDBI_PLAYWRIGHT_PASS_THRESHOLD"] = passRateThreshold.ToString(System.Globalization.CultureInfo.InvariantCulture),
            ["PLAYWRIGHT_PRE_JUNIT_REPORT_PATH"] = GetConfiguredReportPath(
                "UpgradeCenter:Playwright:PreJunitReportPath",
                playwrightOptions.PreJunitReportPath),
            ["PLAYWRIGHT_POST_JUNIT_REPORT_PATH"] = GetConfiguredReportPath(
                "UpgradeCenter:Playwright:PostJunitReportPath",
                playwrightOptions.PostJunitReportPath),
            ["PLAYWRIGHT_PRE_HTML_REPORT_PATH"] = GetConfiguredReportPath(
                "UpgradeCenter:Playwright:PreHtmlReportPath",
                playwrightOptions.PreHtmlReportPath),
            ["PLAYWRIGHT_POST_HTML_REPORT_PATH"] = GetConfiguredReportPath(
                "UpgradeCenter:Playwright:PostHtmlReportPath",
                playwrightOptions.PostHtmlReportPath),
            ["PLAYWRIGHT_PRE_HTML_REPORT_DIR"] = GetConfiguredReportPath(
                "UpgradeCenter:Playwright:PreHtmlReportDirectory",
                playwrightOptions.PreHtmlReportDirectory),
            ["PLAYWRIGHT_POST_HTML_REPORT_DIR"] = GetConfiguredReportPath(
                "UpgradeCenter:Playwright:PostHtmlReportDirectory",
                playwrightOptions.PostHtmlReportDirectory)
        };

        AddOptional(values, "BOLDBI_DB_MAINTENANCE");
        AddOptional(values, "BOLDBI_DB_ADDITIONAL_PARAMETERS");
        AddOptional(values, "BOLDBI_TESTDATA_DB_HOST");
        AddOptional(values, "BOLDBI_TESTDATA_DB_PORT");
        AddOptional(values, "BOLDBI_TESTDATA_DB_USERNAME");
        AddOptional(values, "BOLDBI_TESTDATA_DB_PASSWORD");
        AddOptional(values, "BOLDBI_TESTDATA_DB_NAME");
        AddOptional(values, "BOLDBI_TESTDATA_DB_SCHEMA");
        AddOptional(values, "BOLDBI_TESTDATA_DB_TABLE");
        AddOptional(values, "BOLDBI_TESTDATA_ETL_DB_NAME");
        AddOptional(values, "PLAYWRIGHT_POD_HOLD_SECONDS");

        var redactionValues = values.Values
            .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 4)
            .Distinct(StringComparer.Ordinal)
            .ToList();

        return new RuntimeConfiguration(values, redactionValues);
    }

    private int ResolvePassRateThreshold()
    {
        var rawValue = FirstConfiguredValue(
            configuration["UpgradeCenter:Playwright:PassRateThreshold"],
            playwrightOptions.PassRateThreshold,
            configuration["BOLDBI_PLAYWRIGHT_PASS_THRESHOLD"],
            "100");

        if (!int.TryParse(rawValue, out var threshold) || threshold < 0 || threshold > 100)
        {
            throw new InvalidOperationException("Playwright pass-rate threshold must be configured as a number between 0 and 100.");
        }

        return threshold;
    }

    private string GetConfiguredReportPath(string key, string configuredValue)
    {
        var value = FirstConfiguredValue(configuration[key], configuredValue);
        return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
    }

    private void AddOptional(IDictionary<string, string> values, string key)
    {
        var value = configuration[key];
        if (!string.IsNullOrWhiteSpace(value))
        {
            values[key] = value.Trim();
        }
    }

    private DatabaseRuntimeValues ParseDatabaseValues(DiscoveredDatabaseConnection database)
    {
        return database.DatabaseType switch
        {
            DatabaseType.PostgreSQL => ParsePostgreSql(database.ConnectionString),
            DatabaseType.MySQL => ParseMySql(database.ConnectionString),
            DatabaseType.MSSQL => ParseSqlServer(database.ConnectionString),
            DatabaseType.Oracle => ParseOracle(database.ConnectionString),
            _ => throw new NotSupportedException($"Database type '{database.DatabaseType}' is not supported for Playwright runtime configuration.")
        };
    }

    private static DatabaseRuntimeValues ParsePostgreSql(string connectionString)
    {
        var builder = new NpgsqlConnectionStringBuilder(connectionString);
        return new DatabaseRuntimeValues(
            builder.Host,
            builder.Port.ToString(),
            builder.Database,
            "PostgreSQL",
            builder.Username,
            builder.Password,
            builder.SslMode != SslMode.Disable);
    }

    private static DatabaseRuntimeValues ParseMySql(string connectionString)
    {
        var builder = new MySqlConnectionStringBuilder(connectionString);
        return new DatabaseRuntimeValues(
            builder.Server,
            builder.Port.ToString(),
            builder.Database,
            "MySQL",
            builder.UserID,
            builder.Password,
            builder.SslMode != MySqlSslMode.None);
    }

    private static DatabaseRuntimeValues ParseSqlServer(string connectionString)
    {
        var builder = new SqlConnectionStringBuilder(connectionString);
        var (host, port) = SplitSqlServerDataSource(builder.DataSource);
        return new DatabaseRuntimeValues(
            host,
            port,
            builder.InitialCatalog,
            "MSSQL",
            builder.UserID,
            builder.Password,
            builder.Encrypt);
    }

    private static DatabaseRuntimeValues ParseOracle(string connectionString)
    {
        var builder = new OracleConnectionStringBuilder(connectionString);
        var dataSource = builder.DataSource;
        var (host, port) = SplitOracleDataSource(dataSource);
        return new DatabaseRuntimeValues(
            host,
            port,
            string.IsNullOrWhiteSpace(dataSource) ? builder.UserID : dataSource,
            "Oracle",
            builder.UserID,
            builder.Password,
            false);
    }

    private static (string Host, string Port) SplitSqlServerDataSource(string dataSource)
    {
        var normalized = dataSource.Trim();
        if (normalized.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase))
        {
            normalized = normalized[4..];
        }

        var parts = normalized.Split(',', 2, StringSplitOptions.TrimEntries);
        return parts.Length == 2
            ? (parts[0], parts[1])
            : (normalized, "1433");
    }

    private static (string Host, string Port) SplitOracleDataSource(string dataSource)
    {
        var match = Regex.Match(dataSource, @"(?<host>[^/:]+):(?<port>\d+)");
        return match.Success
            ? (match.Groups["host"].Value, match.Groups["port"].Value)
            : (dataSource, "1521");
    }

    private async Task CreateSecretAsync(
        HttpClient client,
        string kubernetesNamespace,
        string secretName,
        IReadOnlyDictionary<string, string> labels,
        IReadOnlyDictionary<string, string> values,
        CancellationToken cancellationToken)
    {
        var body = new
        {
            apiVersion = "v1",
            kind = "Secret",
            metadata = new
            {
                name = secretName,
                labels
            },
            type = "Opaque",
            stringData = values
        };

        using var response = await client.PostAsync(
            $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/secrets",
            ToJsonContent(body),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task CreateOrReuseSharedStatePvcAsync(
        HttpClient client,
        string kubernetesNamespace,
        string pvcName,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken)
    {
        var spec = new Dictionary<string, object?>
        {
            ["accessModes"] = new[] { GetSharedStateAccessMode() },
            ["resources"] = new
            {
                requests = new
                {
                    storage = GetSharedStateStorageSize()
                }
            }
        };

        var storageClassName = FirstConfiguredValue(
            playwrightOptions.SharedStateStorageClassName,
            configuration["UpgradeCenter:Playwright:SharedStateStorageClassName"]);
        if (!string.IsNullOrWhiteSpace(storageClassName))
        {
            spec["storageClassName"] = storageClassName.Trim();
        }

        var body = new
        {
            apiVersion = "v1",
            kind = "PersistentVolumeClaim",
            metadata = new
            {
                name = pvcName,
                labels
            },
            spec
        };

        using var response = await client.PostAsync(
            $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/persistentvolumeclaims",
            ToJsonContent(body),
            cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.Conflict)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private async Task CreateJobAsync(
        HttpClient client,
        string kubernetesNamespace,
        string jobName,
        string secretName,
        string sharedStatePvcName,
        IReadOnlyDictionary<string, string> labels,
        string mode,
        IEnumerable<string> secretKeys,
        CancellationToken cancellationToken)
    {
        var env = secretKeys
            .Where(key => !string.IsNullOrWhiteSpace(key))
            .Distinct(StringComparer.Ordinal)
            .Select(key => new
            {
                name = key,
                valueFrom = new
                {
                    secretKeyRef = new
                    {
                        name = secretName,
                        key
                    }
                }
            })
            .ToArray();

        object?[] volumeMounts = playwrightOptions.SharedStateVolumeEnabled
            ? new object?[]
            {
                new
                {
                    name = "playwright-shared-state",
                    mountPath = GetSharedStateMountPath()
                }
            }
            : Array.Empty<object?>();

        object?[] volumes = playwrightOptions.SharedStateVolumeEnabled
            ? new object?[]
            {
                new
                {
                    name = "playwright-shared-state",
                    persistentVolumeClaim = new
                    {
                        claimName = sharedStatePvcName
                    }
                }
            }
            : Array.Empty<object?>();

        var podSpec = new Dictionary<string, object?>
        {
            ["restartPolicy"] = "Never",
            ["containers"] = new object[]
            {
                new
                {
                    name = "playwright-runner",
                    image = playwrightOptions.RunnerImage,
                    imagePullPolicy = "IfNotPresent",
                    args = new[] { mode },
                    env,
                    volumeMounts
                }
            }
        };

        if (playwrightOptions.SharedStateVolumeEnabled)
        {
            var sharedStateFsGroup = GetSharedStateFsGroup();
            if (sharedStateFsGroup.HasValue)
            {
                podSpec["securityContext"] = new
                {
                    fsGroup = sharedStateFsGroup.Value
                };
            }
        }

        if (volumes.Length > 0)
        {
            podSpec["volumes"] = volumes;
        }

        var spec = new Dictionary<string, object?>
        {
            ["backoffLimit"] = Math.Max(0, playwrightOptions.BackoffLimit),
            ["activeDeadlineSeconds"] = Math.Max(300, playwrightOptions.JobTimeoutSeconds),
            ["template"] = new
            {
                metadata = new
                {
                    labels
                },
                spec = podSpec
            }
        };

        if (playwrightOptions.CompletedJobTtlSeconds.HasValue)
        {
            spec["ttlSecondsAfterFinished"] = Math.Max(0, playwrightOptions.CompletedJobTtlSeconds.Value);
        }

        var body = new
        {
            apiVersion = "batch/v1",
            kind = "Job",
            metadata = new
            {
                name = jobName,
                labels
            },
            spec
        };

        using var response = await client.PostAsync(
            $"/apis/batch/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/jobs",
            ToJsonContent(body),
            cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    private async Task DeletePersistentVolumeClaimAsync(HttpClient client, string kubernetesNamespace, string pvcName, CancellationToken cancellationToken)
    {
        if (!playwrightOptions.SharedStateVolumeEnabled)
        {
            return;
        }

        using var response = await client.DeleteAsync(
            $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/persistentvolumeclaims/{Uri.EscapeDataString(pvcName)}",
            cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private async Task<PlaywrightScriptResult> MonitorJobAsync(
        HttpClient client,
        string kubernetesNamespace,
        string jobName,
        IReadOnlyDictionary<string, string> labels,
        PlaywrightValidationMode mode,
        string upgradeJobId,
        IReadOnlyList<string> redactionValues,
        DateTimeOffset startedAt,
        string command,
        Action<PlaywrightValidationProgress>? progress,
        CancellationToken cancellationToken)
    {
        var timeout = TimeSpan.FromSeconds(Math.Max(300, playwrightOptions.JobTimeoutSeconds + 60));
        var pollDelay = TimeSpan.FromSeconds(Math.Clamp(playwrightOptions.JobPollSeconds, 2, 60));
        var deadline = DateTimeOffset.UtcNow.Add(timeout);
        string? podName = null;
        string? lastStateMessage = null;

        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var job = await GetJsonAsync(client, $"/apis/batch/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/jobs/{Uri.EscapeDataString(jobName)}", cancellationToken);
            var condition = GetJobTerminalCondition(job.RootElement);
            podName ??= await TryResolvePodNameAsync(client, kubernetesNamespace, labels, cancellationToken);
            var podState = podName is null
                ? PodState.Pending("Waiting for Playwright runner pod to be scheduled.")
                : await GetPodStateAsync(client, kubernetesNamespace, podName, cancellationToken);

            lastStateMessage = podState.Message;
            UpdateRunningProgress(podState, progress);

            if (condition == "Complete")
            {
                var logs = podName is null
                    ? string.Empty
                    : await TryReadPodLogsAsync(client, kubernetesNamespace, podName, cancellationToken);
                TryCaptureHtmlReport(upgradeJobId, mode, logs);
                logs = PrepareLogsForDisplay(logs, redactionValues);
                var resultSummary = TryGetUploadedResultSummary(upgradeJobId, mode) ?? TryParseResultSummary(logs);
                var successMessage = resultSummary?.ToMessage("Playwright validation completed successfully.")
                    ?? "Playwright validation completed successfully.";
                progress?.Invoke(new PlaywrightValidationProgress("playwright-tests", UpgradeJobStageStatus.Succeeded, successMessage));
                return CreateResult(true, podState.ExitCode ?? 0, false, startedAt, command, logs, string.Empty, successMessage);
            }

            if (condition == "Failed" || podState.IsFailed)
            {
                var logs = podName is null
                    ? string.Empty
                    : await TryReadPodLogsAsync(client, kubernetesNamespace, podName, cancellationToken);
                TryCaptureHtmlReport(upgradeJobId, mode, logs);
                logs = PrepareLogsForDisplay(logs, redactionValues);
                var resultSummary = TryGetUploadedResultSummary(upgradeJobId, mode) ?? TryParseResultSummary(logs);
                var message = podState.IsInfrastructureFailure
                    ? $"Playwright validation infrastructure failed: {podState.Message}"
                    : $"Playwright validation failed: {podState.Message}";
                message = resultSummary?.ToMessage(message) ?? message;
                progress?.Invoke(new PlaywrightValidationProgress("playwright-tests", UpgradeJobStageStatus.Failed, message));
                return CreateResult(false, podState.ExitCode, false, startedAt, command, logs, string.Empty, message);
            }

            await Task.Delay(pollDelay, cancellationToken);
        }

        var timeoutLogs = podName is null
            ? string.Empty
            : await TryReadPodLogsAsync(client, kubernetesNamespace, podName, CancellationToken.None);
        TryCaptureHtmlReport(upgradeJobId, mode, timeoutLogs);

        var timeoutMessage = $"Playwright validation timed out. Last status: {lastStateMessage ?? "status was unavailable"}.";
        progress?.Invoke(new PlaywrightValidationProgress("playwright-tests", UpgradeJobStageStatus.Failed, timeoutMessage));
        return CreateResult(false, null, true, startedAt, command, PrepareLogsForDisplay(timeoutLogs, redactionValues), string.Empty, timeoutMessage);
    }

    private void TryCaptureHtmlReport(string upgradeJobId, PlaywrightValidationMode mode, string logs)
    {
        var stage = mode == PlaywrightValidationMode.Pre
            ? "Pre-Upgrade Validation"
            : "Post-Upgrade Validation";

        if (reportStore.Exists(upgradeJobId, mode))
        {
            operationLogStore.Append(
                upgradeJobId,
                stage,
                "Info",
                "PlaywrightReport",
                "Playwright HTML report is already uploaded and stored for this validation stage.");
            return;
        }

        if (string.IsNullOrWhiteSpace(logs))
        {
            operationLogStore.Append(
                upgradeJobId,
                stage,
                "Warning",
                "PlaywrightReport",
                "Playwright HTML report could not be captured because pod logs were empty or unavailable.");
            return;
        }

        var saved = reportStore.TrySaveFromLogs(upgradeJobId, mode, logs);
        operationLogStore.Append(
            upgradeJobId,
            stage,
            saved ? "Info" : "Warning",
            "PlaywrightReport",
            saved
                ? "Playwright HTML report captured and stored for this validation stage."
                : "Playwright HTML report was not captured. The expected single-report marker was not found in the Playwright pod logs. Verify that the runner generated and uploaded the configured report path before the pod was cleaned up.");
    }

    private static void UpdateRunningProgress(PodState podState, Action<PlaywrightValidationProgress>? progress)
    {
        if (progress is null)
        {
            return;
        }

        if (podState.Phase.Equals("Running", StringComparison.OrdinalIgnoreCase))
        {
            progress(new PlaywrightValidationProgress("playwright-runner", UpgradeJobStageStatus.Succeeded, "Playwright runner pod is running."));
            progress(new PlaywrightValidationProgress("playwright-tests", UpgradeJobStageStatus.Running, "Running Playwright validation tests."));
            return;
        }

        progress(new PlaywrightValidationProgress("playwright-runner", UpgradeJobStageStatus.Running, podState.Message));
    }

    private async Task<JsonDocument> GetJsonAsync(HttpClient client, string uri, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(uri, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private async Task<string?> TryResolvePodNameAsync(
        HttpClient client,
        string kubernetesNamespace,
        IReadOnlyDictionary<string, string> labels,
        CancellationToken cancellationToken)
    {
        var selector = string.Join(",", labels.Select(label => $"{label.Key}={label.Value}"));
        using var response = await client.GetAsync(
            $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/pods?labelSelector={Uri.EscapeDataString(selector)}",
            cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        if (!document.RootElement.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        return items.EnumerateArray()
            .Select(item => TryGetString(item, "metadata", "name"))
            .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
    }

    private async Task<PodState> GetPodStateAsync(HttpClient client, string kubernetesNamespace, string podName, CancellationToken cancellationToken)
    {
        using var document = await GetJsonAsync(client, $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/pods/{Uri.EscapeDataString(podName)}", cancellationToken);
        var root = document.RootElement;
        var phase = TryGetString(root, "status", "phase") ?? "Pending";
        var containerStatus = TryGetFirstArrayElement(root, "status", "containerStatuses");
        if (containerStatus.HasValue)
        {
            var state = containerStatus.Value.GetProperty("state");
            if (state.TryGetProperty("terminated", out var terminated))
            {
                var exitCode = TryGetInt(terminated, "exitCode");
                var reason = TryGetString(terminated, "reason") ?? "Terminated";
                var message = TryGetString(terminated, "message") ?? reason;
                return exitCode == 0
                    ? new PodState(phase, false, false, exitCode, "Playwright runner completed successfully.")
                    : new PodState(phase, true, false, exitCode, $"{reason}. {message}".Trim());
            }

            if (state.TryGetProperty("waiting", out var waiting))
            {
                var reason = TryGetString(waiting, "reason") ?? "Waiting";
                var message = TryGetString(waiting, "message") ?? reason;
                var isInfrastructureFailure = reason.Contains("ImagePull", StringComparison.OrdinalIgnoreCase) ||
                                              reason.Contains("ErrImage", StringComparison.OrdinalIgnoreCase) ||
                                              reason.Contains("CreateContainer", StringComparison.OrdinalIgnoreCase);
                return new PodState(phase, isInfrastructureFailure, isInfrastructureFailure, null, $"{reason}. {message}".Trim());
            }
        }

        return PodState.Pending($"Playwright runner pod phase: {phase}.");
    }

    private async Task<string> TryReadPodLogsAsync(
        HttpClient client,
        string kubernetesNamespace,
        string podName,
        CancellationToken cancellationToken)
    {
        try
        {
            using var response = await client.GetAsync(
                $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/pods/{Uri.EscapeDataString(podName)}/log?container=playwright-runner",
                cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                return $"Playwright pod logs could not be read. Status code: {(int)response.StatusCode}.";
            }

            return await response.Content.ReadAsStringAsync(cancellationToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return $"Playwright pod logs could not be read: {exception.Message}";
        }
    }

    private async Task DeleteSecretAsync(HttpClient client, string kubernetesNamespace, string secretName, CancellationToken cancellationToken)
    {
        using var response = await client.DeleteAsync(
            $"/api/v1/namespaces/{Uri.EscapeDataString(kubernetesNamespace)}/secrets/{Uri.EscapeDataString(secretName)}",
            cancellationToken);
        if (response.IsSuccessStatusCode || response.StatusCode == System.Net.HttpStatusCode.NotFound)
        {
            return;
        }

        response.EnsureSuccessStatusCode();
    }

    private KubernetesRuntimeContext ResolveContext()
    {
        var kubernetesNamespace = FirstConfiguredValue(
            kubernetesOptions.Namespace,
            Environment.GetEnvironmentVariable("POD_NAMESPACE"),
            ReadFileIfExists(kubernetesOptions.NamespacePath));

        var apiServer = FirstConfiguredValue(
            kubernetesOptions.ApiServer,
            ResolveInClusterApiServer());

        if (string.IsNullOrWhiteSpace(kubernetesNamespace))
        {
            return KubernetesRuntimeContext.Failed(null, "Kubernetes namespace could not be resolved from configuration, POD_NAMESPACE, or the service account namespace file.");
        }

        if (string.IsNullOrWhiteSpace(apiServer))
        {
            return KubernetesRuntimeContext.Failed(kubernetesNamespace, "Kubernetes API server could not be resolved from configuration or in-cluster environment variables.");
        }

        if (!File.Exists(kubernetesOptions.ServiceAccountTokenPath))
        {
            return KubernetesRuntimeContext.Failed(kubernetesNamespace, $"Kubernetes service account token was not found at '{kubernetesOptions.ServiceAccountTokenPath}'.");
        }

        var token = File.ReadAllText(kubernetesOptions.ServiceAccountTokenPath).Trim();
        if (string.IsNullOrWhiteSpace(token))
        {
            return KubernetesRuntimeContext.Failed(kubernetesNamespace, "Kubernetes service account token file is empty.");
        }

        return new KubernetesRuntimeContext(true, kubernetesNamespace.Trim(), apiServer.TrimEnd('/'), token, "Kubernetes runtime context resolved.");
    }

    private HttpClient CreateKubernetesHttpClient(KubernetesRuntimeContext context)
    {
        var handler = new HttpClientHandler();
        if (kubernetesOptions.SkipTlsVerify)
        {
            handler.ServerCertificateCustomValidationCallback = HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        else if (File.Exists(kubernetesOptions.CertificateAuthorityPath))
        {
            var certificateAuthority = new X509Certificate2(kubernetesOptions.CertificateAuthorityPath);
            handler.ServerCertificateCustomValidationCallback = (_, certificate, chain, sslPolicyErrors) =>
            {
                if (certificate is null)
                {
                    return false;
                }

                if (sslPolicyErrors == SslPolicyErrors.None)
                {
                    return true;
                }

                using var customChain = new X509Chain();
                customChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
                customChain.ChainPolicy.CustomTrustStore.Add(certificateAuthority);
                customChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
                return customChain.Build(certificate as X509Certificate2 ?? new X509Certificate2(certificate));
            };
        }

        var client = new HttpClient(handler)
        {
            BaseAddress = new Uri(context.ApiServer!)
        };
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", context.Token);
        client.DefaultRequestHeaders.Accept.ParseAdd("application/json");
        return client;
    }

    private static HttpContent ToJsonContent(object value)
    {
        return new StringContent(JsonSerializer.Serialize(value, JsonOptions), Encoding.UTF8, "application/json");
    }

    private static string? GetJobTerminalCondition(JsonElement root)
    {
        if (!root.TryGetProperty("status", out var status) ||
            !status.TryGetProperty("conditions", out var conditions) ||
            conditions.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var condition in conditions.EnumerateArray())
        {
            var type = TryGetString(condition, "type");
            var statusText = TryGetString(condition, "status");
            if (string.Equals(statusText, "True", StringComparison.OrdinalIgnoreCase) &&
                (string.Equals(type, "Complete", StringComparison.OrdinalIgnoreCase) ||
                 string.Equals(type, "Failed", StringComparison.OrdinalIgnoreCase)))
            {
                return type;
            }
        }

        return null;
    }

    private static JsonElement? TryGetFirstArrayElement(JsonElement root, params string[] path)
    {
        var current = root;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.Array && current.GetArrayLength() > 0
            ? current[0]
            : null;
    }

    private static string? TryGetString(JsonElement element, params string[] path)
    {
        var current = element;
        foreach (var segment in path)
        {
            if (!current.TryGetProperty(segment, out current))
            {
                return null;
            }
        }

        return current.ValueKind == JsonValueKind.String ? current.GetString() : current.GetRawText();
    }

    private static int? TryGetInt(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetInt32(out var number)
            ? number
            : null;
    }

    private static string Required(string? value, string name)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{name} is not configured.");
        }

        return value.Trim();
    }

    private static string PrepareLogsForDisplay(string logs, IReadOnlyList<string> redactionValues)
    {
        return SanitizeLogs(RemoveEmbeddedReport(logs), redactionValues);
    }

    private static string RemoveEmbeddedReport(string logs)
    {
        return RemoveMarkedBlock(
            logs,
            PlaywrightReportStore.ReportBeginMarker,
            PlaywrightReportStore.ReportEndMarker);
    }

    private static string RemoveMarkedBlock(string logs, string beginMarker, string endMarker)
    {
        var begin = logs.LastIndexOf(beginMarker, StringComparison.Ordinal);
        if (begin < 0)
        {
            return logs;
        }

        var end = logs.IndexOf(endMarker, begin, StringComparison.Ordinal);
        if (end < 0)
        {
            return logs[..begin];
        }

        end += endMarker.Length;
        return logs.Remove(begin, end - begin);
    }

    private string BuildReportUploadUrl(string upgradeJobId, PlaywrightValidationMode mode)
    {
        var baseUrl = FirstConfiguredValue(
            playwrightOptions.ReportUploadBaseUrl,
            configuration["UpgradeCenter:Playwright:ReportUploadBaseUrl"],
            "http://boldbi-upgrade-center/upgrade-center/api/playwright-reports");
        var stage = mode == PlaywrightValidationMode.Pre ? "pre-upgrade" : "post-upgrade";
        return $"{baseUrl!.TrimEnd('/')}/{Uri.EscapeDataString(upgradeJobId)}/{stage}";
    }

    private string BuildResultUploadUrl(string upgradeJobId, PlaywrightValidationMode mode)
    {
        var baseUrl = FirstConfiguredValue(
            playwrightOptions.ResultUploadBaseUrl,
            configuration["UpgradeCenter:Playwright:ResultUploadBaseUrl"],
            "http://boldbi-upgrade-center/upgrade-center/api/playwright-results");
        var stage = mode == PlaywrightValidationMode.Pre ? "pre-upgrade" : "post-upgrade";
        return $"{baseUrl!.TrimEnd('/')}/{Uri.EscapeDataString(upgradeJobId)}/{stage}";
    }

    private static string SanitizeLogs(string logs, IReadOnlyList<string> redactionValues)
    {
        var sanitized = logs;
        foreach (var value in redactionValues)
        {
            sanitized = sanitized.Replace(value, "***", StringComparison.Ordinal);
        }

        sanitized = SensitiveLinePattern.Replace(sanitized, match => $"{match.Groups["key"].Value}=***");
        return sanitized.Length <= 12000 ? sanitized : sanitized[^12000..];
    }

    private static PlaywrightResultSummary? TryParseResultSummary(string logs)
    {
        const string marker = "PLAYWRIGHT_RESULT_JSON ";
        var index = logs.LastIndexOf(marker, StringComparison.Ordinal);
        if (index < 0)
        {
            return null;
        }

        var start = index + marker.Length;
        var lineEnd = logs.IndexOfAny(['\r', '\n'], start);
        var json = lineEnd < 0 ? logs[start..] : logs[start..lineEnd];
        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            return new PlaywrightResultSummary(
                TryGetInt(root, "total") ?? 0,
                TryGetInt(root, "passed") ?? 0,
                TryGetInt(root, "failed") ?? 0,
                TryGetInt(root, "skipped") ?? 0,
                TryGetDouble(root, "passPercentage") ?? 0,
                TryGetInt(root, "threshold") ?? 100,
                TryGetString(root, "status") ?? "Unknown");
        }
        catch
        {
            return null;
        }
    }

    private PlaywrightResultSummary? TryGetUploadedResultSummary(string upgradeJobId, PlaywrightValidationMode mode)
    {
        return validationResultStore.TryGet(upgradeJobId, mode, out var summary) && summary is not null
            ? new PlaywrightResultSummary(
                summary.Total,
                summary.Passed,
                summary.Failed,
                summary.Skipped,
                summary.PassPercentage,
                summary.Threshold,
                summary.Status)
            : null;
    }

    private static double? TryGetDouble(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.Number && value.TryGetDouble(out var number)
            ? number
            : null;
    }

    private static string CreateResourceNameSuffix(string jobId)
    {
        var normalized = Regex.Replace(jobId.ToLowerInvariant(), "[^a-z0-9]+", "-").Trim('-');
        if (string.IsNullOrWhiteSpace(normalized))
        {
            normalized = Guid.NewGuid().ToString("N");
        }

        return normalized.Length <= 28 ? normalized : normalized[..28].Trim('-');
    }

    private static string GetSharedStatePvcName(string jobId)
    {
        return $"upgrade-pw-state-{CreateResourceNameSuffix(jobId)}";
    }

    private bool ShouldDeleteSharedStatePvc(PlaywrightValidationMode mode, PlaywrightScriptResult? result)
    {
        if (!playwrightOptions.SharedStateVolumeEnabled)
        {
            return false;
        }

        return mode == PlaywrightValidationMode.Post ||
               result is null ||
               !result.Succeeded;
    }

    private string GetSharedStateAccessMode()
    {
        return FirstConfiguredValue(
            playwrightOptions.SharedStateAccessMode,
            configuration["UpgradeCenter:Playwright:SharedStateAccessMode"],
            "ReadWriteOnce")!.Trim();
    }

    private string GetSharedStateStorageSize()
    {
        return FirstConfiguredValue(
            playwrightOptions.SharedStateStorageSize,
            configuration["UpgradeCenter:Playwright:SharedStateStorageSize"],
            "1Gi")!.Trim();
    }

    private string GetSharedStateMountPath()
    {
        return FirstConfiguredValue(
            playwrightOptions.SharedStateMountPath,
            configuration["UpgradeCenter:Playwright:SharedStateMountPath"],
            "/app/playwright-shared-state")!.TrimEnd('/');
    }

    private int? GetSharedStateFsGroup()
    {
        var configuredValue = FirstConfiguredValue(
            playwrightOptions.SharedStateFsGroup?.ToString(System.Globalization.CultureInfo.InvariantCulture),
            configuration["UpgradeCenter:Playwright:SharedStateFsGroup"]);

        if (string.IsNullOrWhiteSpace(configuredValue))
        {
            return null;
        }

        return int.TryParse(configuredValue, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out var fsGroup) && fsGroup > 0
            ? fsGroup
            : null;
    }

    private static string? ReadFileIfExists(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        return File.ReadAllText(path).Trim();
    }

    private static string? ResolveInClusterApiServer()
    {
        var host = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_HOST");
        var port = Environment.GetEnvironmentVariable("KUBERNETES_SERVICE_PORT") ?? "443";
        return string.IsNullOrWhiteSpace(host) ? null : $"https://{host}:{port}";
    }

    private static string? FirstConfiguredValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }

    private static PlaywrightScriptResult CreateResult(
        bool succeeded,
        int? exitCode,
        bool timedOut,
        DateTimeOffset startedAt,
        string command,
        string standardOutput,
        string standardError,
        string message)
    {
        return new PlaywrightScriptResult(
            succeeded,
            exitCode,
            timedOut,
            startedAt,
            DateTimeOffset.UtcNow,
            command,
            standardOutput,
            standardError,
            message);
    }

    private sealed record RuntimeConfiguration(
        IReadOnlyDictionary<string, string> Environment,
        IReadOnlyList<string> RedactionValues)
    {
        public RuntimeConfiguration WithPlaywrightUploads(string reportUploadUrl, string resultUploadUrl, string token)
        {
            var values = Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            values["BOLD_UPGRADE_CENTER_REPORT_UPLOAD_URL"] = reportUploadUrl;
            values["BOLD_UPGRADE_CENTER_RESULT_UPLOAD_URL"] = resultUploadUrl;
            values["BOLD_UPGRADE_CENTER_REPORT_UPLOAD_TOKEN"] = token;

            var redactionValues = RedactionValues
                .Concat(new[] { token })
                .Where(value => !string.IsNullOrWhiteSpace(value) && value.Length >= 4)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            return new RuntimeConfiguration(values, redactionValues);
        }

        public RuntimeConfiguration WithSharedStateDirectory(string sharedStateDirectory)
        {
            var values = Environment.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
            values["BOLD_PLAYWRIGHT_SHARED_STATE_DIR"] = sharedStateDirectory;
            return new RuntimeConfiguration(values, RedactionValues);
        }
    }

    private sealed record DatabaseRuntimeValues(
        string? Host,
        string? Port,
        string? DatabaseName,
        string DatabaseType,
        string? Username,
        string? Password,
        bool SslEnabled);

    private sealed record PlaywrightResultSummary(
        int Total,
        int Passed,
        int Failed,
        int Skipped,
        double PassPercentage,
        int Threshold,
        string Status)
    {
        public string ToMessage(string prefix)
        {
            return $"{prefix} Total: {Total}. Passed: {Passed}. Failed: {Failed}. Skipped: {Skipped}. Pass percentage: {PassPercentage:0.##}%. Threshold: {Threshold}%. Final status: {Status}.";
        }
    }

    private sealed record KubernetesRuntimeContext(bool Succeeded, string? Namespace, string? ApiServer, string? Token, string Message)
    {
        public static KubernetesRuntimeContext Failed(string? kubernetesNamespace, string message)
        {
            return new KubernetesRuntimeContext(false, kubernetesNamespace, null, null, message);
        }
    }

    private sealed record PodState(
        string Phase,
        bool IsFailed,
        bool IsInfrastructureFailure,
        int? ExitCode,
        string Message)
    {
        public static PodState Pending(string message)
        {
            return new PodState("Pending", false, false, null, message);
        }
    }
}
