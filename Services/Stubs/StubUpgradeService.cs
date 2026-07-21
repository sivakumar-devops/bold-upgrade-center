using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BoldBI.UpgradeCenter.Models;

namespace BoldBI.UpgradeCenter.Services.Stubs
{
    /// <summary>
    /// Stub upgrade service.  Returns plausible data so all UI flows
    /// render correctly without a real Bold BI back-end.
    ///
    /// Replace each method body with real orchestration logic:
    ///   - GetAvailableReleasesAsync  → call Bold BI update-catalog API
    ///   - ValidatePatchImageAsync    → check image registry / manifest
    ///   - StartUpgradeAsync          → enqueue upgrade pipeline worker
    ///   - StartCustomPatchUpgradeAsync → same, with custom image tag
    ///   - StartRollbackAsync         → enqueue rollback pipeline worker
    /// </summary>
    public class StubUpgradeService : IUpgradeService
    {
        private readonly IUpgradeJobStore _store;
        private readonly IReleaseVersionProvider _releaseVersionProvider;

        public StubUpgradeService(
            IUpgradeJobStore store,
            IReleaseVersionProvider releaseVersionProvider)
        {
            _store = store;
            _releaseVersionProvider = releaseVersionProvider;
        }

        // ── Catalog ───────────────────────────────────────────────────────────

        public async Task<List<Release>> GetAvailableReleasesAsync(
            string installedVersion,
            CancellationToken ct = default)
        {
            var summary = await _releaseVersionProvider.GetReleaseVersionsAsync(installedVersion, ct);
            var recommendedNext = summary.Versions
                .OrderBy(version => ProductVersionComparer.Parse(version.Version), ProductVersionPartsComparer.Instance)
                .FirstOrDefault()?.Version;

            return summary.Versions
                .Select(version => new Release
                {
                    Version = version.Version,
                    ReleasedOn = version.PublishedAt?.UtcDateTime ?? DateTime.MinValue,
                    ReleaseNotesUrl = version.Url,
                    IsRecommendedNext = string.Equals(version.Version, recommendedNext, StringComparison.OrdinalIgnoreCase),
                    RequiresIntermediateUpgrade = false,
                    HasSchemaChanges = false,
                    Type = ReleaseType.Standard
                })
                .ToList();
        }

        // ── Patch validation ──────────────────────────────────────────────────

        public Task<PatchValidationResult> ValidatePatchImageAsync(
            string imageReference,
            CancellationToken ct = default)
        {
            // TODO: Replace with real image registry validation.
            // Logic needed:
            //   1. Confirm imageReference starts with allowed prefix
            //      (e.g. "registry.boldbi.com/patches/").
            //   2. Pull image manifest to verify it exists and is reachable.
            //   3. Parse version / tag from image reference.

            const string AllowedPrefix = "registry.boldbi.com/patches/";

            if (string.IsNullOrWhiteSpace(imageReference))
            {
                return Task.FromResult(new PatchValidationResult
                {
                    IsValid        = false,
                    ImageReference = imageReference ?? string.Empty,
                    ErrorMessage   = "Image reference cannot be empty."
                });
            }

            if (!imageReference.StartsWith(AllowedPrefix, StringComparison.OrdinalIgnoreCase))
            {
                return Task.FromResult(new PatchValidationResult
                {
                    IsValid        = false,
                    ImageReference = imageReference,
                    ErrorMessage   = $"Only images under {AllowedPrefix}* are allowed."
                });
            }

            // Stub: assume valid if prefix matches
            return Task.FromResult(new PatchValidationResult
            {
                IsValid        = true,
                ImageReference = imageReference,
                ParsedVersion  = imageReference.Split('/')[^1]   // last segment as version
            });
        }

        // ── Upgrade execution ─────────────────────────────────────────────────

