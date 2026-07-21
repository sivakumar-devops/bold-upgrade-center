using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BoldBI.UpgradeCenter.Models;

namespace BoldBI.UpgradeCenter.Services
{
    /// <summary>
    /// Core upgrade orchestration service.
    /// All write operations are fire-and-track: they start a background job and
    /// return the job-id immediately so the UI can poll via IUpgradeJobStore.
    /// </summary>
    public interface IUpgradeService
    {
        // ── Catalog ──

        /// <summary>
        /// Returns ordered list of releases available for upgrade from the
        /// currently installed version, newest first.
        /// Business logic: fetch catalog, compute upgrade paths, mark
        /// recommended / blocked entries.
        /// </summary>
        Task<List<Release>> GetAvailableReleasesAsync(
            string installedVersion,
            CancellationToken ct = default);

        // ── Patch validation ──

        /// <summary>
        /// Validates a custom patch image reference entered by the admin.
        /// Must confirm it lives under the allowed registry prefix.
        /// </summary>
        Task<PatchValidationResult> ValidatePatchImageAsync(
            string imageReference,
            CancellationToken ct = default);

        // ── Upgrade execution ──

        /// <summary>
        /// Starts an upgrade job for a standard catalog release.
        /// Returns the new UpgradeJob with status = Running.
        /// </summary>
        Task<UpgradeJob> StartUpgradeAsync(
            string targetVersion,
            string initiatedBy,
            CancellationToken ct = default);

        /// <summary>
        /// Starts an upgrade job for a custom patch image.
        /// Returns the new UpgradeJob with status = Running.
        /// </summary>
        Task<UpgradeJob> StartCustomPatchUpgradeAsync(
            string imageReference,
            string initiatedBy,
            CancellationToken ct = default);

        // ── Rollback ──

        /// <summary>
        /// Triggers a rollback of the specified completed upgrade job.
        /// Returns the new rollback UpgradeJob with status = Running.
        /// </summary>
        Task<UpgradeJob> StartRollbackAsync(
            string upgradeJobId,
            string initiatedBy,
            CancellationToken ct = default);
    }
}
