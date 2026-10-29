using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.CloudWatchLogs;
using Amazon.S3;
using Amazon.S3.Model;
using Microsoft.Extensions.Options;
using AwsTask = Amazon.ECS.Model.Task;
using Task = System.Threading.Tasks.Task;
using ResourceNotFoundException = Amazon.SecretsManager.Model.ResourceNotFoundException;

namespace Bold.UpgradeCenter.Services;

public sealed class EcsPlaywrightJobService(
    IAmazonECS ecs,
    IAmazonSecretsManager secrets,
    IAmazonCloudWatchLogs logs,
    IAmazonS3 s3,
    IOptions<EcsExecutionOptions> ecsOptions,
    IOptions<PlaywrightExecutionOptions> playwrightOptions,
    IPlaywrightRuntimeEnvironmentProvider runtimeProvider,
    IPlaywrightRunnerImageProvider images,
    IUpgradeJobStore jobs,
    IPlaywrightReportUploadTokenStore tokens,
    IPlaywrightValidationResultStore results,
    IPlaywrightReportStore reports,
    IUpgradeOperationLogStore operationLogs) : IPlaywrightKubernetesJobService
{
    private readonly EcsExecutionOptions settings = ecsOptions.Value;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private const string OwnerTag = "bold-upgrade-center-job";

    public async Task<PlaywrightScriptResult> RunAsync(PlaywrightValidationMode mode, string upgradeJobId, Action<PlaywrightValidationProgress>? progress = null,
        string? runnerImageVersion = null, CancellationToken cancellationToken = default)
    {
        var stage = Mode(mode);
        var started = DateTimeOffset.UtcNow;
        EcsValidationExecution? execution = null;
        string? preparedSecret = null;
        string? preparedDefinition = null;
        try
        {
            if (!jobs.TryGet(upgradeJobId, out var job)) throw new InvalidOperationException("Unknown upgrade operation.");
            if (job.EcsValidationExecutions.TryGetValue(stage, out var completed) && completed.Result is not null)
            {
                if (completed.Summary is not null) results.Save(upgradeJobId, mode, completed.Summary);
                if (mode == PlaywrightValidationMode.Cleanup && completed.Result.Succeeded) await CleanupSharedStateAsync(upgradeJobId, cancellationToken);
                return completed.Result;
            }
            if (mode == PlaywrightValidationMode.Cleanup) await StopTasksAsync(upgradeJobId, cancellationToken);
            if (!job.EcsValidationExecutions.TryGetValue(stage, out execution))
            {
                var image = await images.GetRunnerImageAsync(runnerImageVersion ?? "", cancellationToken);
                if (string.IsNullOrWhiteSpace(image)) throw new InvalidOperationException("No compatible ECS Playwright runner mapping.");
                var secretName = $"{settings.RuntimeSecretPrefix}/{Hash(upgradeJobId)}-{stage}";
                Dictionary<string, string> environment;
                string secretArn;
                try
                {
                    var existing = await ReadOwnedSecretAsync(secretName, upgradeJobId, stage, cancellationToken);
                    secretArn = existing.Arn;
                    environment = existing.Environment;
                    tokens.Register(upgradeJobId, mode, TimeSpan.FromSeconds(Timeout(mode) + 600), environment["BOLD_UPGRADE_CENTER_REPORT_UPLOAD_TOKEN"]);
                }
                catch (ResourceNotFoundException)
                {
                    var uploadToken = tokens.Register(upgradeJobId, mode, TimeSpan.FromSeconds(Timeout(mode) + 600));
                    environment = new(await runtimeProvider.ResolveEnvironmentAsync(cancellationToken));
                    environment["BOLD_UPGRADE_CENTER_REPORT_UPLOAD_URL"] = UploadUrl(upgradeJobId, stage, "playwright-reports");
                    environment["BOLD_UPGRADE_CENTER_RESULT_UPLOAD_URL"] = UploadUrl(upgradeJobId, stage, "playwright-results");
                    environment["BOLD_UPGRADE_CENTER_REPORT_UPLOAD_TOKEN"] = uploadToken;
                    environment["PLAYWRIGHT_POD_HOLD_SECONDS"] = "0";
                    if (settings.StateBucket is not null)
                    {
                        var key = StateKey(upgradeJobId);
                        foreach (var method in new[] { HttpVerb.GET, HttpVerb.PUT })
                        {
                            var signed = await s3.GetPreSignedURLAsync(new GetPreSignedUrlRequest
                            {
                                BucketName = settings.StateBucket, Key = key, Verb = method,
                                Expires = DateTime.UtcNow.AddSeconds(Timeout(mode) + 600)
                            });
                            environment[method == HttpVerb.GET ? "BOLD_PLAYWRIGHT_STATE_GET_URL" : "BOLD_PLAYWRIGHT_STATE_PUT_URL"] = signed;
                        }
                    }
                    else environment["BOLD_PLAYWRIGHT_SHARED_STATE_DIR"] = StateDirectory(upgradeJobId);
                    var created = await secrets.CreateSecretAsync(new()
                    {
                        Name = secretName, SecretString = JsonSerializer.Serialize(environment),
                        Tags = [new Amazon.SecretsManager.Model.Tag { Key = OwnerTag, Value = Hash(upgradeJobId) }, new Amazon.SecretsManager.Model.Tag { Key = "stage", Value = stage }]
                    }, cancellationToken);
                    secretArn = created.ARN;
                    preparedSecret = secretArn;
                }
                var definition = await ecs.DescribeTaskDefinitionAsync(new() { TaskDefinition = settings.RunnerTaskDefinition, Include = [TaskDefinitionField.TAGS] }, cancellationToken);
                var container = definition.TaskDefinition.ContainerDefinitions.Single(item => item.Name == settings.RunnerContainer);
                if (definition.TaskDefinition.RequiresCompatibilities?.Contains(Compatibility.FARGATE) != true || definition.TaskDefinition.NetworkMode != NetworkMode.Awsvpc)
                    throw new InvalidOperationException("Runner must be a Fargate awsvpc task definition.");
                container.Image = image;
                container.Command = [stage];
                if (settings.StateBucket is not null)
                {
                    // Use the existing release image; add only the S3 handoff around its original entrypoint.
                    container.EntryPoint = ["node", "-e"];
                    container.Command = [EcsPlaywrightBootstrap.Script, stage];
                }
                container.Environment = (container.Environment ?? []).Where(item => !environment.ContainsKey(item.Name)).ToList();
                container.Secrets = (container.Secrets ?? []).Where(item => !environment.ContainsKey(item.Name))
                    .Concat(environment.Keys.Select(name => new Amazon.ECS.Model.Secret { Name = name, ValueFrom = $"{secretArn}:{name}::" })).ToList();
                var request = EcsUpgradeService.CopyDefinition(definition);
                request.Tags = [new Amazon.ECS.Model.Tag { Key = OwnerTag, Value = Hash(upgradeJobId) }];
                var registered = await ecs.RegisterTaskDefinitionAsync(request, cancellationToken);
                preparedDefinition = registered.TaskDefinition.TaskDefinitionArn;
                execution = new(registered.TaskDefinition.TaskDefinitionArn, secretArn, Hash(upgradeJobId + ":" + stage), started);
                jobs.SaveEcsValidation(upgradeJobId, stage, execution);
            }
            else
            {
                var existing = await ReadOwnedSecretAsync(execution.SecretArn, upgradeJobId, stage, cancellationToken);
                tokens.Register(upgradeJobId, mode, TimeSpan.FromSeconds(Timeout(mode) + 600), existing.Environment["BOLD_UPGRADE_CENTER_REPORT_UPLOAD_TOKEN"]);
                started = execution.StartedAt;
            }

            if (execution.TaskArn is null)
            {
                var run = await ecs.RunTaskAsync(new()
                {
                    Cluster = settings.Cluster, TaskDefinition = execution.TaskDefinition, Count = 1, LaunchType = LaunchType.FARGATE,
                    ClientToken = execution.ClientToken, StartedBy = execution.ClientToken,
                    NetworkConfiguration = new() { AwsvpcConfiguration = new() { Subnets = settings.Subnets, SecurityGroups = settings.SecurityGroups, AssignPublicIp = AssignPublicIp.DISABLED } }
                }, cancellationToken);
                if (run.Failures?.Count > 0 || run.Tasks?.Count != 1) throw new InvalidOperationException("ECS could not schedule the validation task.");
                execution = execution with { TaskArn = run.Tasks[0].TaskArn };
                jobs.SaveEcsValidation(upgradeJobId, stage, execution);
            }
            progress?.Invoke(new("playwright-job", UpgradeJobStageStatus.Succeeded, "ECS Playwright task scheduled."));
            while (DateTimeOffset.UtcNow - started < TimeSpan.FromSeconds(Timeout(mode)))
            {
                var task = await TaskAsync(execution, cancellationToken);
                if (task.LastStatus == "STOPPED")
                {
                    var output = "";
                    var logDeadline = DateTimeOffset.UtcNow.AddSeconds(45);
                    do
                    {
                        try { output = await ReadLogsAsync(task, execution.TaskDefinition, cancellationToken); }
                        catch (Amazon.CloudWatchLogs.Model.ResourceNotFoundException) { }
                        if (ReadSummary(output) is not null || results.TryGet(upgradeJobId, mode, out _)) break;
                        await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), cancellationToken);
                    } while (DateTimeOffset.UtcNow < logDeadline);
                    var summary = ReadSummary(output);
                    if (summary is not null) results.Save(upgradeJobId, mode, summary);
                    else results.TryGet(upgradeJobId, mode, out summary);
                    var exit = task.Containers.Single(item => item.Name == settings.RunnerContainer).ExitCode;
                    var passed = exit == 0 && summary is not null && summary.Total > 0 && summary.Passed >= 0 && summary.Failed >= 0 && summary.Skipped >= 0 &&
                        summary.Passed + summary.Failed + summary.Skipped == summary.Total && summary.Mode.Equals(stage, StringComparison.OrdinalIgnoreCase) &&
                        summary.PassPercentage >= summary.Threshold && summary.Threshold is >= 0 and <= 100 && summary.Status.Equals("Passed", StringComparison.OrdinalIgnoreCase);
                    if (mode == PlaywrightValidationMode.Cleanup) passed &= summary?.Failed == 0 && summary.Passed + summary.Skipped == summary.Total;
                    var safeOutput = Redact(output, (await ReadOwnedSecretAsync(execution.SecretArn, upgradeJobId, stage, cancellationToken)).Environment.Values);
                    reports.TrySaveFromLogs(upgradeJobId, mode, safeOutput);
                    operationLogs.Append(upgradeJobId, "Playwright " + stage, passed ? "Info" : "Error", "EcsRunner", safeOutput);
                    var result = new PlaywrightScriptResult(passed, exit, false, started, DateTimeOffset.UtcNow, "ECS RunTask " + stage, safeOutput, "", summary?.ToMessage("Validation completed.") ?? "ECS task stopped without a verified validation result.", true);
                    jobs.SaveEcsValidation(upgradeJobId, stage, execution with { Result = result, Summary = summary });
                    if (mode == PlaywrightValidationMode.Cleanup && passed) await CleanupSharedStateAsync(upgradeJobId, cancellationToken);
                    return result;
                }
                await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), cancellationToken);
            }
            await StopExecutionAsync(execution, CancellationToken.None);
            return Failure("ECS Playwright task timed out.", true);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            if (execution?.TaskArn is not null) await StopExecutionAsync(execution, CancellationToken.None);
            if (execution is null) await CleanupPreparationAsync();
            throw;
        }
        catch
        {
            if (execution?.TaskArn is not null) await StopExecutionAsync(execution, CancellationToken.None);
            if (execution is null) await CleanupPreparationAsync();
            return Failure("ECS Playwright infrastructure failed. Check task events, IAM, networking, runtime Secret access, and runner storage compatibility.", false);
        }
        finally { tokens.Revoke(upgradeJobId, mode); }

        async Task CleanupPreparationAsync()
        {
            // No task can be launched before its definition is durably recorded.
            if (preparedDefinition is not null) await ecs.DeregisterTaskDefinitionAsync(new() { TaskDefinition = preparedDefinition }, CancellationToken.None);
            if (preparedSecret is not null)
            {
                await ReadOwnedSecretAsync(preparedSecret, upgradeJobId, stage, CancellationToken.None);
                await secrets.DeleteSecretAsync(new() { SecretId = preparedSecret, ForceDeleteWithoutRecovery = true }, CancellationToken.None);
            }
        }

        PlaywrightScriptResult Failure(string message, bool timedOut) => new(false, null, timedOut, started, DateTimeOffset.UtcNow, "ECS RunTask " + stage, "", message, message, execution?.TaskArn is not null);
    }

    public Task<PlaywrightScriptResult> RunCleanupAsync(string upgradeJobId, Action<PlaywrightValidationProgress>? progress = null, string? runnerImageVersion = null, CancellationToken cancellationToken = default) =>
        RunAsync(PlaywrightValidationMode.Cleanup, upgradeJobId, progress, runnerImageVersion, cancellationToken);

    public async Task CleanupSharedStateAsync(string upgradeJobId, CancellationToken cancellationToken = default)
    {
        await StopTasksAsync(upgradeJobId, cancellationToken);
        if (!jobs.TryGet(upgradeJobId, out var job)) throw new InvalidOperationException("Unknown ECS cleanup operation.");
        foreach (var (stage, execution) in job.EcsValidationExecutions)
        {
            try
            {
                await ReadOwnedSecretAsync(execution.SecretArn, upgradeJobId, stage, cancellationToken);
                await secrets.DeleteSecretAsync(new() { SecretId = execution.SecretArn, ForceDeleteWithoutRecovery = true }, cancellationToken);
            }
            catch (ResourceNotFoundException) { }
            var definition = await ecs.DescribeTaskDefinitionAsync(new() { TaskDefinition = execution.TaskDefinition, Include = [TaskDefinitionField.TAGS] }, cancellationToken);
            if (definition.Tags?.Any(tag => tag.Key == OwnerTag && tag.Value == Hash(upgradeJobId)) != true)
                throw new InvalidOperationException("ECS runner definition ownership could not be verified.");
            if (definition.TaskDefinition.Status == TaskDefinitionStatus.ACTIVE)
                await ecs.DeregisterTaskDefinitionAsync(new() { TaskDefinition = execution.TaskDefinition }, cancellationToken);
            tokens.Revoke(upgradeJobId, Enum.Parse<PlaywrightValidationMode>(stage, true));
        }
        if (settings.StateBucket is not null)
            await s3.DeleteObjectAsync(new DeleteObjectRequest { BucketName = settings.StateBucket, Key = StateKey(upgradeJobId) }, cancellationToken);
        else
        {
            var directory = StateDirectory(upgradeJobId);
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private async Task StopTasksAsync(string jobId, CancellationToken token)
    {
        if (!jobs.TryGet(jobId, out var job)) throw new InvalidOperationException("Unknown ECS operation.");
        foreach (var execution in job.EcsValidationExecutions.Values) if (execution.TaskArn is not null && execution.Result is null) await StopExecutionAsync(execution, token);
    }

    private async Task StopExecutionAsync(EcsValidationExecution execution, CancellationToken token)
    {
        var task = await TaskAsync(execution, token);
        if (task.LastStatus == "STOPPED") return;
        await ecs.StopTaskAsync(new() { Cluster = settings.Cluster, Task = execution.TaskArn, Reason = "Upgrade Center validation cancellation/cleanup" }, token);
        var deadline = DateTimeOffset.UtcNow.AddSeconds(120);
        while (DateTimeOffset.UtcNow < deadline)
        {
            if ((await TaskAsync(execution, token)).LastStatus == "STOPPED") return;
            await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), token);
        }
        throw new InvalidOperationException("ECS validation did not stop; shared state and Secrets were retained.");
    }

    private async Task<AwsTask> TaskAsync(EcsValidationExecution execution, CancellationToken token)
    {
        var response = await ecs.DescribeTasksAsync(new() { Cluster = settings.Cluster, Tasks = [execution.TaskArn!] }, token);
        var task = response.Tasks?.SingleOrDefault() ?? throw new InvalidOperationException("Persisted ECS validation task is unavailable; automatic rerun is blocked.");
        if (response.Failures?.Count > 0 || task.TaskDefinitionArn != execution.TaskDefinition || task.StartedBy != execution.ClientToken)
            throw new InvalidOperationException("ECS task does not match the persisted validation operation.");
        return task;
    }

    private async Task<(string Arn, Dictionary<string, string> Environment)> ReadOwnedSecretAsync(string id, string jobId, string stage, CancellationToken token)
    {
        var description = await secrets.DescribeSecretAsync(new() { SecretId = id }, token);
        if (description.Name != $"{settings.RuntimeSecretPrefix}/{Hash(jobId)}-{stage}" || description.Tags?.Any(tag => tag.Key == OwnerTag && tag.Value == Hash(jobId)) != true)
            throw new InvalidOperationException("Runtime Secret ownership does not match the operation.");
        var value = await secrets.GetSecretValueAsync(new() { SecretId = description.ARN }, token);
        return (description.ARN, JsonSerializer.Deserialize<Dictionary<string, string>>(value.SecretString) ?? throw new InvalidOperationException("Empty runtime Secret."));
    }

    private async Task<string> ReadLogsAsync(AwsTask task, string definitionArn, CancellationToken token)
    {
        var definition = await ecs.DescribeTaskDefinitionAsync(new() { TaskDefinition = definitionArn }, token);
        var container = definition.TaskDefinition.ContainerDefinitions.Single(item => item.Name == settings.RunnerContainer);
        var options = container.LogConfiguration.Options;
        var stream = $"{options["awslogs-stream-prefix"]}/{settings.RunnerContainer}/{task.TaskArn.Split('/').Last()}";
        string? next = null;
        var output = new StringBuilder();
        do
        {
            var page = await logs.GetLogEventsAsync(new() { LogGroupName = options["awslogs-group"], LogStreamName = stream, StartFromHead = true, NextToken = next }, token);
            foreach (var item in page.Events ?? []) output.AppendLine(item.Message);
            if (page.NextForwardToken == next) break;
            next = page.NextForwardToken;
        } while (next is not null);
        return output.ToString();
    }

    private static PlaywrightValidationResultSummary? ReadSummary(string output)
    {
        var line = output.Split('\n').LastOrDefault(line => line.StartsWith("PLAYWRIGHT_RESULT_JSON ", StringComparison.Ordinal));
        return line is null ? null : JsonSerializer.Deserialize<PlaywrightValidationResultSummary>(line[23..], JsonOptions);
    }

    private static string Redact(string output, IEnumerable<string> values)
    {
        foreach (var value in values.Where(value => value.Length >= 4).OrderByDescending(value => value.Length)) output = output.Replace(value, "***", StringComparison.Ordinal);
        return Regex.Replace(output, @"(?<key>password|pwd|secret|token|authorization|api[_-]?key|connectionstring|X-Amz-[A-Za-z-]+)\s*[:=]\s*[""']?[^,""'\r\n&]+", "${key}=***", RegexOptions.IgnoreCase);
    }
    private string UploadUrl(string job, string mode, string endpoint) => $"{settings.UploadBaseUrl.TrimEnd('/')}/api/{endpoint}/{Uri.EscapeDataString(job)}/{mode}";
    private int Timeout(PlaywrightValidationMode mode) => mode == PlaywrightValidationMode.Cleanup ? playwrightOptions.Value.CleanupJobTimeoutSeconds : playwrightOptions.Value.JobTimeoutSeconds;
    private string StateKey(string jobId) => settings.StatePrefix.TrimEnd('/') + "/" + Hash(jobId) + "/state.json";
    private string StateDirectory(string jobId) => Path.Combine(settings.SharedStateDirectory, Hash(jobId));
    private static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant()[..32];
    private static string Mode(PlaywrightValidationMode mode) => mode.ToString().ToLowerInvariant();
}
