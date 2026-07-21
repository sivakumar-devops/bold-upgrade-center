using BoldBI.UpgradeCenter.Services;
using BoldBI.UpgradeCenter.Services.Stubs;
using BoldBI.UpgradeCenter.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

var dataProtectionKeysPath = Path.Combine(builder.Environment.ContentRootPath, "App_Data", "DataProtectionKeys");
Directory.CreateDirectory(dataProtectionKeysPath);

// ── Add services ──────────────────────────────────────────────────────────

builder.Services.AddControllersWithViews();
builder.Services.AddHealthChecks();
builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath))
    .SetApplicationName("BoldBI.UpgradeCenter");
builder.Services.Configure<UpgradeCenterOptions>(builder.Configuration.GetSection("UpgradeCenter"));
builder.Services.AddHttpClient(IdpConfigurationProvider.HttpClientName, client =>
{
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient(RemoteUmsAuthenticationHandler.HttpClientName)
    .ConfigureHttpClient(client => client.Timeout = TimeSpan.FromSeconds(10));
builder.Services.AddSingleton<IUpgradeCenterConfigurationProvider, IdpConfigurationProvider>();
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

// Upgrade Center services — swap Stub* implementations for real ones when ready
builder.Services.AddSingleton<IUpgradeJobStore,                    StubUpgradeJobStore>();
builder.Services.AddScoped<IInstallationInfoProvider,              StubInstallationInfoProvider>();
builder.Services.AddScoped<IUpgradeCenterAuthorizationService,     ClaimsUpgradeCenterAuthorizationService>();
builder.Services.AddScoped<IUpgradeService,                        StubUpgradeService>();

var app = builder.Build();

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
