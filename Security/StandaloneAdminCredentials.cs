using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Bold.UpgradeCenter.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Bold.UpgradeCenter.Security;

public sealed class StandaloneAdminCredentials
{
    private const string CredentialVersionClaim = "upgrade_center:credential_version";
    private readonly PasswordHasher<string> passwordHasher = new();
    private readonly string passwordHash;
    private readonly string credentialVersion;

    public string Username { get; }

    public StandaloneAdminCredentials(IOptions<UpgradeCenterOptions> options, IConfiguration configuration)
    {
        var settings = options.Value.Authentication.Standalone;
        Username = FirstConfiguredValue(settings.Username, configuration["BOLD_ADMIN_USERNAME"])?.Trim() ?? string.Empty;
        var password = FirstConfiguredValue(settings.Password, configuration["BOLD_ADMIN_PASSWORD"]);
        if (string.IsNullOrWhiteSpace(Username) || string.IsNullOrWhiteSpace(password))
        {
            throw new InvalidOperationException("Standalone login requires an administrator username and password. Configure UpgradeCenter:Authentication:Standalone:Username/Password or BOLD_ADMIN_USERNAME/BOLD_ADMIN_PASSWORD through a Secret.");
        }

        if (Username.Length > 256 || password.Length > 4096)
        {
            throw new InvalidOperationException("Standalone administrator username/password exceed the supported login field lengths.");
        }

        if (settings.SessionMinutes is < 5 or > 480)
        {
            throw new InvalidOperationException("Standalone SessionMinutes must be between 5 and 480.");
        }

        passwordHash = passwordHasher.HashPassword(Username, password);
        credentialVersion = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Username + "\0" + password)));
    }

    public bool Validate(string username, string password)
    {
        var passwordValid = passwordHasher.VerifyHashedPassword(Username, passwordHash, password) != PasswordVerificationResult.Failed;
        return string.Equals(Username, username.Trim(), StringComparison.OrdinalIgnoreCase) & passwordValid;
    }

    public ClaimsPrincipal CreatePrincipal()
    {
        var identity = new ClaimsIdentity(
        [
            new Claim(ClaimTypes.NameIdentifier, Username),
            new Claim(ClaimTypes.Name, Username),
            new Claim(ClaimTypes.Role, "Admin"),
            new Claim(UpgradeCenterClaimTypes.UserName, Username),
            new Claim(UpgradeCenterClaimTypes.IsAdmin, "true"),
            new Claim(CredentialVersionClaim, credentialVersion)
        ], UpgradeCenterAuthenticationDefaults.StandaloneScheme);
        return new ClaimsPrincipal(identity);
    }

    public bool IsCurrentPrincipal(ClaimsPrincipal? principal) =>
        principal?.Identity?.IsAuthenticated == true &&
        string.Equals(principal.FindFirstValue(ClaimTypes.NameIdentifier), Username, StringComparison.Ordinal) &&
        string.Equals(principal.FindFirstValue(CredentialVersionClaim), credentialVersion, StringComparison.Ordinal);

    private static string? FirstConfiguredValue(params string?[] values) =>
        values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
}
