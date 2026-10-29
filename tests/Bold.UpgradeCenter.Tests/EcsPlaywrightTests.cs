using System.Text.Json;
using Amazon.Runtime;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.SecretsManager;
using Amazon.SecretsManager.Model;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.S3;
using Amazon.S3.Model;
using Bold.UpgradeCenter.Services;
using Microsoft.Extensions.Options;
using Xunit;
using Task = System.Threading.Tasks.Task;
using SecretTag = Amazon.SecretsManager.Model.Tag;

namespace Bold.UpgradeCenter.Tests;

public sealed class EcsPlaywrightTests
{
    [Fact]
    public async Task UsesVersionSpecificImagesAndNeverPassesCredentialsInTaskOverrides()
    {
        using var fixture = new Fixture();
        Assert.True((await fixture.Runner.RunAsync(PlaywrightValidationMode.Pre, fixture.Job.Id, runnerImageVersion: "16.3.5")).Succeeded);
        Assert.True((await fixture.Runner.RunAsync(PlaywrightValidationMode.Post, fixture.Job.Id, runnerImageVersion: "17.1.30")).Succeeded);
        Assert.Equal(new[] { "16.3.5", "17.1.30" }, fixture.Images.Versions);
        Assert.All(fixture.Ecs.Runs, request => { Assert.Null(request.Overrides); Assert.Equal(AssignPublicIp.DISABLED, request.NetworkConfiguration.AwsvpcConfiguration.AssignPublicIp); });
        Assert.All(fixture.Ecs.Registered, request =>
        {
            Assert.Null(request.ContainerDefinitions[0].EntryPoint);
            Assert.Single(request.ContainerDefinitions[0].Command);
            Assert.DoesNotContain(request.ContainerDefinitions[0].Environment, item => item.Name.Contains("PASSWORD"));
            Assert.Contains(request.ContainerDefinitions[0].Secrets, item => item.Name == "BOLDBI_DB_PASSWORD" && item.ValueFrom.Contains(":BOLDBI_DB_PASSWORD::"));
            Assert.Contains(request.ContainerDefinitions[0].Secrets, item => item.Name == "CUSTOM_TEST_CREDENTIAL" && item.ValueFrom == "customer-secret");
        });
        Assert.DoesNotContain("test-password", string.Join('\n', fixture.Log.Messages));
    }

    [Fact]
    public async Task RestartUsesPersistedTaskAndDoesNotCreateAnotherTask()
    {
        using var fixture = new Fixture();
        var first = await fixture.Runner.RunAsync(PlaywrightValidationMode.Pre, fixture.Job.Id, runnerImageVersion: "16.3.5");
        var second = await fixture.Runner.RunAsync(PlaywrightValidationMode.Pre, fixture.Job.Id, runnerImageVersion: "16.3.5");
        Assert.True(first.Succeeded); Assert.True(second.Succeeded);
        Assert.Single(fixture.Ecs.Runs);
        Assert.Single(fixture.Secret.Values);
        Assert.True(fixture.Jobs.TryGet(fixture.Job.Id, out var persisted));
        var copy = JsonSerializer.Deserialize<UpgradeJob>(JsonSerializer.Serialize(persisted))!;
        Assert.NotNull(copy.EcsValidationExecutions["pre"].TaskArn);
        Assert.NotNull(copy.EcsValidationExecutions["pre"].Result);
    }

    [Fact]
    public async Task RestartBeforeCompletionReusesExistingSecretTokenAndTask()
    {
        using var fixture = new Fixture();
        await fixture.Runner.RunAsync(PlaywrightValidationMode.Pre, fixture.Job.Id, runnerImageVersion: "16.3.5");
        fixture.Jobs.TryGet(fixture.Job.Id, out var persisted);
        fixture.Jobs.SaveEcsValidation(fixture.Job.Id, "pre", persisted.EcsValidationExecutions["pre"] with { Result = null, Summary = null });
        Assert.True((await fixture.Runner.RunAsync(PlaywrightValidationMode.Pre, fixture.Job.Id, runnerImageVersion: "16.3.5")).Succeeded);
        Assert.Single(fixture.Ecs.Runs);
        Assert.Single(fixture.Secret.Values);
    }

