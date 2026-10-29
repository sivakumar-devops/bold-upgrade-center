using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using Bold.UpgradeCenter.Security;
using Bold.UpgradeCenter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Bold.UpgradeCenter.Tests;

public sealed class StandaloneAuthenticationTests
{
    [Theory]
    [InlineData("BoldBI", "Bold BI", "boldbi-login.svg")]
    [InlineData("boldreports", "Bold Reports", "boldreports-login.svg")]
    [InlineData("Common", "Bold", "bold-login.svg")]
    [InlineData("Auto", "Bold", "bold-login.svg")]
    public async Task LoginUsesLocalIdpBrandingAndKeepsTheExistingFormContract(string mode, string product, string logo)
    {
        using var factory = new UpgradeCenterFactory(deploymentMode: mode);
        using var client = factory.CreateClient(ClientOptions);
        var html = await client.GetStringAsync("/upgrade-center/login?returnUrl=%2Fupgrade-center%2Fmonitor%2Ftest");
        Assert.Contains($"Sign in - {product} Upgrade Center", html);
        Assert.Contains(logo, html);
        Assert.Contains("standalone-login-page", html);
        Assert.Contains("/upgrade-center/css/standalone-login.css", html);
        Assert.Contains("/upgrade-center/css/idp-roboto.css", html);
        Assert.Contains("action=\"/upgrade-center/login\"", html);
        Assert.Contains("autocomplete=\"username\"", html);
        Assert.Contains("autocomplete=\"current-password\"", html);
        Assert.Contains("name=\"ReturnUrl\" value=\"/upgrade-center/monitor/test\"", html);
        Assert.Contains("aria-label=\"Show password\"", html);
        Assert.DoesNotContain("Remember me", html);
        Assert.DoesNotContain("/accounts/login", html);
        Assert.NotEmpty(GetToken(html));
        Assert.Empty(factory.RemoteRequests);
    }

