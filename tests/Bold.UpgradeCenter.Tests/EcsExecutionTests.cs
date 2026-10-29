using System.Net;
using System.Text;
using System.Text.Json;
using Amazon.Runtime;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.ApplicationAutoScaling;
using Amazon.ApplicationAutoScaling.Model;
using Bold.UpgradeCenter.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Task = System.Threading.Tasks.Task;

namespace Bold.UpgradeCenter.Tests;

public sealed class EcsExecutionTests
{
    [Fact]
    public void KubernetesRemainsTheDefaultBackend() => Assert.Equal(UpgradeExecutionEnvironment.Kubernetes, UpgradeExecutionEnvironments.Parse(null));

    [Theory]
    [InlineData("Standalone", "Common")]
    [InlineData("IdpIntegrated", "BoldBI")]
    [InlineData("Standalone", "BoldReports")]
    public void UnsupportedEcsCombinationsAreRejected(string integration, string product)
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        { ["UpgradeCenter:IntegrationMode"] = integration, ["UpgradeCenter:DeploymentMode"] = product }).Build();
        Assert.Throws<InvalidOperationException>(() => Settings().Validate(config));
    }

    [Fact]
    public async Task MissingRequiredMappingsDoNotPassValidation()
    {
        using var client = new Ecs(); using var autoscaling = new Scaling();
        var settings = Settings(); settings.Services.Remove("id-ums-deployment");
        var validation = await Service(client, autoscaling, settings).ValidateNamespaceAsync();
        Assert.False(validation.Succeeded);
        Assert.Contains("id-ums-deployment", validation.MissingRequiredDeployments);
        Assert.Empty(client.Updates);
    }

    [Fact]
    public async Task UpgradePersistsEveryRevisionBeforeStoppingAnyServiceAndPreservesSettings()
    {
        using var client = new Ecs(); using var autoscaling = new Scaling();
        var service = Service(client, autoscaling);
        KubernetesUpgradeResult? persisted = null;
        var result = await service.UpgradeAsync("17.1.30", KubernetesUpgradeScope.Bulk, null, rollbackContextPrepared: context =>
        {
            Assert.Empty(client.Updates);
            Assert.Equal(7, context.Ecs!.Services.Count);
            persisted = JsonSerializer.Deserialize<KubernetesUpgradeResult>(JsonSerializer.Serialize(context));
            return Task.CompletedTask;
        });
        Assert.True(result.Succeeded, result.Message);
        Assert.NotNull(persisted?.Ecs);
        Assert.Equal(7, client.Updates.TakeWhile(update => update.DesiredCount == 0).Count());
        Assert.Equal("id-ums-deployment", client.Updates.First(update => update.TaskDefinition is not null).Service);
        Assert.All(client.Registered, request =>
        {
            Assert.Equal("awsvpc", request.NetworkMode.Value);
            Assert.Equal("execution-role", request.ExecutionRoleArn);
            Assert.Equal("task-role", request.TaskRoleArn);
            Assert.Equal("preserved", request.ContainerDefinitions[0].Environment[0].Value);
            Assert.Single(request.Volumes);
        });
        Assert.Contains(autoscaling.Requests, request => request.SuspendedState.DynamicScalingOutSuspended == true);
        Assert.Contains(autoscaling.Requests, request => request.SuspendedState.DynamicScalingOutSuspended == false);
    }

    [Fact]
    public async Task RollbackStopsServicesAndRestoresRecordedArnsInsteadOfLatestFamilyRevisions()
    {
        using var client = new Ecs(); using var autoscaling = new Scaling();
        var service = Service(client, autoscaling);
        var result = await service.UpgradeAsync("17.1.30", KubernetesUpgradeScope.Bulk, null, rollbackContextPrepared: _ => Task.CompletedTask);
        client.Updates.Clear();
        await service.PrepareDatabaseRestoreAsync(result, default);
        Assert.All(client.Services.Values, item => Assert.Equal(0, item.RunningCount));
        var restored = await service.RollbackAsync(result);
        Assert.True(restored.Succeeded, restored.Message);
        foreach (var snapshot in result.Ecs!.Services) Assert.Equal(snapshot.PreviousTaskDefinition, client.Services[snapshot.Service].TaskDefinition);
    }

    [Fact]
    public async Task PersistenceFailureCausesNoServiceMutation()
    {
        using var client = new Ecs(); using var autoscaling = new Scaling();
        var result = await Service(client, autoscaling).UpgradeAsync("17.1.30", KubernetesUpgradeScope.Bulk, null,
            rollbackContextPrepared: _ => throw new IOException("database context save failed"));
        Assert.False(result.Succeeded);
        Assert.Empty(client.Updates);
        Assert.Empty(result.PreviousImages);
    }

    [Theory]
    [InlineData("Kubernetes", "k8s")]
    [InlineData("ECS", "k8s")]
    public async Task BothEnvironmentsUseTheExistingKubernetesRunnerCatalog(string environment, string suffix)
    {
        var requests = new List<string>();
        using var client = new HttpClient(new RunnerMetadata(requests, suffix, exact: true));
        var provider = new ReleasePlaywrightRunnerImageProvider(client, Options.Create(new UpgradeCenterOptions { ExecutionEnvironment = environment, BiReleaseVersionsApiUrl = "https://releases.example/version/json" }),
            new UpgradeProductContext(), NullLogger<ReleasePlaywrightRunnerImageProvider>.Instance);
        Assert.Equal("docker.io/syncfusion/runner:exact", await provider.GetRunnerImageAsync("17.1.31"));
        Assert.Single(requests);
        Assert.EndsWith("/17.1.31/" + suffix, requests[0]);
    }

    [Fact]
    public async Task EcsRunnerFallbackStaysInTheSameFamilyAndSharedKubernetesCatalog()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new RunnerMetadata(requests, "k8s", exact: false));
        var provider = new ReleasePlaywrightRunnerImageProvider(client, Options.Create(new UpgradeCenterOptions { ExecutionEnvironment = "ECS", BiReleaseVersionsApiUrl = "https://releases.example/version/json" }),
            new UpgradeProductContext(), NullLogger<ReleasePlaywrightRunnerImageProvider>.Instance);
        Assert.Equal("docker.io/syncfusion/runner:family", await provider.GetRunnerImageAsync("17.1.31"));
        Assert.Contains(requests, request => request.EndsWith("/17.1.30/k8s"));
        Assert.DoesNotContain(requests, request => request.Contains("/ecs") || request.Contains("18.1.1") || request.Contains("16.3.5"));
    }

    [Fact]
    public async Task ExternalDeploymentIsNotOverwrittenDuringRecovery()
    {
        using var client = new Ecs(); using var autoscaling = new Scaling();
        var service = Service(client, autoscaling);
        var result = await service.UpgradeAsync("17.1.30", KubernetesUpgradeScope.Bulk, null, rollbackContextPrepared: _ => Task.CompletedTask);
        client.Updates.Clear();
        client.Services.Values.First().TaskDefinition = "external-revision";
        var recovered = await service.ReconcileUpgradeRolloutAsync(result);
        Assert.False(recovered.Succeeded);
        Assert.Empty(client.Updates);
    }

    [Fact]
    public async Task UnapprovedImageCannotStopServices()
    {
        using var client = new Ecs(); using var autoscaling = new Scaling();
        var images = Settings().Services.Keys.Select(name => new CustomPatchImageMapping(name, "evil.example/unapproved/product:1")).ToArray();
        var result = await Service(client, autoscaling).UpgradeWithCustomImagesAsync("patch", images, rollbackContextPrepared: _ => Task.CompletedTask);
        Assert.False(result.Succeeded);
        Assert.Empty(client.Updates);
    }

    [Fact]
    public async Task OptionalUnmappedServicesAreNotCreated()
    {
        using var client = new Ecs(); using var autoscaling = new Scaling();
        var result = await Service(client, autoscaling).UpgradeWithCustomImagesAsync("patch", Settings().Services.Keys
            .Append("bold-mcp-deployment").Select(name => new CustomPatchImageMapping(name, "docker.io/syncfusion/product:17.1.30")).ToArray(), rollbackContextPrepared: _ => Task.CompletedTask);
        Assert.True(result.Succeeded, result.Message);
        Assert.DoesNotContain(client.Updates, item => item.Service == "bold-mcp-deployment");
    }

    [Fact]
    public async Task LegacyKubernetesRecoveryContextCannotBeUsedForEcsRestore()
    {
        using var client = new Ecs(); using var autoscaling = new Scaling();
        var legacy = KubernetesUpgradeResult.CreateSkipped("legacy");
        await Assert.ThrowsAsync<InvalidOperationException>(() => Service(client, autoscaling).PrepareDatabaseRestoreAsync(legacy, default));
        Assert.Empty(client.Updates);
    }

    private static EcsExecutionOptions Settings() => new()
    {
        Region = "us-east-1", Cluster = "cluster", RunnerTaskDefinition = "runner:1", Subnets = ["subnet"], SecurityGroups = ["sg"],
        UploadBaseUrl = "https://example/upgrade-center", MaintenanceWindowConfirmed = true, PollSeconds = 1,
        Services = UpgradeProductDefinitions.BoldBi.ExpectedDeployments.Except(UpgradeProductDefinitions.BoldBi.OptionalDeployments)
            .ToDictionary(name => name, name => new EcsServiceMapping { Service = name, Container = "main" })
    };

    private static EcsUpgradeService Service(Ecs client, Scaling scaling, EcsExecutionOptions? settings = null) => new(client, scaling,
        new HttpClient(new Metadata()), Options.Create(settings ?? Settings()), Options.Create(new UpgradeCenterOptions { BiReleaseVersionsApiUrl = "https://releases.example/version/json" }),
        Options.Create(new KubernetesUpgradeOptions { AllowedImageRegistryHosts = ["docker.io"], AllowedImageRepositories = ["syncfusion"] }));

    private sealed class Metadata : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.EndsWith("/17.1.30/k8s", request.RequestUri!.AbsolutePath);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(new
            {
                version = "17.1.30", deployments = Settings().Services.Keys.Select(name => new { name, imageTag = "docker.io/syncfusion/product:17.1.30" })
            }), Encoding.UTF8, "application/json") });
        }
    }

    private sealed class RunnerMetadata(List<string> requests, string suffix, bool exact) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            requests.Add(path);
            var data = path.EndsWith("/version/json") ? "{\"versions\":[{\"version\":\"18.1.1\"},{\"version\":\"17.1.30\"},{\"version\":\"16.3.5\"}]}" :
                path.EndsWith("/17.1.31/" + suffix) && exact ? "{\"version\":\"17.1.31\",\"playwrightRunnerImage\":\"docker.io/syncfusion/runner:exact\"}" :
                path.EndsWith("/17.1.30/" + suffix) ? "{\"version\":\"17.1.30\",\"playwrightRunnerImage\":\"docker.io/syncfusion/runner:family\"}" : "{}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(data, Encoding.UTF8, "application/json") });
        }
    }

    private sealed class Ecs() : AmazonECSClient(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1)
    {
        public Dictionary<string, Amazon.ECS.Model.Service> Services { get; } = Settings().Services.Keys.ToDictionary(name => name, name => new Amazon.ECS.Model.Service
        {
            ServiceName = name, Status = "ACTIVE", DeploymentController = new() { Type = DeploymentControllerType.ECS },
            TaskDefinition = name + ":1", DesiredCount = 1, RunningCount = 1, PendingCount = 0,
            Deployments = [new() { TaskDefinition = name + ":1", RolloutState = DeploymentRolloutState.COMPLETED }]
        });
        public List<UpdateServiceRequest> Updates { get; } = [];
        public List<RegisterTaskDefinitionRequest> Registered { get; } = [];
        public override Task<DescribeServicesResponse> DescribeServicesAsync(DescribeServicesRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DescribeServicesResponse { Services = request.Services.Select(name => Services[name]).ToList(), Failures = [] });
        public override Task<DescribeTaskDefinitionResponse> DescribeTaskDefinitionAsync(DescribeTaskDefinitionRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DescribeTaskDefinitionResponse
            {
                TaskDefinition = new() { TaskDefinitionArn = request.TaskDefinition, Family = request.TaskDefinition.Split(':')[0], NetworkMode = NetworkMode.Awsvpc,
                    ExecutionRoleArn = "execution-role", TaskRoleArn = "task-role", RequiresCompatibilities = [Compatibility.FARGATE], Cpu = "512", Memory = "1024",
                    ContainerDefinitions = [new() { Name = "main", Image = "syncfusion/old:16.3.5", Environment = [new() { Name = "keep", Value = "preserved" }] }],
                    Volumes = [new() { Name = "product", EfsVolumeConfiguration = new() { FileSystemId = "fs-product", TransitEncryption = EFSTransitEncryption.ENABLED } }] }, Tags = []
            });
        public override Task<RegisterTaskDefinitionResponse> RegisterTaskDefinitionAsync(RegisterTaskDefinitionRequest request, CancellationToken cancellationToken = default)
        {
            Registered.Add(request);
            return Task.FromResult(new RegisterTaskDefinitionResponse { TaskDefinition = new() { TaskDefinitionArn = request.Family + ":2" } });
        }
        public override Task<UpdateServiceResponse> UpdateServiceAsync(UpdateServiceRequest request, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested(); Updates.Add(request);
            var service = Services[request.Service];
            service.DesiredCount = service.RunningCount = request.DesiredCount ?? service.DesiredCount;
            if (request.TaskDefinition is not null) service.TaskDefinition = request.TaskDefinition;
            service.Deployments = [new() { TaskDefinition = service.TaskDefinition, RolloutState = DeploymentRolloutState.COMPLETED }];
            return Task.FromResult(new UpdateServiceResponse { Service = service });
        }
    }

    private sealed class Scaling() : AmazonApplicationAutoScalingClient(new AnonymousAWSCredentials(), Amazon.RegionEndpoint.USEast1)
    {
        public List<RegisterScalableTargetRequest> Requests { get; } = [];
        public override Task<DescribeScalableTargetsResponse> DescribeScalableTargetsAsync(DescribeScalableTargetsRequest request, CancellationToken cancellationToken = default) =>
            Task.FromResult(new DescribeScalableTargetsResponse { ScalableTargets = [new() { ResourceId = request.ResourceIds[0], MinCapacity = 1, MaxCapacity = 20, SuspendedState = new() } ] });
        public override Task<RegisterScalableTargetResponse> RegisterScalableTargetAsync(RegisterScalableTargetRequest request, CancellationToken cancellationToken = default)
        { Requests.Add(request); return Task.FromResult(new RegisterScalableTargetResponse()); }
    }
}