    [Fact]
    public async Task MissingRunnerMappingDoesNotScheduleATask()
    {
        using var fixture = new Fixture(); fixture.Images.Missing = true;
        Assert.False((await fixture.Runner.RunAsync(PlaywrightValidationMode.Pre, fixture.Job.Id, runnerImageVersion: "missing")).Succeeded);
        Assert.Empty(fixture.Ecs.Runs); Assert.Empty(fixture.Secret.Values);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public async Task ExitZeroAloneAndPassingSummaryWithNonzeroExitDoNotPass(bool summary, int exit)
    {
        using var fixture = new Fixture(); fixture.Ecs.Exit = exit; fixture.Logs.IncludeSummary = summary;
        Assert.False((await fixture.Runner.RunAsync(PlaywrightValidationMode.Pre, fixture.Job.Id, runnerImageVersion: "16.3.5")).Succeeded);
    }

    [Fact]
    public async Task SuccessfulCleanupDeletesOnlyOwnedSecretsAndIsRepeatable()
    {
        using var fixture = new Fixture();
        await fixture.Runner.RunAsync(PlaywrightValidationMode.Pre, fixture.Job.Id, runnerImageVersion: "16.3.5");
        var cleanup = await fixture.Runner.RunCleanupAsync(fixture.Job.Id, runnerImageVersion: "16.3.5");
        Assert.True(cleanup.Succeeded, cleanup.Message);
        Assert.Empty(fixture.Secret.Values);
        Assert.Equal(2, fixture.Ecs.Deregistered.Count);
        Assert.True((await fixture.Runner.RunCleanupAsync(fixture.Job.Id, runnerImageVersion: "16.3.5")).Succeeded);
        Assert.Equal(2, fixture.Ecs.Runs.Count);
    }

    [Fact]
    public async Task CleanupDeletionFailureIsNotReportedAsSuccess()
    {
        using var fixture = new Fixture();
        await fixture.Runner.RunAsync(PlaywrightValidationMode.Pre, fixture.Job.Id, runnerImageVersion: "16.3.5");
        fixture.Secret.RefuseDelete = true;
        Assert.False((await fixture.Runner.RunCleanupAsync(fixture.Job.Id, runnerImageVersion: "16.3.5")).Succeeded);
        Assert.NotEmpty(fixture.Secret.Values);
    }

    [Fact]
    public async Task CancellationBeforeRegistrationRemovesPreparedSecretWithoutLaunchingTask()
    {
        using var fixture = new Fixture();
        using var cancellation = new CancellationTokenSource();
        fixture.Ecs.BeforeDescribe = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Runner.RunAsync(
            PlaywrightValidationMode.Pre, fixture.Job.Id, runnerImageVersion: "16.3.5", cancellationToken: cancellation.Token));
        Assert.Empty(fixture.Secret.Values);
        Assert.Empty(fixture.Ecs.Runs);
    }

