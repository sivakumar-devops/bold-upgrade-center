using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace BoldBI.UpgradeCenter.Services;

public interface IContainerImageRegistryValidator
{
    Task<ContainerImageValidationResult> ValidateAsync(
        string imageReference,
        CancellationToken cancellationToken = default);
}

public sealed record ContainerImageValidationResult(
    bool IsValid,
    string Message);

public sealed class ContainerImageRegistryValidator : IContainerImageRegistryValidator
{
    public const string HttpClientName = "container-registry";

    private static readonly MediaTypeWithQualityHeaderValue[] ManifestAcceptHeaders =
    [
        new("application/vnd.oci.image.manifest.v1+json"),
        new("application/vnd.docker.distribution.manifest.v2+json"),
        new("application/vnd.docker.distribution.manifest.list.v2+json"),
        new("application/vnd.oci.image.index.v1+json")
    ];

    private readonly IHttpClientFactory httpClientFactory;
    private readonly ILogger<ContainerImageRegistryValidator> logger;

    public ContainerImageRegistryValidator(
        IHttpClientFactory httpClientFactory,
        ILogger<ContainerImageRegistryValidator> logger)
    {
        this.httpClientFactory = httpClientFactory;
        this.logger = logger;
    }

    public async Task<ContainerImageValidationResult> ValidateAsync(
        string imageReference,
        CancellationToken cancellationToken = default)
    {
        if (!TryParseImageReference(imageReference, out var registry, out var repository, out var tag))
        {
            return new ContainerImageValidationResult(false, "Image reference must include registry, repository, image name, and tag.");
        }

        var client = httpClientFactory.CreateClient(HttpClientName);
        var manifestUri = new Uri($"https://{registry}/v2/{repository}/manifests/{Uri.EscapeDataString(tag)}");
        try
        {
            var result = await ProbeManifestAsync(client, manifestUri, null, cancellationToken);
            if (result.StatusCode == HttpStatusCode.Unauthorized &&
                TryCreateBearerChallengeRequest(result.AuthenticateHeader, repository, out var tokenUri))
            {
                var token = await RequestAnonymousBearerTokenAsync(client, tokenUri!, cancellationToken);
                if (!string.IsNullOrWhiteSpace(token))
                {
                    result = await ProbeManifestAsync(client, manifestUri, token, cancellationToken);
                }
            }

            return result.StatusCode switch
            {
                HttpStatusCode.OK => new ContainerImageValidationResult(true, "Image manifest is available."),
                HttpStatusCode.NotFound => new ContainerImageValidationResult(false, "Image tag was not found in the registry."),
                HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => new ContainerImageValidationResult(false, "Registry denied access to the image manifest."),
                _ => new ContainerImageValidationResult(false, $"Registry returned HTTP {(int)result.StatusCode}.")
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            logger.LogWarning(exception, "Unable to validate custom patch image manifest.");
            return new ContainerImageValidationResult(false, "Unable to contact the container registry for image validation.");
        }
    }

    private static async Task<ManifestProbeResult> ProbeManifestAsync(
        HttpClient client,
        Uri manifestUri,
        string? bearerToken,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Head, manifestUri);
        ApplyManifestHeaders(request, bearerToken);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != HttpStatusCode.MethodNotAllowed)
        {
            return new ManifestProbeResult(response.StatusCode, response.Headers.WwwAuthenticate.ToString());
        }

        using var getRequest = new HttpRequestMessage(HttpMethod.Get, manifestUri);
        ApplyManifestHeaders(getRequest, bearerToken);
        using var getResponse = await client.SendAsync(getRequest, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        return new ManifestProbeResult(getResponse.StatusCode, getResponse.Headers.WwwAuthenticate.ToString());
    }

    private static void ApplyManifestHeaders(HttpRequestMessage request, string? bearerToken)
    {
        foreach (var header in ManifestAcceptHeaders)
        {
            request.Headers.Accept.Add(header);
        }

        if (!string.IsNullOrWhiteSpace(bearerToken))
        {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearerToken);
        }
    }

    private static async Task<string?> RequestAnonymousBearerTokenAsync(
        HttpClient client,
        Uri tokenUri,
        CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(tokenUri, cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
        return document.RootElement.TryGetProperty("token", out var token)
            ? token.GetString()
            : document.RootElement.TryGetProperty("access_token", out var accessToken)
                ? accessToken.GetString()
                : null;
    }

    private static bool TryCreateBearerChallengeRequest(
        string? authenticateHeader,
        string repository,
        out Uri? tokenUri)
    {
        tokenUri = null;
        if (string.IsNullOrWhiteSpace(authenticateHeader) ||
            !authenticateHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var parameters = ParseChallengeParameters(authenticateHeader["Bearer ".Length..]);
        if (!parameters.TryGetValue("realm", out var realm) ||
            !Uri.TryCreate(realm, UriKind.Absolute, out var realmUri))
        {
            return false;
        }

        var query = new List<string>();
        if (parameters.TryGetValue("service", out var service))
        {
            query.Add($"service={Uri.EscapeDataString(service)}");
        }

        var scope = parameters.TryGetValue("scope", out var challengeScope) && !string.IsNullOrWhiteSpace(challengeScope)
            ? challengeScope
            : $"repository:{repository}:pull";
        query.Add($"scope={Uri.EscapeDataString(scope)}");

        var builder = new UriBuilder(realmUri)
        {
            Query = string.Join("&", query)
        };
        tokenUri = builder.Uri;
        return true;
    }

    private static Dictionary<string, string> ParseChallengeParameters(string challenge)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var part in challenge.Split(','))
        {
            var pair = part.Split('=', 2);
            if (pair.Length != 2)
            {
                continue;
            }

            values[pair[0].Trim()] = pair[1].Trim().Trim('"');
        }

        return values;
    }

    private static bool TryParseImageReference(
        string imageReference,
        out string registry,
        out string repository,
        out string tag)
    {
        registry = string.Empty;
        repository = string.Empty;
        tag = string.Empty;

        var trimmed = imageReference.Trim();
        var slashIndex = trimmed.IndexOf('/');
        var lastSlashIndex = trimmed.LastIndexOf('/');
        var colonIndex = trimmed.LastIndexOf(':');
        if (slashIndex <= 0 || colonIndex <= lastSlashIndex || colonIndex == trimmed.Length - 1)
        {
            return false;
        }

        registry = trimmed[..slashIndex];
        repository = trimmed[(slashIndex + 1)..colonIndex];
        tag = trimmed[(colonIndex + 1)..];
        return !string.IsNullOrWhiteSpace(registry) &&
               !string.IsNullOrWhiteSpace(repository) &&
               !string.IsNullOrWhiteSpace(tag);
    }

    private sealed record ManifestProbeResult(
        HttpStatusCode StatusCode,
        string? AuthenticateHeader);
}
