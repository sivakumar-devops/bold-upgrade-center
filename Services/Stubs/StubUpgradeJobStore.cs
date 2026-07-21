using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BoldBI.UpgradeCenter.Models;

namespace BoldBI.UpgradeCenter.Services.Stubs
{
    /// <summary>
    /// In-memory stub for the job store.  Data is seeded with representative
    /// history and a completed job so the dashboard and monitoring screens
    /// render meaningful content during development.
    ///
    /// Replace with a persistent implementation (EF Core, Dapper, file-based, etc.)
    /// that survives application restarts.
    /// </summary>
    public class StubUpgradeJobStore : IUpgradeJobStore
    {
        private readonly List<UpgradeJob> _jobs;

        public StubUpgradeJobStore()
        {
            _jobs = BuildSeedData();
        }

        public Task SaveJobAsync(UpgradeJob job, CancellationToken ct = default)
        {
            _jobs.Insert(0, job);
            return Task.CompletedTask;
        }

        public Task UpdateJobAsync(UpgradeJob job, CancellationToken ct = default)
        {
            var idx = _jobs.FindIndex(j => j.JobId == job.JobId);
            if (idx >= 0) _jobs[idx] = job;
            return Task.CompletedTask;
        }

        public Task<UpgradeJob?> GetJobAsync(string jobId, CancellationToken ct = default)
        {
            var job = _jobs.FirstOrDefault(j => j.JobId == jobId);
            return Task.FromResult<UpgradeJob?>(job);
        }

        public Task<UpgradeJob?> GetActiveJobAsync(CancellationToken ct = default)
        {
            var job = _jobs.FirstOrDefault(j =>
                j.Status == UpgradeJobStatus.Running ||
                j.Status == UpgradeJobStatus.RollingBack);
            return Task.FromResult<UpgradeJob?>(job);
        }

        public Task<UpgradeJob?> GetMostRecentRollbackableJobAsync(CancellationToken ct = default)
        {
            // The most recent completed (non-rolled-back) upgrade, still within its
            // 7-day rollback window. This is the only job that can be rolled back.
            var candidate = _jobs
                .Where(j => j.CanRollBack)
                .OrderByDescending(j => j.CompletedAt ?? j.StartedAt)
                .FirstOrDefault();

            return Task.FromResult<UpgradeJob?>(candidate);
        }

        public Task<List<UpgradeJob>> GetAllJobsAsync(int maxCount = 50, CancellationToken ct = default)
        {
            return Task.FromResult(_jobs.Take(maxCount).ToList());
        }

        public Task<List<UpgradeHistoryEntry>> GetHistoryAsync(int maxCount = 20, CancellationToken ct = default)
        {
            var entries = _jobs
                .Take(maxCount)
                .Select(j => new UpgradeHistoryEntry
                {
                    JobId        = j.JobId,
                    Label        = j.IsRollback
                                        ? $"{j.FromVersion} → {j.ToVersion} (rollback)"
                                        : $"{j.FromVersion} → {j.ToVersion}",
                    InitiatedBy  = j.InitiatedBy,
                    Status       = j.Status,
                    CompletedAt  = j.CompletedAt ?? j.StartedAt
                })
                .ToList();

            return Task.FromResult(entries);
        }

        // ─── Seed data ───────────────────────────────────────────────────────

        private static List<UpgradeJob> BuildSeedData()
        {
            var jobs = new List<UpgradeJob>();

            // Completed upgrade  v12.2.0 → v12.3.1
            jobs.Add(new UpgradeJob
            {
                JobId         = "3a9c1d00-bb10",
                FromVersion   = "12.2.0",
                ToVersion     = "12.3.1",
                Status        = UpgradeJobStatus.Completed,
                StartedAt     = new DateTime(2026, 5, 1, 9, 0, 0, DateTimeKind.Utc),
                CompletedAt   = new DateTime(2026, 5, 1, 9, 10, 0, DateTimeKind.Utc),
                InitiatedBy   = "Jordan M.",
                Stages        = new List<JobStage>
                {
                    new JobStage { Name = "Pre-upgrade",       Status = StageStatus.Passed, Duration = TimeSpan.FromSeconds(130) },
                    new JobStage { Name = "Upgrade",           Status = StageStatus.Passed, Duration = TimeSpan.FromSeconds(280) },
                    new JobStage { Name = "Post-upgrade",      Status = StageStatus.Passed, Duration = TimeSpan.FromSeconds(95) }
                }
            });

            // Rolled-back upgrade  v12.1.4 → v12.2.0
            jobs.Add(new UpgradeJob
            {
                JobId         = "2b8d0a11-cc22",
                FromVersion   = "12.1.4",
                ToVersion     = "12.2.0",
                Status        = UpgradeJobStatus.RolledBack,
                StartedAt     = new DateTime(2026, 3, 15, 14, 0, 0, DateTimeKind.Utc),
                CompletedAt   = new DateTime(2026, 3, 15, 14, 9, 0, DateTimeKind.Utc),
                InitiatedBy   = "Priya K.",
                Stages        = new List<JobStage>
                {
                    new JobStage { Name = "Pre-upgrade",       Status = StageStatus.Passed, Duration = TimeSpan.FromSeconds(118) },
                    new JobStage { Name = "Upgrade",           Status = StageStatus.Passed, Duration = TimeSpan.FromSeconds(302) },
                    new JobStage { Name = "Post-upgrade",      Status = StageStatus.Failed, Duration = TimeSpan.FromSeconds(60) }
                }
            });

            // Completed custom-patch
            jobs.Add(new UpgradeJob
            {
                JobId         = "1c7e2b22-dd33",
                FromVersion   = "12.1.0",
                ToVersion     = "12.1.0-hotfix-2",
                Status        = UpgradeJobStatus.Completed,
                ReleaseType   = ReleaseType.CustomPatch,
                StartedAt     = new DateTime(2026, 2, 10, 11, 0, 0, DateTimeKind.Utc),
                CompletedAt   = new DateTime(2026, 2, 10, 11, 7, 0, DateTimeKind.Utc),
                InitiatedBy   = "Jordan M.",
                Stages        = new List<JobStage>
                {
                    new JobStage { Name = "Pre-upgrade",       Status = StageStatus.Passed, Duration = TimeSpan.FromSeconds(90) },
                    new JobStage { Name = "Upgrade",           Status = StageStatus.Passed, Duration = TimeSpan.FromSeconds(200) },
                    new JobStage { Name = "Post-upgrade",      Status = StageStatus.Passed, Duration = TimeSpan.FromSeconds(75) }
                }
            });

            return jobs;
        }
    }
}