    [Fact]
    public async Task ProfileMenuProvidesAStyledAntiforgeryProtectedStandaloneSignout()
    {
        using var factory = new UpgradeCenterFactory(deploymentMode: "BoldBI");
        using var client = factory.CreateClient(ClientOptions);
        var login = await client.GetStringAsync("/upgrade-center/login");
        var invalid = await LoginAsync(client, password: "wrong-password");
        var error = await invalid.Content.ReadAsStringAsync();
        await LoginAsync(client);
        var profile = await client.GetStringAsync("/upgrade-center/test-auth/profile");
        Assert.Contains("class=\"user-menu-signout\"", profile);
        Assert.Contains("action=\"/upgrade-center/sign-out\"", profile);
        Assert.Contains("class=\"user-menu-logout\" type=\"submit\"", profile);
        Assert.DoesNotContain("standalone-signout", profile);

        var logout = await client.PostAsync("/upgrade-center/sign-out", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = GetToken(profile)
        }));
        Assert.Equal("/upgrade-center/login", logout.Headers.Location!.OriginalString);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/upgrade-center/test-auth")).StatusCode);
        Assert.Empty(factory.RemoteRequests);

        var output = Environment.GetEnvironmentVariable("BOLD_UC_UI_OUTPUT");
        if (!string.IsNullOrWhiteSpace(output))
        {
            Directory.CreateDirectory(output);
            await File.WriteAllTextAsync(Path.Combine(output, "login.html"), login);
            await File.WriteAllTextAsync(Path.Combine(output, "login-error.html"), error);
            await File.WriteAllTextAsync(Path.Combine(output, "profile.html"), profile);
        }
    }

    [Fact]
    public async Task IntegratedProfileMenuKeepsItsExistingLogoutLink()
    {
        using var factory = new UpgradeCenterFactory(standalone: false);
        using var client = factory.CreateClient(ClientOptions);
        client.DefaultRequestHeaders.Add("Cookie", "product-session=test-cookie");
        var html = await client.GetStringAsync("/upgrade-center/test-auth/profile");
        Assert.Contains("href=\"/upgrade-center/logout\"", html);
        Assert.DoesNotContain("class=\"user-menu-signout\"", html);
    }

    [Fact]
    public async Task StandaloneUsesFileProvidersAndLocalLoginWithoutIdpRequests()
    {
        using var factory = new UpgradeCenterFactory();
        using var client = factory.CreateClient(ClientOptions);
        Assert.IsType<FileUpgradeCenterConfigurationProvider>(factory.Services.GetRequiredService<IUpgradeCenterConfigurationProvider>());
        Assert.Same(factory.Services.GetRequiredService<IUpgradeCenterConfigurationProvider>(), factory.Services.GetRequiredService<IIdpPrivateKeyProvider>());
        var challenge = await client.GetAsync("/upgrade-center/test-auth");
        Assert.Equal(HttpStatusCode.Redirect, challenge.StatusCode);
        Assert.StartsWith("https://localhost/upgrade-center/login", challenge.Headers.Location!.ToString());

        var login = await LoginAsync(client);
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Contains(login.Headers.GetValues("Set-Cookie"), cookie => cookie.StartsWith(".Bold.UpgradeCenter.Standalone=", StringComparison.Ordinal) && cookie.Contains("httponly", StringComparison.OrdinalIgnoreCase));
        var protectedResponse = await client.GetAsync("/upgrade-center/test-auth");
        Assert.Equal(HttpStatusCode.OK, protectedResponse.StatusCode);
        Assert.Contains("Upgrade Center Admin", await protectedResponse.Content.ReadAsStringAsync());
        Assert.Empty(factory.RemoteRequests);
    }

    [Fact]
    public async Task ObjectStorageStandaloneStartsAndAuthenticatesWithoutMountedFilesOrIdp()
    {
        using var factory = new UpgradeCenterFactory(objectStorage: true);
        factory.RemoveKeyFile();
        using var client = factory.CreateClient(ClientOptions);
        Assert.IsType<ObjectStorageUpgradeCenterConfigurationProvider>(factory.Services.GetRequiredService<IUpgradeCenterConfigurationProvider>());
        Assert.Equal(HttpStatusCode.Redirect, (await LoginAsync(client)).StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/upgrade-center/test-auth")).StatusCode);
        Assert.Empty(factory.RemoteRequests);
    }

    [Fact]
    public async Task NoPvLoginCookieAndReportSurviveApplicationReplacement()
    {
        var storage = new MemoryUpgradeCenterObjectStore();
        string cookie;
        using (var first = new UpgradeCenterFactory(objectStorage: true, persistence: storage))
        using (var client = first.CreateClient(ClientOptions))
        {
            var login = await LoginAsync(client);
            cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(".Bold.UpgradeCenter.Standalone=", StringComparison.Ordinal)).Split(';')[0];
            using var report = new MemoryStream(Encoding.UTF8.GetBytes("persisted report"));
            Assert.True(first.Services.GetRequiredService<IPlaywrightReportStore>().TrySaveHtml("job", PlaywrightValidationMode.Pre, report, out _));
        }
        using var replacement = new UpgradeCenterFactory(objectStorage: true, persistence: storage);
        using var restartedClient = replacement.CreateClient(ClientOptions);
        restartedClient.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await restartedClient.GetAsync("/upgrade-center/test-auth")).StatusCode);
        Assert.True(replacement.Services.GetRequiredService<IPlaywrightReportStore>().TryOpenRead("job", PlaywrightValidationMode.Pre, out var saved));
        using (saved) using (var reader = new StreamReader(saved!)) Assert.Equal("persisted report", await reader.ReadToEndAsync());
        Assert.Empty(replacement.RemoteRequests);
    }

    [Fact]
    public async Task LoginAndLogoutRequireAntiforgeryTokens()
    {
        using var factory = new UpgradeCenterFactory();
        using var client = factory.CreateClient(ClientOptions);
        var invalidLogin = await client.PostAsync("/upgrade-center/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = ProductConfigurationFixture.Username, ["Password"] = ProductConfigurationFixture.Password
        }));
        Assert.Equal(HttpStatusCode.BadRequest, invalidLogin.StatusCode);
        await LoginAsync(client);
        var invalidLogout = await client.PostAsync("/upgrade-center/sign-out", new FormUrlEncodedContent(new Dictionary<string, string>()));
        Assert.Equal(HttpStatusCode.BadRequest, invalidLogout.StatusCode);
        var logoutPage = await client.GetStringAsync("/upgrade-center/sign-out");
        var logout = await client.PostAsync("/upgrade-center/sign-out", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = GetToken(logoutPage)
        }));
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/upgrade-center/test-auth")).StatusCode);
        Assert.Empty(factory.RemoteRequests);
    }

    [Fact]
    public async Task WrongCredentialsDoNotIssueAnAuthenticationCookie()
    {
        using var factory = new UpgradeCenterFactory();
        using var client = factory.CreateClient(ClientOptions);
        var result = await LoginAsync(client, password: "wrong-password");
        Assert.Equal(HttpStatusCode.OK, result.StatusCode);
        var html = await result.Content.ReadAsStringAsync();
        Assert.Contains("The username or password is incorrect.", html);
        Assert.DoesNotContain("wrong-password", html);
        Assert.False(result.Headers.TryGetValues("Set-Cookie", out var cookies) && cookies.Any(cookie => cookie.StartsWith(".Bold.UpgradeCenter.Standalone=", StringComparison.Ordinal)));
        Assert.Equal(HttpStatusCode.Redirect, (await client.GetAsync("/upgrade-center/test-auth")).StatusCode);
    }

    [Theory]
    [InlineData("https://attacker.example/", "/upgrade-center/index")]
    [InlineData("//attacker.example/", "/upgrade-center/index")]
    [InlineData("/accounts/logout", "/upgrade-center/index")]
    [InlineData("/upgrade-center/login", "/upgrade-center/index")]
    [InlineData("/upgrade-center/monitor/test?product=Reports", "/upgrade-center/monitor/test?product=Reports")]
    public async Task ReturnUrlsStayInsideUpgradeCenter(string returnUrl, string expected)
    {
        using var factory = new UpgradeCenterFactory();
        using var client = factory.CreateClient(ClientOptions);
        var result = await LoginAsync(client, returnUrl: returnUrl);
        Assert.Equal(expected, result.Headers.Location!.OriginalString);
    }

    [Fact]
    public async Task LoginAttemptsAreRateLimited()
    {
        using var factory = new UpgradeCenterFactory();
        using var client = factory.CreateClient(ClientOptions);
        for (var attempt = 0; attempt < 5; attempt++)
        {
            Assert.Equal(HttpStatusCode.OK, (await LoginAsync(client, password: "wrong-password")).StatusCode);
        }
        Assert.Equal(HttpStatusCode.TooManyRequests, (await LoginAsync(client, password: "wrong-password")).StatusCode);
    }

    [Fact]
    public async Task LocalLoginPreservesAnAllowedProxyPrefix()
    {
        using var factory = new UpgradeCenterFactory();
        using var client = factory.CreateClient(ClientOptions);
        client.DefaultRequestHeaders.Add("X-Forwarded-Prefix", "/platform");
        var page = await client.GetAsync("/upgrade-center/login");
        var html = await page.Content.ReadAsStringAsync();
        Assert.Contains("action=\"/platform/upgrade-center/login\"", html);
        // The proxy strips /platform before forwarding; reproduce its cookie forwarding too.
        var antiforgeryCookie = page.Headers.GetValues("Set-Cookie").First(cookie => cookie.StartsWith(".AspNetCore.Antiforgery.", StringComparison.Ordinal)).Split(';')[0];
        client.DefaultRequestHeaders.Add("Cookie", antiforgeryCookie);
        var result = await client.PostAsync("/upgrade-center/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = ProductConfigurationFixture.Username,
            ["Password"] = ProductConfigurationFixture.Password,
            ["ReturnUrl"] = "/platform/upgrade-center/monitor/test?product=Reports",
            ["__RequestVerificationToken"] = GetToken(html)
        }));
        Assert.Equal(HttpStatusCode.Redirect, result.StatusCode);
        Assert.Equal("/platform/upgrade-center/monitor/test?product=Reports", result.Headers.Location!.OriginalString);
        Assert.Empty(factory.RemoteRequests);
    }

    [Fact]
    public void MissingMountedKeyStopsStandaloneStartup()
    {
        using var factory = new UpgradeCenterFactory();
        factory.RemoveKeyFile();
        var error = Assert.ThrowsAny<Exception>(() => factory.CreateClient(ClientOptions));
        Assert.Contains("PrivateKeyFilePath", error.ToString());
        Assert.Empty(factory.RemoteRequests);
    }

    [Fact]
    public async Task IntegratedModeKeepsRemoteProvidersAndUmsAuthentication()
    {
        using var factory = new UpgradeCenterFactory(standalone: false);
        using var client = factory.CreateClient(ClientOptions);
        Assert.IsType<IdpConfigurationProvider>(factory.Services.GetRequiredService<IUpgradeCenterConfigurationProvider>());
        Assert.IsType<IdpPrivateKeyProvider>(factory.Services.GetRequiredService<IIdpPrivateKeyProvider>());
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync("/upgrade-center/login")).StatusCode);
        client.DefaultRequestHeaders.Add("Cookie", "product-session=test-cookie");
        var response = await client.GetAsync("/upgrade-center/test-auth");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("UMS Admin", await response.Content.ReadAsStringAsync());
        Assert.Contains(factory.RemoteRequests, path => path.EndsWith("/upgrade-center/auth/session", StringComparison.Ordinal));
    }

    [Fact]
    public void ChangedCredentialsInvalidatePreviouslyIssuedPrincipals()
    {
        var oldCredentials = CreateCredentials("old-test-password");
        var newCredentials = CreateCredentials("new-test-password");
        Assert.False(newCredentials.IsCurrentPrincipal(oldCredentials.CreatePrincipal()));
        Assert.True(newCredentials.IsCurrentPrincipal(newCredentials.CreatePrincipal()));
    }

    [Fact]
    public void MissingLocalCredentialsAreRejected()
    {
        Assert.Throws<InvalidOperationException>(() => new StandaloneAdminCredentials(
            Microsoft.Extensions.Options.Options.Create(new UpgradeCenterOptions()),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()));
    }

    [Fact]
    public async Task LocalAuthenticationCookieSurvivesAnApplicationRestartWithTheSameKeys()
    {
        string cookie;
        using (var firstFactory = new UpgradeCenterFactory())
        using (var firstClient = firstFactory.CreateClient(ClientOptions))
        {
            var login = await LoginAsync(firstClient);
            cookie = login.Headers.GetValues("Set-Cookie").Single(value => value.StartsWith(".Bold.UpgradeCenter.Standalone=", StringComparison.Ordinal)).Split(';')[0];
        }

        using var secondFactory = new UpgradeCenterFactory();
        using var secondClient = secondFactory.CreateClient(ClientOptions);
        secondClient.DefaultRequestHeaders.Add("Cookie", cookie);
        Assert.Equal(HttpStatusCode.OK, (await secondClient.GetAsync("/upgrade-center/test-auth")).StatusCode);
        Assert.Empty(secondFactory.RemoteRequests);
    }

    private static StandaloneAdminCredentials CreateCredentials(string password) => new(
        Microsoft.Extensions.Options.Options.Create(new UpgradeCenterOptions
        {
            Authentication = new() { Standalone = new() { Username = "admin", Password = password } }
        }), new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static readonly WebApplicationFactoryClientOptions ClientOptions = new()
    {
        AllowAutoRedirect = false,
        BaseAddress = new Uri("https://localhost")
    };

    private static async Task<HttpResponseMessage> LoginAsync(HttpClient client, string? password = null, string returnUrl = "/upgrade-center")
    {
        var html = await client.GetStringAsync("/upgrade-center/login");
        return await client.PostAsync("/upgrade-center/login", new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Username"] = ProductConfigurationFixture.Username,
            ["Password"] = password ?? ProductConfigurationFixture.Password,
            ["ReturnUrl"] = returnUrl,
            ["__RequestVerificationToken"] = GetToken(html)
        }));
    }

    private static string GetToken(string html)
    {
        var token = Regex.Match(html, "name=\"__RequestVerificationToken\"[^>]*value=\"([^\"]+)\"");
        Assert.True(token.Success, "Expected an antiforgery token in the rendered form.");
        return WebUtility.HtmlDecode(token.Groups[1].Value);
    }
}