    [Fact]
    public async Task S3HandoffUsesSecretReferencesAndCleanupDeletesOnlyTheJobObject()
    {
        using var fixture = new Fixture(useS3: true);
        Assert.True((await fixture.Runner.RunAsync(PlaywrightValidationMode.Pre, fixture.Job.Id, runnerImageVersion: "16.3.5")).Succeeded);
        var environment = JsonSerializer.Deserialize<Dictionary<string, string>>(Assert.Single(fixture.Secret.Values).Value.SecretString)!;
        Assert.Contains("BOLD_PLAYWRIGHT_STATE_GET_URL", environment.Keys);
        Assert.Contains("BOLD_PLAYWRIGHT_STATE_PUT_URL", environment.Keys);
        Assert.DoesNotContain("BOLD_PLAYWRIGHT_SHARED_STATE_DIR", environment.Keys);
        var container = Assert.Single(fixture.Ecs.Registered).ContainerDefinitions[0];
        Assert.Equal("syncfusion/runner:16.3.5", container.Image);
        Assert.Equal(new[] { "node", "-e" }, container.EntryPoint);
        Assert.Equal("pre", container.Command[1]);
        Assert.Contains("runBundledRunner(", container.Command[0]);
        Assert.Contains("/app/docker-entrypoint.js", container.Command[0]);
        Assert.DoesNotContain(environment["BOLD_PLAYWRIGHT_STATE_GET_URL"], container.Command[0]);
        Assert.DoesNotContain("test-password", container.Command[0]);
        Assert.Equal(new[] { HttpVerb.GET, HttpVerb.PUT }, fixture.Storage.Signed.Select(request => request.Verb));
        Assert.All(fixture.Storage.Signed, request => Assert.Equal("state-bucket", request.BucketName));
        Assert.DoesNotContain("signed-test-url", string.Join('\n', fixture.Log.Messages));
        Assert.True((await fixture.Runner.RunCleanupAsync(fixture.Job.Id, runnerImageVersion: "16.3.5")).Succeeded);
        var deleted = Assert.Single(fixture.Storage.Deleted);
        Assert.Equal("state-bucket", deleted.BucketName);
        Assert.Equal(fixture.Storage.Signed[0].Key, deleted.Key);
        Assert.Matches("^app_data/upgrade-center/playwright-state/[a-f0-9]{32}/state.json$", deleted.Key);
    }

    private sealed class Fixture : IDisposable
    {
        public Ecs Ecs { get; } = new(); public Secrets Secret { get; } = new(); public Logs Logs { get; } = new();
        public ImageProvider Images { get; } = new(); public LogStore Log { get; } = new();
        public InMemoryUpgradeJobStore Jobs { get; }
        public UpgradeJob Job { get; }
        public EcsPlaywrightJobService Runner { get; }
        public Storage Storage { get; } = new();
        public Fixture(bool useS3 = false)
        {
            Jobs = new(new States(), Log);
            Job = Jobs.Create("16.3.5", "17.1.30", UpgradeJobType.AvailableRelease, ["bi-web-deployment"]);
            Logs.Ecs = Ecs;
            Runner = new(Ecs, Secret, Logs, Storage, Options.Create(new EcsExecutionOptions { Cluster = "cluster", RunnerTaskDefinition = "runner:1", RunnerContainer = "playwright", StateBucket = useS3 ? "state-bucket" : null,
                SharedStateDirectory = Path.GetTempPath(), UploadBaseUrl = "https://example/upgrade-center", Subnets = ["subnet"], SecurityGroups = ["sg"], PollSeconds = 1 }),
                Options.Create(new PlaywrightExecutionOptions()), new Runtime(), Images, Jobs, new InMemoryPlaywrightReportUploadTokenStore(), new InMemoryPlaywrightValidationResultStore(), new Reports(), Log);
        }
        public void Dispose() { Ecs.Dispose(); Secret.Dispose(); Logs.Dispose(); Storage.Dispose(); }
    }

