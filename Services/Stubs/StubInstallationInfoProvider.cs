using System;
using System.Threading;
using System.Threading.Tasks;
using BoldBI.UpgradeCenter.Models;

namespace BoldBI.UpgradeCenter.Services.Stubs
{
    /// <summary>
    /// Stub — returns hard-coded installation data for development / UI testing.
    /// Replace with a real implementation that reads from Kubernetes API,
    /// app-settings, or a deployment metadata endpoint.
    /// </summary>
    public class StubInstallationInfoProvider : IInstallationInfoProvider
    {
        public Task<InstallationInfo> GetInstallationInfoAsync(CancellationToken ct = default)
        {
            var info = new InstallationInfo
            {
                InstalledVersion     = "12.3.1",
                DaysInstalled        = 41,
                LatestAvailableVersion = "12.5.0",
                NewerVersionCount    = 3,
                DeploymentTarget     = "On-premise · Kubernetes"
            };
            return Task.FromResult(info);
        }
    }
}
