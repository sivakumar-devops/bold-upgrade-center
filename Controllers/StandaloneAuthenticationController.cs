using Bold.UpgradeCenter.Models.ViewModels;
using Bold.UpgradeCenter.Security;
using Bold.UpgradeCenter.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Controllers;

[Route("upgrade-center")]
[ResponseCache(Location = ResponseCacheLocation.None, NoStore = true)]
public sealed class StandaloneAuthenticationController(IOptions<UpgradeCenterOptions> options) : Controller
{
    private bool IsStandalone => UpgradeCenterIntegrationModes.Parse(options.Value.IntegrationMode) == UpgradeCenterIntegrationMode.Standalone;

    [AllowAnonymous]
    [HttpGet("login")]
    public IActionResult Login(string? returnUrl)
    {
        if (!IsStandalone)
        {
            return NotFound();
        }

        var safeReturnUrl = GetSafeReturnUrl(returnUrl);
        return User.Identity?.IsAuthenticated == true
            ? LocalRedirect(safeReturnUrl)
            : View(new StandaloneLoginViewModel { ReturnUrl = safeReturnUrl });
    }

    [AllowAnonymous]
    [HttpPost("login")]
    [ValidateAntiForgeryToken]
    [EnableRateLimiting("standalone-login")]
    public async Task<IActionResult> Login(StandaloneLoginViewModel model)
    {
        if (!IsStandalone)
        {
            return NotFound();
        }

        model.ReturnUrl = GetSafeReturnUrl(model.ReturnUrl);
        if (ModelState.IsValid)
        {
            var credentials = HttpContext.RequestServices.GetRequiredService<StandaloneAdminCredentials>();
            if (credentials.Validate(model.Username, model.Password))
            {
                await HttpContext.SignInAsync(
                    UpgradeCenterAuthenticationDefaults.StandaloneScheme,
                    credentials.CreatePrincipal(),
                    new AuthenticationProperties { IsPersistent = false, AllowRefresh = false });
                return LocalRedirect(model.ReturnUrl);
            }

            ModelState.AddModelError(string.Empty, "The username or password is incorrect.");
        }

        model.Password = string.Empty;
        ModelState.SetModelValue(nameof(model.Password), string.Empty, string.Empty);
        ModelState.Remove(nameof(model.ReturnUrl));
        return View(model);
    }

    [Authorize(Policy = UpgradeCenterPolicies.AdminOnly)]
    [HttpGet("sign-out")]
    public IActionResult Logout() => IsStandalone ? View() : NotFound();

    [Authorize(Policy = UpgradeCenterPolicies.AdminOnly)]
    [HttpPost("sign-out")]
    [ValidateAntiForgeryToken]
    public async Task<IActionResult> SignOutLocal()
    {
        if (!IsStandalone)
        {
            return NotFound();
        }

        await HttpContext.SignOutAsync(UpgradeCenterAuthenticationDefaults.StandaloneScheme);
        return RedirectToAction(nameof(Login));
    }

    private string GetSafeReturnUrl(string? returnUrl)
    {
        var dashboardUrl = Url.Action("Index", "UpgradeCenter") ?? "/upgrade-center";
        if (!Url.IsLocalUrl(returnUrl))
        {
            return dashboardUrl;
        }

        var root = $"{Request.PathBase}/upgrade-center";
        var path = returnUrl!.Split('?', '#')[0].TrimEnd('/');
        if ((!path.Equals(root, StringComparison.OrdinalIgnoreCase) &&
             !path.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase)) ||
            path.Equals(root + "/login", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(root + "/logout", StringComparison.OrdinalIgnoreCase) ||
            path.Equals(root + "/sign-out", StringComparison.OrdinalIgnoreCase))
        {
            return dashboardUrl;
        }

        return returnUrl;
    }
}