    private sealed class Runtime : IPlaywrightRuntimeEnvironmentProvider
    {
        public Task<IReadOnlyDictionary<string, string>> ResolveEnvironmentAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyDictionary<string, string>>(new Dictionary<string, string> { ["BOLDBI_DB_PASSWORD"] = "test-password", ["BOLDBI_BASE_URL"] = "https://example" });
    }
    private sealed class ImageProvider : IPlaywrightRunnerImageProvider
    {
        public List<string> Versions { get; } = []; public bool Missing { get; set; }
        public Task<string?> GetRunnerImageAsync(string selectedVersion, CancellationToken cancellationToken = default)
        { Versions.Add(selectedVersion); return Task.FromResult<string?>(Missing ? null : "syncfusion/runner:" + selectedVersion); }
    }
    private sealed class Ecs() : AmazonECSClient(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1)
    {
        public int Exit { get; set; }
        public Action? BeforeDescribe { get; set; }
        public List<RunTaskRequest> Runs { get; } = []; public List<RegisterTaskDefinitionRequest> Registered { get; } = []; public List<string> Deregistered { get; } = [];
        public string Stage => Registered.Last().ContainerDefinitions[0].Command.Last();
        public override Task<DescribeTaskDefinitionResponse> DescribeTaskDefinitionAsync(DescribeTaskDefinitionRequest request, CancellationToken cancellationToken = default)
        {
            BeforeDescribe?.Invoke();
            cancellationToken.ThrowIfCancellationRequested();
            var registered = Registered.FindIndex(item => "runner:" + (Registered.IndexOf(item) + 2) == request.TaskDefinition);
            return Task.FromResult(new DescribeTaskDefinitionResponse { TaskDefinition = new() { Family = "runner", NetworkMode = NetworkMode.Awsvpc, RequiresCompatibilities = [Compatibility.FARGATE],
                Status = Deregistered.Contains(request.TaskDefinition) ? TaskDefinitionStatus.INACTIVE : TaskDefinitionStatus.ACTIVE,
                ContainerDefinitions = [new() { Name = "playwright", LogConfiguration = new() { LogDriver = LogDriver.Awslogs, Options = new() { ["awslogs-group"] = "group", ["awslogs-stream-prefix"] = "ecs" } }, Environment = [], Secrets = [new() { Name = "CUSTOM_TEST_CREDENTIAL", ValueFrom = "customer-secret" }] }] },
                Tags = registered < 0 ? [] : Registered[registered].Tags });
        }
        public override Task<RegisterTaskDefinitionResponse> RegisterTaskDefinitionAsync(RegisterTaskDefinitionRequest request, CancellationToken cancellationToken = default)
        { Registered.Add(request); return Task.FromResult(new RegisterTaskDefinitionResponse { TaskDefinition = new() { TaskDefinitionArn = "runner:" + (Registered.Count + 1) } }); }
        public override Task<RunTaskResponse> RunTaskAsync(RunTaskRequest request, CancellationToken cancellationToken = default)
        { Runs.Add(request); return Task.FromResult(new RunTaskResponse { Tasks = [new() { TaskArn = "task/" + Runs.Count }] }); }
        public override Task<DescribeTasksResponse> DescribeTasksAsync(DescribeTasksRequest request, CancellationToken cancellationToken = default)
        {
            var id = int.Parse(request.Tasks[0].Split('/').Last()); var run = Runs[id - 1];
            return Task.FromResult(new DescribeTasksResponse { Tasks = [new() { TaskArn = request.Tasks[0], TaskDefinitionArn = run.TaskDefinition, StartedBy = run.StartedBy,
                LastStatus = "STOPPED", Containers = [new() { Name = "playwright", ExitCode = Exit }] }] });
        }
        public override Task<DeregisterTaskDefinitionResponse> DeregisterTaskDefinitionAsync(DeregisterTaskDefinitionRequest request, CancellationToken cancellationToken = default)
        { if (!Deregistered.Contains(request.TaskDefinition)) Deregistered.Add(request.TaskDefinition); return Task.FromResult(new DeregisterTaskDefinitionResponse()); }
    }
    private sealed class Secrets() : AmazonSecretsManagerClient(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1)
    {
        public Dictionary<string, CreateSecretRequest> Values { get; } = []; public bool RefuseDelete { get; set; }
        public override Task<DescribeSecretResponse> DescribeSecretAsync(DescribeSecretRequest request, CancellationToken cancellationToken = default)
        {
            var key = request.SecretId.Replace("arn-secret/", "");
            if (!Values.TryGetValue(key, out var value)) throw new Amazon.SecretsManager.Model.ResourceNotFoundException("missing");
            return Task.FromResult(new DescribeSecretResponse { ARN = "arn-secret/" + key, Name = key, Tags = value.Tags });
        }
        public override Task<CreateSecretResponse> CreateSecretAsync(CreateSecretRequest request, CancellationToken cancellationToken = default)
        { Values[request.Name] = request; return Task.FromResult(new CreateSecretResponse { ARN = "arn-secret/" + request.Name }); }
        public override Task<GetSecretValueResponse> GetSecretValueAsync(GetSecretValueRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetSecretValueResponse { SecretString = Values[request.SecretId.Replace("arn-secret/", "")].SecretString });
        public override Task<DeleteSecretResponse> DeleteSecretAsync(DeleteSecretRequest request, CancellationToken cancellationToken = default)
        { if (RefuseDelete) throw new IOException("permission denied"); Values.Remove(request.SecretId.Replace("arn-secret/", "")); return Task.FromResult(new DeleteSecretResponse()); }
    }
    private sealed class Logs() : AmazonCloudWatchLogsClient(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1)
    {
        public Ecs Ecs { get; set; } = null!; public bool IncludeSummary { get; set; } = true;
        public override Task<GetLogEventsResponse> GetLogEventsAsync(GetLogEventsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new GetLogEventsResponse { NextForwardToken = "done", Events = request.NextToken is not null ? [] : [new() { Message = "password=test-password" }, new() { Message = IncludeSummary ?
                "PLAYWRIGHT_RESULT_JSON " + JsonSerializer.Serialize(new { mode = Ecs.Stage, total = 2, passed = 2, failed = 0, skipped = 0, passPercentage = 100, threshold = 100, status = "Passed" }) : "No result" }] });
    }
    private sealed class Storage() : AmazonS3Client(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1), IAmazonS3
    {
        public List<GetPreSignedUrlRequest> Signed { get; } = [];
        public List<DeleteObjectRequest> Deleted { get; } = [];
        Task<string> IAmazonS3.GetPreSignedURLAsync(GetPreSignedUrlRequest request)
        { Signed.Add(request); return Task.FromResult("https://example/signed-test-url?verb=" + request.Verb); }
        public override Task<DeleteObjectResponse> DeleteObjectAsync(DeleteObjectRequest request, CancellationToken cancellationToken = default)
        { Deleted.Add(request); return Task.FromResult(new DeleteObjectResponse()); }
    }
    private sealed class Reports : IPlaywrightReportStore
    {
        public bool TrySaveFromLogs(string jobId, PlaywrightValidationMode mode, string logs) => false;
        public bool TrySaveHtml(string jobId, PlaywrightValidationMode mode, Stream htmlStream, out string? safeError) { safeError = null; return true; }
        public bool Exists(string jobId, PlaywrightValidationMode mode) => false;
        public bool TryOpenRead(string jobId, PlaywrightValidationMode mode, out Stream? stream) { stream = null; return false; }
        public bool TryOpenRead(string jobId, PlaywrightValidationMode mode, string? relativePath, out Stream? stream, out string contentType) { stream = null; contentType = "text/html"; return false; }
    }
    private sealed class LogStore : IUpgradeOperationLogStore
    {
        public List<string> Messages { get; } = [];
        public void Append(string jobId, string? stage, string level, string source, string message) => Messages.Add(message);
        public IReadOnlyList<UpgradeOperationLogEntry> List(string jobId, int take = 500) => [];
        public IReadOnlyList<UpgradeOperationLogEntry> ListAfter(string jobId, long afterSequence, int take = 200) => [];
        public IReadOnlyList<UpgradeOperationLogEntry> ListAll(string jobId) => [];
        public void RetainLatestJobs(int jobCount) { }
    }
    private sealed class States : IUpgradeOperationalStateStore
    {
        private readonly Dictionary<string, UpgradeJob> jobs = [];
        public void SaveJob(UpgradeJob job) => jobs[job.Id] = JsonSerializer.Deserialize<UpgradeJob>(JsonSerializer.Serialize(job))!;
        public bool TryGetJob(string jobId, out UpgradeJob job) => jobs.TryGetValue(jobId, out job!);
        public bool TryGetLatestActiveJob(out UpgradeJob job) { job = null!; return false; }
        public bool TryGetLatestJob(out UpgradeJob job) { job = null!; return false; }
        public void SaveRollback(UpgradeRollbackEntry entry) { }
        public bool TryGetRollback(string id, out UpgradeRollbackEntry entry) { entry = null!; return false; }
        public UpgradeRollbackEntry? GetLatestRollback() => null;
    }
}