[Route("upgrade-center/test-auth")]
[Authorize(Policy = UpgradeCenterPolicies.AdminOnly)]
public sealed class AuthenticationProbeController(IUpgradeCenterAuthorizationService authorization) : Controller
{
    [HttpGet]
    public async Task<IActionResult> Get() => Ok(await authorization.GetCurrentUserAsync(User));

    [HttpGet("profile")]
    public async Task<IActionResult> Profile() => View("_Topbar", await authorization.GetCurrentUserAsync(User));
}

internal sealed class UpgradeCenterFactory : WebApplicationFactory<Program>
{
    private readonly ProductConfigurationFixture fixture = new();
    private readonly Dictionary<string, string?> previousEnvironment = [];
    private bool disposed;
    private readonly bool objectStorage;
    private readonly IUpgradeCenterObjectStore? persistence;
    private readonly string privateKey;
    public List<string> RemoteRequests { get; } = [];

    public UpgradeCenterFactory(bool standalone = true, bool objectStorage = false, IUpgradeCenterObjectStore? persistence = null, string? deploymentMode = null)
    {
        this.objectStorage = objectStorage;
        this.persistence = persistence;
        privateKey = File.ReadAllText(fixture.KeyPath);
        SetEnvironment("UpgradeCenter__IntegrationMode", standalone ? "Standalone" : null);
        SetEnvironment("UpgradeCenter__DeploymentMode", deploymentMode);
        SetEnvironment("UpgradeCenter__Configuration__Source", null);
        SetEnvironment("BOLD_SERVICES_STORAGETYPE", objectStorage ? "3" : null);
        SetEnvironment("BOLD_SERVICES_PV_USED", objectStorage ? "false" : "true");
        if (objectStorage)
        {
            SetEnvironment("BOLD_SERVICES_AMAZON_BUCKET_NAME", "test-storage");
            SetEnvironment("BOLD_SERVICES_AWS_REGION", "us-east-1");
        }
        SetEnvironment("UpgradeCenter__Configuration__ConfigFilePath", fixture.ConfigPath);
        SetEnvironment("UpgradeCenter__Configuration__PrivateKeyFilePath", fixture.KeyPath);
        SetEnvironment("BOLD_ADMIN_USERNAME", ProductConfigurationFixture.Username);
        SetEnvironment("BOLD_ADMIN_PASSWORD", ProductConfigurationFixture.Password);
        SetEnvironment("UpgradeCenter__Services__Ums", "https://ums.example/ums");
        SetEnvironment("UpgradeCenter__Services__IdpApi", "https://idp.example/api");
    }

