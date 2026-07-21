using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Services;

public sealed class CustomPatchValidationService : ICustomPatchValidationService
{
    private static readonly Regex ImageReferencePattern = new(
        @"^(?=.{1,255}$)([a-zA-Z0-9.-]+(?::[0-9]+)?/)?[a-z0-9]+([._/-][a-z0-9]+)*:[A-Za-z0-9][A-Za-z0-9_.-]{0,127}$",
        RegexOptions.Compiled);
    private static readonly Regex ComparableVersionPrefixPattern = new(
        @"^[vV]?(?<version>\d+(?:\.\d+)+)(?:[-+][A-Za-z0-9_.-]+)?$",
        RegexOptions.Compiled);

    private readonly IKubernetesUpgradeService kubernetesUpgradeService;
    private readonly KubernetesUpgradeOptions kubernetesOptions;

    public CustomPatchValidationService(
        IKubernetesUpgradeService kubernetesUpgradeService,
        IOptions<KubernetesUpgradeOptions> kubernetesOptions)
    {
        this.kubernetesUpgradeService = kubernetesUpgradeService;
        this.kubernetesOptions = kubernetesOptions.Value;
    }

    public async Task<CustomPatchValidationResponse> ValidateAsync(
        CustomPatchValidationRequest request,
        string currentVersion,
        CancellationToken cancellationToken = default)
    {
        var errors = new List<string>();
        var results = new List<CustomPatchImageValidationResult>();
        var targetVersion = request.TargetVersion?.Trim() ?? string.Empty;

        ValidateTargetVersion(targetVersion, currentVersion, errors);

        var validation = await kubernetesUpgradeService.ValidateNamespaceAsync(cancellationToken);
        if (!validation.Succeeded)
        {
            errors.Add($"Kubernetes namespace validation failed: {validation.Message}");
        }

        var discoveredRequired = validation.DeploymentStatuses
            .Where(deployment => deployment.IsExpected && deployment.IsRequired && deployment.IsAvailable)
            .Select(deployment => deployment.DeploymentName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var discoveredOptional = validation.DeploymentStatuses
            .Where(deployment => deployment.IsExpected && deployment.IsOptional && deployment.IsAvailable)
            .Select(deployment => deployment.DeploymentName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        var discoveredTargets = discoveredRequired
            .Concat(discoveredOptional)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var images = request.Images
            .Where(image => !string.IsNullOrWhiteSpace(image.DeploymentName) || !string.IsNullOrWhiteSpace(image.Image))
            .ToList();
        if (images.Count == 0)
        {
            errors.Add("At least one custom deployment image is required.");
        }

        var duplicateDeployments = images
            .Where(image => !string.IsNullOrWhiteSpace(image.DeploymentName))
            .GroupBy(image => image.DeploymentName!.Trim(), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() > 1)
            .Select(group => group.Key)
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var duplicate in duplicateDeployments)
        {
            errors.Add($"Duplicate image mapping found for deployment '{duplicate}'.");
        }

        var mappedDeployments = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var image in images)
        {
            var deploymentName = image.DeploymentName?.Trim() ?? string.Empty;
            var imageReference = image.Image?.Trim() ?? string.Empty;
            var itemErrors = new List<string>();

            if (string.IsNullOrWhiteSpace(deploymentName))
            {
                itemErrors.Add("Deployment name is required.");
            }
            else if (!discoveredTargets.Contains(deploymentName))
            {
                itemErrors.Add("Deployment is not an available Bold BI upgrade target in the current namespace.");
            }
            else
            {
                mappedDeployments.Add(deploymentName);
            }

            ValidateImageReference(imageReference, itemErrors);

            if (itemErrors.Count > 0)
            {
                errors.AddRange(itemErrors.Select(error => $"{deploymentName}: {error}"));
                results.Add(new CustomPatchImageValidationResult(deploymentName, imageReference, false, string.Join(" ", itemErrors)));
                continue;
            }

            results.Add(new CustomPatchImageValidationResult(
                deploymentName,
                imageReference,
                true,
                "Image format, repository policy, and deployment mapping validation succeeded. Registry existence was not checked because no registry validation endpoint or credentials are configured."));
        }

        var missingRequired = discoveredRequired
            .Where(deployment => !mappedDeployments.Contains(deployment))
            .OrderBy(deployment => deployment, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (missingRequired.Count > 0)
        {
            errors.Add($"Missing custom image mapping for required deployment(s): {string.Join(", ", missingRequired)}.");
        }

        return new CustomPatchValidationResponse(
            errors.Count == 0 && results.Count > 0 && results.All(result => result.IsValid),
            targetVersion,
            results,
            errors.Distinct(StringComparer.OrdinalIgnoreCase).ToList());
    }

    private static void ValidateTargetVersion(string targetVersion, string currentVersion, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(targetVersion))
        {
            errors.Add("Custom target version is required.");
            return;
        }

        if (!TryParseComparableVersion(targetVersion, out var targetParts))
        {
            errors.Add("Custom target version must start with a valid product version, for example 16.1.90 or 16.1.90-hotfix-1.");
            return;
        }

        if (!ProductVersionComparer.TryParse(currentVersion, out var currentParts))
        {
            errors.Add("Currently installed version could not be parsed for comparison.");
            return;
        }

        if (ProductVersionComparer.Compare(targetParts, currentParts) <= 0)
        {
            errors.Add("Custom target version must be greater than the currently installed version.");
        }
    }

    private static bool TryParseComparableVersion(string value, out ProductVersionParts parts)
    {
        var match = ComparableVersionPrefixPattern.Match(value.Trim());
        if (!match.Success)
        {
            parts = new ProductVersionParts(Array.Empty<int>());
            return false;
        }

        return ProductVersionComparer.TryParse(match.Groups["version"].Value, out parts);
    }

    private void ValidateImageReference(string imageReference, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(imageReference))
        {
            errors.Add("Custom image reference is required.");
            return;
        }

        if (!ImageReferencePattern.IsMatch(imageReference))
        {
            errors.Add("Image reference must include a valid repository/name and tag, for example registry.example.com/boldbi/bi-api:16.1.90-custom.");
            return;
        }

        var allowedRepositories = kubernetesOptions.AllowedImageRepositories
            .Where(prefix => !string.IsNullOrWhiteSpace(prefix))
            .Select(prefix => prefix.Trim())
            .ToList();
        if (allowedRepositories.Count == 0)
        {
            return;
        }

        if (!allowedRepositories.Any(prefix => imageReference.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)))
        {
            errors.Add("Image repository is not in the configured allowed repository list.");
        }
    }
}
