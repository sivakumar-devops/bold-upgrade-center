using System.Net;
using Microsoft.AspNetCore.Authorization;

namespace Bold.UpgradeCenter.Security;

public static class UpgradeCenterPolicies
{
    public const string AdminOnly = "UpgradeCenterAdminOnly";
}

public sealed class AdminRequirement : IAuthorizationRequirement;

public sealed class AdminAuthorizationHandler : AuthorizationHandler<AdminRequirement>
{
    public const string HttpClientName = "UpgradeCenterAuthorization";

    private readonly IHttpClientFactory httpClientFactory;
    private readonly IConfiguration configuration;
    private readonly IHttpContextAccessor httpContextAccessor;

    public AdminAuthorizationHandler(
        IHttpClientFactory httpClientFactory,
        IConfiguration configuration,
        IHttpContextAccessor httpContextAccessor)
    {
        this.httpClientFactory = httpClientFactory;
        this.configuration = configuration;
        this.httpContextAccessor = httpContextAccessor;
    }

    protected override async Task HandleRequirementAsync(AuthorizationHandlerContext context, AdminRequirement requirement)
    {
        if (context.User.Identity?.IsAuthenticated != true)
        {
            return;
        }

        if (await IsAdminInTenantManagementAsync())
        {
            context.Succeed(requirement);
        }
    }

    private async Task<bool> IsAdminInTenantManagementAsync()
    {
        var httpContext = httpContextAccessor.HttpContext;
        var validationUrl = ResolveAdminValidationUrl();
        if (httpContext == null || string.IsNullOrWhiteSpace(validationUrl))
        {
            return false;
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, validationUrl);
        var cookieHeader = httpContext.Request.Headers.Cookie.ToString();
        if (!string.IsNullOrWhiteSpace(cookieHeader))
        {
            request.Headers.TryAddWithoutValidation("Cookie", cookieHeader);
        }

        try
        {
            var client = httpClientFactory.CreateClient(HttpClientName);
            using var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                httpContext.RequestAborted);

            return response.StatusCode == HttpStatusCode.OK;
        }
        catch (OperationCanceledException) when (httpContext.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private string? ResolveAdminValidationUrl()
    {
        var configuredValidationUrl = FirstConfiguredValue(
            configuration["UpgradeCenter:Authorization:AdminValidationUrl"],
            configuration["BOLD_UPGRADE_CENTER_ADMIN_VALIDATION_URL"]);
        if (!string.IsNullOrWhiteSpace(configuredValidationUrl))
        {
            return configuredValidationUrl;
        }

        var umsServiceUrl = FirstConfiguredValue(
            configuration["UpgradeCenter:Authorization:UmsUrl"],
            configuration["ID_UMS_SERVICE_URL"]);

        return string.IsNullOrWhiteSpace(umsServiceUrl)
            ? null
            : $"{umsServiceUrl.TrimEnd('/')}/upgrade-center/authorization/admin";
    }

    private static string? FirstConfiguredValue(params string?[] values)
    {
        return values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
    }
}
