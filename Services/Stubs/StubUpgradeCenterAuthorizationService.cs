using System.Security.Claims;
using System.Threading;
using System.Threading.Tasks;
using BoldBI.UpgradeCenter.Models;

namespace BoldBI.UpgradeCenter.Services.Stubs
{
    /// <summary>
    /// Stub — always returns an authorized UMS Admin user for development.
    /// Replace with a real implementation that checks Bold BI / UMS roles
    /// from the claims principal.
    /// </summary>
    public class StubUpgradeCenterAuthorizationService : IUpgradeCenterAuthorizationService
    {
        public Task<UpgradeCenterUser> GetCurrentUserAsync(
            ClaimsPrincipal principal,
            CancellationToken ct = default)
        {
            // TODO: read from principal.Claims or call UMS API to check role.
            var user = new UpgradeCenterUser
            {
                DisplayName  = "Jordan M.",
                Initials     = "JM",
                Role         = "UMS Admin",
                IsAuthorized = true
            };
            return Task.FromResult(user);
        }
    }
}