        public async Task<UpgradeJob> StartUpgradeAsync(
            string targetVersion,
            string initiatedBy,
            CancellationToken ct = default)
        {
            // Single-job constraint: refuse to start a new upgrade if anything
            // (upgrade OR rollback) is already in flight.
            var active = await _store.GetActiveJobAsync(ct);
            if (active != null)
            {
                var what = active.IsRollback ? "rollback" : "upgrade";
                throw new InvalidOperationException(
                    $"An {what} is already in progress (job {active.JobId}). " +
                    "Please wait until it finishes before starting a new upgrade.");
            }

            // TODO: Replace with real pipeline launch.
            // Logic needed:
            //   1. Verify no other job is already running (throw if so).
            //   2. Create UpgradeJob, persist via _store.SaveJobAsync.
            //   3. Enqueue background worker that drives the stages and calls
            //      _store.UpdateJobAsync as each stage transitions.
            //   4. Return the job immediately with Status = Running.

            var job = BuildNewJob(
                fromVersion: "12.3.1",   // TODO: read from IInstallationInfoProvider
                toVersion:   targetVersion,
                initiatedBy: initiatedBy,
                type:        ReleaseType.Standard);

            job.Status = UpgradeJobStatus.Running;

            // Seed the three stages as Pending — background worker will advance them
            job.Stages.Add(new JobStage { Name = "Pre-upgrade",  Status = StageStatus.Pending });
            job.Stages.Add(new JobStage { Name = "Upgrade",      Status = StageStatus.Pending });
            job.Stages.Add(new JobStage { Name = "Post-upgrade", Status = StageStatus.Pending });

            await _store.SaveJobAsync(job, ct);

            // Simulate upgrade progress in background (1 min delay for now)
            _ = SimulateUpgradeProgressAsync(job, ct).ContinueWith(
                async _ => await _store.UpdateJobAsync(job, ct),
                TaskScheduler.Default);

            return job;
        }

        public async Task<UpgradeJob> StartCustomPatchUpgradeAsync(
            string imageReference,
            string initiatedBy,
            CancellationToken ct = default)
        {
            // Single-job constraint (same as standard upgrade).
            var active = await _store.GetActiveJobAsync(ct);
            if (active != null)
            {
                var what = active.IsRollback ? "rollback" : "upgrade";
                throw new InvalidOperationException(
                    $"An {what} is already in progress (job {active.JobId}). " +
                    "Please wait until it finishes before starting a new upgrade.");
            }

            // TODO: Replace with real pipeline launch (same as StartUpgradeAsync
            // but uses imageReference as the deployment artifact instead of a
            // catalog version tag).

            var patchVersion = imageReference.Split('/')[^1];

            var job = BuildNewJob(
                fromVersion: "12.3.1",   // TODO: read from IInstallationInfoProvider
                toVersion:   patchVersion,
                initiatedBy: initiatedBy,
                type:        ReleaseType.CustomPatch);

            job.Status = UpgradeJobStatus.Running;
            job.Stages.Add(new JobStage { Name = "Pre-upgrade",  Status = StageStatus.Pending });
            job.Stages.Add(new JobStage { Name = "Upgrade",      Status = StageStatus.Pending });
            job.Stages.Add(new JobStage { Name = "Post-upgrade", Status = StageStatus.Pending });

            await _store.SaveJobAsync(job, ct);

            // Simulate patch upgrade progress in background (1 min delay for now)
            _ = SimulateUpgradeProgressAsync(job, ct).ContinueWith(
                async _ => await _store.UpdateJobAsync(job, ct),
                TaskScheduler.Default);

            return job;
        }

        // ── Rollback ──────────────────────────────────────────────────────────

