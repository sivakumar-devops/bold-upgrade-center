using BoldBI.UpgradeCenter.Services;
using BoldBI.UpgradeCenter.Services.Stubs;

var builder = WebApplication.CreateBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole();
builder.Logging.AddDebug();

// ── Add services ──────────────────────────────────────────────────────────

builder.Services.AddControllersWithViews();
builder.Services.AddHealthChecks();

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
