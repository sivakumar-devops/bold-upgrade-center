using BoldBI.UpgradeCenter.Services;
using BoldBI.UpgradeCenter.Services.Stubs;
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

// Upgrade Center services — swap Stub* implementations for real ones when ready
builder.Services.AddSingleton<IUpgradeJobStore,                    StubUpgradeJobStore>();
builder.Services.AddScoped<IInstallationInfoProvider,              StubInstallationInfoProvider>();
builder.Services.AddScoped<IUpgradeCenterAuthorizationService,     StubUpgradeCenterAuthorizationService>();
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