    public void RemoveKeyFile() => File.Delete(fixture.KeyPath);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.UseEnvironment("Development");
        builder.UseContentRoot(Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "../../../../../")));
        builder.ConfigureTestServices(services =>
        {
            foreach (var service in services.Where(service => service.ServiceType == typeof(IHostedService) && service.ImplementationType == typeof(UpgradeJobRecoveryService)).ToArray())
            {
                services.Remove(service);
            }
            services.AddControllers().AddApplicationPart(typeof(AuthenticationProbeController).Assembly);
            services.AddSingleton<IHttpClientFactory>(new RecordedClientFactory(RemoteRequests, fixture.Root.ToJsonString()));
            if (objectStorage)
            {
                services.AddSingleton<IUpgradeCenterObjectStore>(persistence ?? new MemoryUpgradeCenterObjectStore());
                services.AddSingleton<IStandaloneObjectReader>(new TestObjectReader(fixture.Root.ToJsonString(), privateKey));
                services.AddSingleton<IStandaloneRuntimeConfigurationReader>(new TestRuntimeReader());
            }
        });
    }

    private void SetEnvironment(string key, string? value)
    {
        previousEnvironment[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposed)
        {
            return;
        }
        disposed = true;

        try
        {
            base.Dispose(disposing);
        }
        finally
        {
            if (disposing)
            {
                fixture.Dispose();
                foreach (var (key, value) in previousEnvironment)
                {
                    Environment.SetEnvironmentVariable(key, value);
                }
            }
        }
    }

    private sealed class RecordedClientFactory(List<string> requests, string config) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new Handler(requests, config));
    }

    private sealed class TestObjectReader(string config, string key) : IStandaloneObjectReader
    {
        public Task<StandaloneStorageObject> ReadAsync(string objectKey, string? expectedETag, CancellationToken cancellationToken) =>
            Task.FromResult(new StandaloneStorageObject(objectKey.EndsWith("config.json", StringComparison.Ordinal) ? config : key, "test-version"));
    }

    private sealed class TestRuntimeReader : IStandaloneRuntimeConfigurationReader
    {
        public Task<string?> ReadAsync(UpgradeCenterRuntimeConfiguration bootstrap, string connectionString, CancellationToken cancellationToken) => Task.FromResult<string?>(null);
    }

    private sealed class Handler(List<string> requests, string config) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            requests.Add(request.RequestUri!.AbsolutePath);
            var response = request.RequestUri.AbsolutePath.EndsWith("configuration", StringComparison.Ordinal)
                ? config
                : "{\"IsAuthenticated\":true,\"IsAdmin\":true,\"UserId\":\"test-user\",\"UserName\":\"Test Admin\",\"Status\":true}";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(response, Encoding.UTF8, "application/json") });
        }
    }
}