        public async Task<UpgradeJob> StartRollbackAsync(
            string upgradeJobId,
            string initiatedBy,
            CancellationToken ct = default)
        {
            // Single-job constraint: refuse to roll back while another job is in flight.
            var active = await _store.GetActiveJobAsync(ct);
            if (active != null)
            {
                var what = active.IsRollback ? "rollback" : "upgrade";
                throw new InvalidOperationException(
                    $"An {what} is already in progress (job {active.JobId}). " +
                    "Please wait until it finishes before starting a rollback.");
            }

            // Rollback is only allowed for the most recent successful upgrade
            // (within the 7-day window). Older upgrades cannot be rolled back.
            var latest = await _store.GetMostRecentRollbackableJobAsync(ct);
            if (latest == null || latest.JobId != upgradeJobId)
            {
                throw new InvalidOperationException(
                    "Rollback is only available for the most recent successful upgrade. " +
                    "Older versions cannot be rolled back directly.");
            }

            // TODO: Replace with real rollback pipeline launch.
            // Logic needed:
            //   1. Load the original upgrade job via _store.GetJobAsync.
            //   2. Verify it is in a Completed or Failed state and still within
            //      the 7-day rollback window.
            //   3. Create a rollback UpgradeJob (swap From/To, set RevertsJobId).
            //   4. Mark the original job's RollbackJobId.
            //   5. Persist both, enqueue rollback worker, return job.

            var originalJob = await _store.GetJobAsync(upgradeJobId, ct);
            if (originalJob == null)
                throw new InvalidOperationException($"Upgrade job '{upgradeJobId}' not found.");

            var rollback = BuildNewJob(
                fromVersion: originalJob.ToVersion,
                toVersion:   originalJob.FromVersion,
                initiatedBy: initiatedBy,
                type:        originalJob.ReleaseType);

            rollback.Status        = UpgradeJobStatus.RollingBack;
            rollback.RevertsJobId  = upgradeJobId;

            // Rollback-specific stage names
            rollback.Stages.Add(new JobStage { Name = "Revert deployment", Status = StageStatus.Pending });
            rollback.Stages.Add(new JobStage { Name = "Restore schema",    Status = StageStatus.Pending });
            rollback.Stages.Add(new JobStage { Name = "Verify health",     Status = StageStatus.Pending });

            // Link original job → rollback
            originalJob.RollbackJobId = rollback.JobId;

            await _store.UpdateJobAsync(originalJob, ct);
            await _store.SaveJobAsync(rollback, ct);

            return rollback;
        }

        // ── Upgrade Progress Simulation ───────────────────────────────────────

        private async Task SimulateUpgradeProgressAsync(
            UpgradeJob job,
            CancellationToken ct = default)
        {
            // TODO: Replace with real upgrade pipeline execution.
            // Logic needed:
            //   1. Call Bold BI upgrade API or orchestration service.
            //   2. Poll or stream stage updates in real time.
            //   3. Update job.Stages with actual status and duration as they progress.
            //   4. Set CompletedAt and final Status (Completed or Failed).

            // Stub: wait 1 minute, then simulate all stages passing
            await Task.Delay(TimeSpan.FromMinutes(1), ct);

            // Mark all stages as passed with simulated durations
            if (job.Stages.Count > 0)
            {
                job.Stages[0].Status = StageStatus.Passed;
                job.Stages[0].Duration = TimeSpan.FromSeconds(20);
            }
            if (job.Stages.Count > 1)
            {
                job.Stages[1].Status = StageStatus.Passed;
                job.Stages[1].Duration = TimeSpan.FromSeconds(20);
            }
            if (job.Stages.Count > 2)
            {
                job.Stages[2].Status = StageStatus.Passed;
                job.Stages[2].Duration = TimeSpan.FromSeconds(20);
            }

            // Mark job as completed
            job.Status = UpgradeJobStatus.Completed;
            job.CompletedAt = DateTime.UtcNow;
        }

        // ── Helpers ───────────────────────────────────────────────────────────

        private static UpgradeJob BuildNewJob(
            string fromVersion, string toVersion, string initiatedBy, ReleaseType type)
        {
            return new UpgradeJob
            {
                JobId       = $"{Guid.NewGuid():N}"[..8] + "-" + $"{Guid.NewGuid():N}"[..4],
                FromVersion = fromVersion,
                ToVersion   = toVersion,
                InitiatedBy = initiatedBy,
                StartedAt   = DateTime.UtcNow,
                ReleaseType = type
            };
        }
    }
}
