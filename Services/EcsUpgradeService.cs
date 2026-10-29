using System.Reflection;
using System.Text.Json;
using Amazon.ECS;
using Amazon.ECS.Model;
using Amazon.ApplicationAutoScaling;
using Amazon.ApplicationAutoScaling.Model;
using Microsoft.Extensions.Options;
using EcsService = Amazon.ECS.Model.Service;
using Task = System.Threading.Tasks.Task;

namespace Bold.UpgradeCenter.Services;

// Adapts the existing persisted workflow contracts; Kubernetes registration is unchanged.
public sealed class EcsUpgradeService(
    IAmazonECS ecs,
    IAmazonApplicationAutoScaling scaling,
    HttpClient releases,
    IOptions<EcsExecutionOptions> options,
    IOptions<UpgradeCenterOptions> upgradeOptions,
    IOptions<KubernetesUpgradeOptions> imageOptions) : IKubernetesUpgradeService, IUpgradeExecutionSafety
{
    private readonly EcsExecutionOptions settings = options.Value;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public async Task ValidateBeforeBackupAsync(UpgradeJob job, CancellationToken cancellationToken)
    {
        if (!settings.MaintenanceWindowConfirmed)
            throw new InvalidOperationException("An operator must block customer writes and confirm the ECS maintenance window before an upgrade. Upgrade Center does not install ALB maintenance rules automatically.");
        var validation = await ValidateNamespaceAsync(cancellationToken);
        if (!validation.Succeeded) throw new InvalidOperationException(validation.Message);
        var targetImages = job.UpgradeType == UpgradeJobType.CustomVersionOrPatch ? job.CustomImages
            : (await GetReleaseImagesAsync(job.TargetVersion, cancellationToken)).Values.Select(item => new CustomPatchImageMapping(item.DeploymentName, item.Image)).ToArray();
        if (targetImages.Count == 0 || targetImages.Any(item => !UpgradeProductDefinitions.BoldBi.ExpectedDeployments.Contains(item.DeploymentName)) ||
            targetImages.Select(item => item.DeploymentName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != targetImages.Count ||
            validation.RequiredDeployments.Any(name => !targetImages.Any(item => item.DeploymentName == name)))
            throw new InvalidOperationException("ECS image mappings must cover required components exactly once.");
        foreach (var image in targetImages) ValidateImage(image.Image);
        var runner = await DefinitionAsync(settings.RunnerTaskDefinition, cancellationToken);
        if (runner.TaskDefinition.NetworkMode != NetworkMode.Awsvpc || runner.TaskDefinition.RequiresCompatibilities?.Contains(Compatibility.FARGATE) != true ||
            !runner.TaskDefinition.ContainerDefinitions.Any(item => item.Name == settings.RunnerContainer && item.LogConfiguration?.LogDriver == LogDriver.Awslogs && (item.EntryPoint?.Count ?? 0) == 0))
            throw new InvalidOperationException("ECS validation runner requires Fargate, awsvpc, awslogs, and no task-level entrypoint override of its selected image.");
    }

    public async Task<KubernetesNamespaceValidationResult> ValidateNamespaceAsync(CancellationToken cancellationToken = default)
    {
        var product = UpgradeProductDefinitions.BoldBi;
        var statuses = new List<KubernetesDeploymentDiscoveryItem>();
        foreach (var component in product.ExpectedDeployments)
        {
            var required = !product.OptionalDeployments.Contains(component);
            if (!settings.Services.TryGetValue(component, out var mapping))
            {
                statuses.Add(new(component, true, false, required, "Missing", "No approved ECS service mapping.", []));
                continue;
            }
            try
            {
                var service = await ServiceAsync(mapping.Service, cancellationToken);
                var definition = await DefinitionAsync(service.TaskDefinition, cancellationToken);
                var container = definition.TaskDefinition.ContainerDefinitions.Single(item => item.Name == mapping.Container);
                statuses.Add(new(component, true, true, required, service.RunningCount > 0 ? "Running" : "Stopped", null, [new(mapping.Container, container.Image)]));
            }
            catch (OperationCanceledException) { throw; }
            catch { statuses.Add(new(component, true, false, required, "Unavailable", "ECS service/container could not be validated. Check mapping, controller, and IAM permissions.", [])); }
        }
        var missing = statuses.Where(item => !item.IsAvailable).Select(item => item.DeploymentName).ToArray();
        var missingRequired = statuses.Where(item => item.IsRequired && !item.IsAvailable).Select(item => item.DeploymentName).ToArray();
        var available = statuses.Where(item => item.IsAvailable).Select(item => item.DeploymentName).ToArray();
        return new(missingRequired.Length == 0, settings.Cluster, missingRequired.Length == 0 ? "ECS service mappings validated." : "Required ECS services are unavailable.",
            product.ExpectedDeployments, available, available, missing, statuses.Where(item => item.IsRequired).Select(item => item.DeploymentName).ToArray(), product.OptionalDeployments,
            missingRequired, missing.Except(missingRequired).ToArray(), statuses);
    }

    public async Task<IReadOnlyDictionary<string, KubernetesDeploymentImageInfo>> GetReleaseImagesAsync(string selectedVersion, CancellationToken cancellationToken = default)
    {
        var root = upgradeOptions.Value.BiReleaseVersionsApiUrl.TrimEnd('/');
        var metadata = await releases.GetFromJsonAsync<ReleaseImages>($"{root}/{Uri.EscapeDataString(selectedVersion)}/k8s", JsonOptions, cancellationToken)
            ?? throw new InvalidOperationException("Release image metadata is missing.");
        if (metadata.Version != selectedVersion || metadata.Deployments.Count == 0)
            throw new InvalidOperationException("Release image metadata does not match the requested version.");
        return metadata.Deployments.ToDictionary(item => item.Name, item => new KubernetesDeploymentImageInfo(item.Name, item.ImageTag), StringComparer.OrdinalIgnoreCase);
    }

    public async Task<KubernetesUpgradeResult> UpgradeAsync(string selectedVersion, KubernetesUpgradeScope scope, string? deploymentName, CancellationToken cancellationToken = default,
        Action<KubernetesDeploymentProgress>? progress = null, Func<KubernetesUpgradeResult, Task>? rollbackContextPrepared = null)
    {
        var images = await GetReleaseImagesAsync(selectedVersion, cancellationToken);
        return await UpgradeImagesAsync(images.Values.Where(item => scope == KubernetesUpgradeScope.Bulk || item.DeploymentName == deploymentName)
            .Select(item => new CustomPatchImageMapping(item.DeploymentName, item.Image)).ToArray(), scope, deploymentName, rollbackContextPrepared, progress, cancellationToken);
    }

    public Task<KubernetesUpgradeResult> UpgradeWithCustomImagesAsync(string selectedVersion, IReadOnlyList<CustomPatchImageMapping> images, CancellationToken cancellationToken = default,
        Action<KubernetesDeploymentProgress>? progress = null, Func<KubernetesUpgradeResult, Task>? rollbackContextPrepared = null) =>
        UpgradeImagesAsync(images, KubernetesUpgradeScope.Bulk, null, rollbackContextPrepared, progress, cancellationToken);

    private async Task<KubernetesUpgradeResult> UpgradeImagesAsync(IReadOnlyList<CustomPatchImageMapping> images, KubernetesUpgradeScope scope, string? selected,
        Func<KubernetesUpgradeResult, Task>? persist, Action<KubernetesDeploymentProgress>? progress, CancellationToken token)
    {
        var validation = await ValidateNamespaceAsync(token);
        if (!validation.Succeeded) return KubernetesUpgradeResult.Failed(validation.Message, scope, selected, validation);
        if (images.Select(item => item.DeploymentName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != images.Count)
            return KubernetesUpgradeResult.Failed("Duplicate ECS component image mappings are not allowed.", scope, selected, validation);
        var required = validation.RequiredDeployments.Where(name => scope == KubernetesUpgradeScope.Bulk || name == selected);
        if (required.Any(name => !images.Any(image => image.DeploymentName == name)))
            return KubernetesUpgradeResult.Failed("ECS metadata is missing required component images.", scope, selected, validation);
        if (persist is null) throw new InvalidOperationException("ECS upgrades require durable rollback context before service changes.");
        var revisions = new List<EcsServiceRevision>();
        var snapshots = new List<KubernetesDeploymentImageSnapshot>();
        var results = new List<KubernetesDeploymentUpgradeResult>();
        var prepared = new KubernetesUpgradeResult(false, false, "ECS upgrade prepared.", settings.Cluster, scope, selected, results, snapshots, validation);
        var contextSaved = false;
        try
        {
            foreach (var image in images)
            {
                if (!UpgradeProductDefinitions.BoldBi.ExpectedDeployments.Contains(image.DeploymentName)) throw new InvalidOperationException("Unapproved ECS component.");
                if (!settings.Services.TryGetValue(image.DeploymentName, out var mapping))
                {
                    if (UpgradeProductDefinitions.BoldBi.OptionalDeployments.Contains(image.DeploymentName)) continue;
                    throw new InvalidOperationException("Required ECS component has no service mapping.");
                }
                ValidateImage(image.Image);
                if (image.ContainerName is not null && image.ContainerName != mapping.Container) throw new InvalidOperationException("Custom patch container differs from the approved mapping.");
                var service = await ServiceAsync(mapping.Service, token);
                if (service.DesiredCount < 1 || service.Deployments?.Count != 1) throw new InvalidOperationException("ECS services must be running and have no concurrent deployment.");
                var definition = await DefinitionAsync(service.TaskDefinition, token);
                var container = definition.TaskDefinition.ContainerDefinitions.Single(item => item.Name == mapping.Container);
                snapshots.Add(new(image.DeploymentName, mapping.Container, container.Image));
                container.Image = image.Image;
                var request = CopyDefinition(definition);
                var target = await ecs.RegisterTaskDefinitionAsync(request, token);
                var scalingState = await ReadScalingAsync(mapping.Service, token);
                revisions.Add(new(image.DeploymentName, mapping.Service, mapping.Container, service.TaskDefinition, target.TaskDefinition.TaskDefinitionArn, service.DesiredCount ?? 0, scalingState));
                results.Add(new(true, image.DeploymentName, mapping.Container, snapshots[^1].Image, image.Image, "Task definition registered; rollout pending."));
            }
            if (revisions.Count == 0) throw new InvalidOperationException("No ECS component matched the upgrade plan.");
            prepared = prepared with { Ecs = new(settings.Cluster, revisions.ToArray()) };
            await persist(prepared);
            contextSaved = true;
            // Stop every selected old service before starting any new revision against the schema.
            await StopServicesAsync(prepared, token);
            foreach (var revision in Ordered(revisions))
            {
                progress?.Invoke(new(KubernetesDeploymentProgressKind.Upgrade, revision.Component, KubernetesDeploymentProgressStatus.Patching, "Updating ECS task definition."));
                await StartRevisionAsync(revision, false, token);
                await WaitAsync(revision.Service, revision.TargetTaskDefinition, revision.DesiredCount, token);
                progress?.Invoke(new(KubernetesDeploymentProgressKind.Upgrade, revision.Component, KubernetesDeploymentProgressStatus.Completed, "ECS service deployment completed."));
            }
            await RestoreScalingAsync(revisions, token);
            return prepared with { Succeeded = true, Message = "ECS service upgrade completed." };
        }
        catch (OperationCanceledException) { throw; }
        catch { return !contextSaved ? KubernetesUpgradeResult.Failed("ECS upgrade preparation failed before service changes. Check image mappings, IAM, and service state.", scope, selected, validation)
            : prepared with { Succeeded = false, Message = "ECS upgrade failed. Review ECS service events, mappings, image access, and IAM permissions. Recorded rollback state is retained." }; }
    }

    public Task PrepareDatabaseRestoreAsync(KubernetesUpgradeResult result, CancellationToken cancellationToken) => StopServicesAsync(result, cancellationToken);

    private async Task StopServicesAsync(KubernetesUpgradeResult result, CancellationToken token)
    {
        var plan = RequirePlan(result);
        foreach (var revision in plan.Services) await AssertOwnedRevisionAsync(revision, token);
        foreach (var revision in plan.Services)
        {
            if (revision.Scaling is not null) await SetScalingAsync(revision.Scaling, true, token);
            await ecs.UpdateServiceAsync(new() { Cluster = settings.Cluster, Service = revision.Service, DesiredCount = 0 }, token);
        }
        foreach (var revision in plan.Services) await WaitAsync(revision.Service, null, 0, token);
    }

    public async Task<KubernetesRollbackResult> RollbackAsync(KubernetesUpgradeResult upgradeResult, CancellationToken cancellationToken = default, Action<KubernetesDeploymentProgress>? progress = null)
    {
        var plan = RequirePlan(upgradeResult);
        var results = new List<KubernetesDeploymentRollbackResult>();
        try
        {
            await StopServicesAsync(upgradeResult, cancellationToken);
            foreach (var revision in Ordered(plan.Services))
            {
                progress?.Invoke(new(KubernetesDeploymentProgressKind.Rollback, revision.Component, KubernetesDeploymentProgressStatus.RollingBack, "Restoring recorded ECS task definition."));
                await StartRevisionAsync(revision, true, cancellationToken);
                await WaitAsync(revision.Service, revision.PreviousTaskDefinition, revision.DesiredCount, cancellationToken);
                results.Add(new(true, revision.Component, revision.Container, upgradeResult.PreviousImages.Single(item => item.DeploymentName == revision.Component).Image, "Previous ECS revision restored."));
                progress?.Invoke(new(KubernetesDeploymentProgressKind.Rollback, revision.Component, KubernetesDeploymentProgressStatus.RollbackCompleted, "Previous ECS revision is healthy."));
            }
            await RestoreScalingAsync(plan.Services, cancellationToken);
            return new(true, false, "Previous ECS task definitions restored.", settings.Cluster, results);
        }
        catch (OperationCanceledException) { throw; }
        catch { return new(false, false, "ECS rollback failed. Services may remain stopped; retained backups and manual recovery are required.", settings.Cluster, results); }
    }

    public Task<KubernetesRollbackResult> ReconcileRollbackAsync(KubernetesUpgradeResult result, CancellationToken cancellationToken = default, Action<KubernetesDeploymentProgress>? progress = null) => RollbackAsync(result, cancellationToken, progress);

    public async Task<KubernetesUpgradeResult> ReconcileUpgradeRolloutAsync(KubernetesUpgradeResult result, CancellationToken cancellationToken = default, Action<KubernetesDeploymentProgress>? progress = null)
    {
        var plan = RequirePlan(result);
        try
        {
            await StopServicesAsync(result, cancellationToken);
            foreach (var revision in Ordered(plan.Services))
            {
                await AssertOwnedRevisionAsync(revision, cancellationToken);
                await StartRevisionAsync(revision, false, cancellationToken);
                await WaitAsync(revision.Service, revision.TargetTaskDefinition, revision.DesiredCount, cancellationToken);
            }
            await RestoreScalingAsync(plan.Services, cancellationToken);
            return result with { Succeeded = true, Message = "ECS rollout recovered." };
        }
        catch (OperationCanceledException) { throw; }
        catch { return result with { Succeeded = false, Message = "ECS rollout recovery failed; recorded rollback state is retained." }; }
    }

    private EcsDeploymentPlan RequirePlan(KubernetesUpgradeResult result)
    {
        var plan = result.Ecs ?? throw new InvalidOperationException("This operation has no ECS recovery context. Do not switch execution environments during an operation.");
        if (plan.Cluster != settings.Cluster || plan.Services.Count == 0 || plan.Services.Any(item => !settings.Services.TryGetValue(item.Component, out var mapping) || mapping.Service != item.Service || mapping.Container != item.Container))
            throw new InvalidOperationException("ECS recovery context does not match the configured deployment.");
        return plan;
    }

    private async Task AssertOwnedRevisionAsync(EcsServiceRevision revision, CancellationToken token)
    {
        var service = await ServiceAsync(revision.Service, token);
        if (service.TaskDefinition != revision.PreviousTaskDefinition && service.TaskDefinition != revision.TargetTaskDefinition)
            throw new InvalidOperationException("An external ECS deployment changed this service. Automatic changes are blocked.");
    }

    private Task<UpdateServiceResponse> StartRevisionAsync(EcsServiceRevision revision, bool rollback, CancellationToken token) => ecs.UpdateServiceAsync(new()
    {
        Cluster = settings.Cluster, Service = revision.Service, DesiredCount = revision.DesiredCount,
        TaskDefinition = rollback ? revision.PreviousTaskDefinition : revision.TargetTaskDefinition
    }, token);

    private async Task WaitAsync(string name, string? definition, int count, CancellationToken token)
    {
        var deadline = DateTimeOffset.UtcNow.AddSeconds(settings.RolloutTimeoutSeconds);
        while (DateTimeOffset.UtcNow < deadline)
        {
            var service = await ServiceAsync(name, token);
            if (count == 0 && service.RunningCount == 0 && service.PendingCount == 0) return;
            if (definition is not null && service.TaskDefinition == definition && service.RunningCount == count && service.PendingCount == 0 &&
                service.Deployments?.Count == 1 && service.Deployments[0].RolloutState == DeploymentRolloutState.COMPLETED) return;
            if (service.Deployments?.Any(item => item.RolloutState == DeploymentRolloutState.FAILED) == true) throw new InvalidOperationException("ECS deployment failed.");
            await Task.Delay(TimeSpan.FromSeconds(settings.PollSeconds), token);
        }
        throw new TimeoutException("ECS service did not reach the expected task definition and health state.");
    }

    private async Task<EcsService> ServiceAsync(string name, CancellationToken token)
    {
        var response = await ecs.DescribeServicesAsync(new() { Cluster = settings.Cluster, Services = [name] }, token);
        var service = response.Services?.SingleOrDefault() ?? throw new InvalidOperationException("ECS service not found.");
        if (response.Failures?.Count > 0 || service.Status != "ACTIVE" || service.DeploymentController?.Type != DeploymentControllerType.ECS)
            throw new InvalidOperationException("Only active ECS rolling services are supported.");
        return service;
    }

    private Task<DescribeTaskDefinitionResponse> DefinitionAsync(string arn, CancellationToken token) => ecs.DescribeTaskDefinitionAsync(new() { TaskDefinition = arn, Include = [TaskDefinitionField.TAGS] }, token);

    internal static RegisterTaskDefinitionRequest CopyDefinition(DescribeTaskDefinitionResponse response)
    {
        var request = new RegisterTaskDefinitionRequest();
        foreach (var target in typeof(RegisterTaskDefinitionRequest).GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(item => item.CanWrite))
        {
            var source = typeof(TaskDefinition).GetProperty(target.Name);
            if (source?.CanRead == true && target.PropertyType.IsAssignableFrom(source.PropertyType)) target.SetValue(request, source.GetValue(response.TaskDefinition));
        }
        request.Tags = response.Tags;
        return request;
    }

    private void ValidateImage(string image)
    {
        if (string.IsNullOrWhiteSpace(image) || image.Any(char.IsWhiteSpace) || image.Contains("://") || !image.Contains(':')) throw new InvalidOperationException("Invalid ECS release image.");
        var repository = image[..image.LastIndexOf(':')];
        var segments = repository.Split('/');
        var host = segments[0].Contains('.') || segments[0].Contains(':') ? segments[0] : "docker.io";
        var path = host == segments[0] ? string.Join('/', segments.Skip(1)) : repository;
        if (!imageOptions.Value.AllowedImageRegistryHosts.Contains(host, StringComparer.OrdinalIgnoreCase) ||
            !imageOptions.Value.AllowedImageRepositories.Any(allowed => path == allowed || path.StartsWith(allowed.TrimEnd('/') + "/", StringComparison.Ordinal)))
            throw new InvalidOperationException("ECS image repository is not approved.");
    }

    private static IEnumerable<EcsServiceRevision> Ordered(IEnumerable<EcsServiceRevision> revisions) => revisions.OrderBy(item => item.Component switch
    { "id-ums-deployment" => 0, "id-api-deployment" => 1, "id-web-deployment" => 2, "bi-dataservice-deployment" => 3, "bi-web-deployment" => 4, "bi-api-deployment" => 5, "bi-jobs-deployment" => 6, _ => 7 });

    private async Task<EcsScalingState?> ReadScalingAsync(string name, CancellationToken token)
    {
        var resource = $"service/{settings.Cluster.Split('/').Last()}/{name.Split('/').Last()}";
        var result = await scaling.DescribeScalableTargetsAsync(new() { ServiceNamespace = ServiceNamespace.Ecs, ResourceIds = [resource], ScalableDimension = ScalableDimension.EcsServiceDesiredCount }, token);
        var target = result.ScalableTargets?.SingleOrDefault();
        return target is null ? null : new(resource, target.MinCapacity ?? 0, target.MaxCapacity ?? 0, target.SuspendedState?.DynamicScalingInSuspended == true, target.SuspendedState?.DynamicScalingOutSuspended == true, target.SuspendedState?.ScheduledScalingSuspended == true);
    }

    private Task<RegisterScalableTargetResponse> SetScalingAsync(EcsScalingState state, bool suspend, CancellationToken token) => scaling.RegisterScalableTargetAsync(new()
    {
        ServiceNamespace = ServiceNamespace.Ecs, ResourceId = state.ResourceId, ScalableDimension = ScalableDimension.EcsServiceDesiredCount,
        SuspendedState = new() { DynamicScalingInSuspended = suspend || state.InSuspended, DynamicScalingOutSuspended = suspend || state.OutSuspended, ScheduledScalingSuspended = suspend || state.ScheduledSuspended }
    }, token);

    private async Task RestoreScalingAsync(IEnumerable<EcsServiceRevision> revisions, CancellationToken token)
    {
        foreach (var revision in revisions) if (revision.Scaling is not null) await SetScalingAsync(revision.Scaling, false, token);
    }

    private sealed record ReleaseImages(string Version, List<ReleaseImage> Deployments);
    private sealed record ReleaseImage(string Name, string ImageTag);
}
