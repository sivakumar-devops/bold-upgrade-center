using BoldBI.UpgradeCenter.Services;
using BoldBI.UpgradeCenter.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Legacy = Bold.UpgradeCenter.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
Directory.CreateDirectory(dataProtectionKeysPath);

// ── Add services ──────────────────────────────────────────────────────────

builder.Services.AddControllersWithViews();
builder.Services.AddHealthChecks();
builder.Services.AddHttpContextAccessor();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
    .SetApplicationName("BoldBI.UpgradeCenter");
builder.Services.Configure<UpgradeCenterOptions>(builder.Configuration.GetSection("UpgradeCenter"));
builder.Services.Configure<Legacy.UpgradeCenterOptions>(builder.Configuration.GetSection("UpgradeCenter"));
builder.Services.AddHttpClient(IdpConfigurationProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient(ProductVersionProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient<IReleaseVersionProvider, BoldBiReleaseVersionProvider>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient(RemoteUmsAuthenticationHandler.HttpClientName)
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<IUpgradeCenterConfigurationProvider, IdpConfigurationProvider>();
builder.Services.AddSingleton<IProductVersionProvider, ProductVersionProvider>();
builder.Services.AddSingleton<IAdminContinuationStore, InMemoryAdminContinuationStore>();
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(UpgradeCenterPolicies.AdminOnly, policy =>
    {
        policy.RequireAuthenticatedUser();
        policy.RequireClaim(UpgradeCenterClaimTypes.IsAdmin, "true");
    });
});
builder.Services
    .AddAuthentication(UpgradeCenterAuthenticationDefaults.Scheme)
    .AddScheme<AuthenticationSchemeOptions, RemoteUmsAuthenticationHandler>(
        UpgradeCenterAuthenticationDefaults.Scheme,
        _ => { });

// Real Upgrade Center backend workflow reused from the validated Upgrade Center implementation.
builder.Services.AddHttpClient(Legacy.IdpPrivateKeyProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(15);
});
builder.Services.AddHttpClient<Legacy.IUpgradeDatabaseScriptImpactService, Legacy.UpgradeDatabaseScriptImpactService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.AddHttpClient<Legacy.IKubernetesUpgradeService, Legacy.KubernetesUpgradeService>(client =>
{
    client.Timeout = TimeSpan.FromSeconds(30);
});
builder.Services.Configure<Legacy.PlaywrightExecutionOptions>(builder.Configuration.GetSection("UpgradeCenter:Playwright"));
builder.Services.Configure<Legacy.DatabaseBackupOptions>(builder.Configuration.GetSection("UpgradeCenter:DatabaseBackup"));
builder.Services.Configure<Legacy.KubernetesUpgradeOptions>(builder.Configuration.GetSection("UpgradeCenter:Kubernetes"));
builder.Services.AddSingleton<Legacy.IUpgradeCenterConfigurationProvider, LegacyConfigurationProviderAdapter>();
builder.Services.AddSingleton<Legacy.IProductVersionProvider, LegacyProductVersionProviderAdapter>();
builder.Services.AddSingleton<Legacy.IReleaseVersionProvider, LegacyReleaseVersionProviderAdapter>();
builder.Services.AddSingleton<Legacy.IIdpPrivateKeyProvider, Legacy.IdpPrivateKeyProvider>();
builder.Services.AddSingleton<Legacy.IBoldConnectionStringDecryptor, Legacy.BoldConnectionStringDecryptor>();
builder.Services.AddScoped<Legacy.IUpgradeDatabaseDiscoveryService, Legacy.UpgradeDatabaseDiscoveryService>();
builder.Services.AddScoped<Legacy.IUpgradeUserContextProvider, Legacy.UpgradeUserContextProvider>();
builder.Services.AddScoped<Legacy.IPlaywrightScriptRunner, Legacy.PlaywrightScriptRunner>();
builder.Services.AddSingleton<Legacy.IUpgradeOperationalStateStore, Legacy.DatabaseUpgradeOperationalStateStore>();
builder.Services.AddSingleton<Legacy.IUpgradeRollbackStore, Legacy.InMemoryUpgradeRollbackStore>();
builder.Services.AddSingleton<Legacy.IUpgradeJobStore, Legacy.InMemoryUpgradeJobStore>();
builder.Services.AddSingleton<Legacy.IUpgradeJobRunner, Legacy.UpgradeJobRunner>();
builder.Services.AddSingleton<Legacy.IUpgradeRollbackJobRunner, Legacy.UpgradeRollbackJobRunner>();
builder.Services.AddHostedService<Legacy.UpgradeJobRecoveryService>();
builder.Services.AddScoped<Legacy.IUpgradeHistoryStore, Legacy.DatabaseUpgradeHistoryStore>();
builder.Services.AddScoped<Legacy.ICustomPatchValidationService, Legacy.CustomPatchValidationService>();
builder.Services.AddScoped<Legacy.IUpgradeDatabaseBackupService, Legacy.UpgradeDatabaseBackupService>();
builder.Services.AddScoped<Legacy.IUpgradeImpactService, Legacy.UpgradeImpactService>();

builder.Services.AddSingleton<IUpgradeJobStore, LegacyUpgradeJobStoreAdapter>();
builder.Services.AddScoped<IInstallationInfoProvider, ProductVersionInstallationInfoProvider>();
builder.Services.AddScoped<IUpgradeCenterAuthorizationService, ClaimsUpgradeCenterAuthorizationService>();
builder.Services.AddScoped<IUpgradeService, LegacyUpgradeServiceAdapter>();

var app = builder.Build();

app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedHost | ForwardedHeaders.XForwardedProto
});

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.Use(ApplyForwardedPrefix);
app.UseStaticFiles(new StaticFileOptions
{
    RequestPath = "/upgrade-center"
});
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.MapStaticAssets();
app.MapHealthChecks("/health-check");
app.MapHealthChecks("/upgrade-center/health-check");

// Default route — redirect / to /upgrade-center
app.MapControllerRoute(
    name: "upgrade-center",
    pattern: "upgrade-center/{action=Index}/{jobId?}",
    defaults: new { controller = "UpgradeCenter" });

// Root path → redirect to upgrade center
app.MapGet("/", () => Results.Redirect("/upgrade-center"));

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=UpgradeCenter}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();

static async Task ApplyForwardedPrefix(HttpContext context, RequestDelegate next)
{
    var forwardedPrefix = context.Request.Headers["X-Forwarded-Prefix"].FirstOrDefault();
    if (!string.IsNullOrWhiteSpace(forwardedPrefix))
    {
        var normalizedPrefix = NormalizePath(forwardedPrefix);
        if (normalizedPrefix != "/" &&
            !context.Request.PathBase.StartsWithSegments(normalizedPrefix, StringComparison.OrdinalIgnoreCase))
        {
            context.Request.PathBase = normalizedPrefix + context.Request.PathBase;
        }
    }

    await next(context);
}

static string NormalizePath(string value)
{
    if (string.IsNullOrWhiteSpace(value))
    {
        return "/";
    }

    return value.StartsWith("/", StringComparison.Ordinal) ? value : "/" + value;
}
