using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BoldBI.UpgradeCenter.Models;

namespace BoldBI.UpgradeCenter.Services
{
    /// <summary>
    /// Read/write store for upgrade job records.
    /// The implementation should be backed by a persistent store (DB, file, etc.)
    /// so job state survives application restarts.
    /// </summary>
    public interface IUpgradeJobStore
    {
        /// <summary>
        /// Saves a new job.  Throws if a job with the same id already exists.
        /// </summary>
        Task SaveJobAsync(UpgradeJob job, CancellationToken ct = default);

        /// <summary>
        /// Updates an existing job record in-place.
        /// </summary>
        Task UpdateJobAsync(UpgradeJob job, CancellationToken ct = default);

        /// <summary>
        /// Returns the job by id, or null if not found.
        /// </summary>
        Task<UpgradeJob?> GetJobAsync(string jobId, CancellationToken ct = default);

        /// <summary>
        /// Returns the most recent job that is currently in a Running or RollingBack
        /// state, or null if none.
        /// </summary>
        Task<UpgradeJob?> GetActiveJobAsync(CancellationToken ct = default);

        /// <summary>
        /// Returns the most recent completed, non-rolled-back upgrade job that
        /// is still within its rollback window. This is the only job the UI
        /// is permitted to roll back — older upgrades are blocked.
        /// </summary>
        Task<UpgradeJob?> GetMostRecentRollbackableJobAsync(CancellationToken ct = default);

        /// <summary>
        /// Returns all jobs, newest first, optionally limited to <paramref name="maxCount"/>.
        /// </summary>
        Task<List<UpgradeJob>> GetAllJobsAsync(int maxCount = 50, CancellationToken ct = default);

        /// <summary>
        /// Returns lightweight history entries suitable for the History tab.
        /// </summary>
        Task<List<UpgradeHistoryEntry>> GetHistoryAsync(int maxCount = 20, CancellationToken ct = default);
    }
}
