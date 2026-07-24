using System;
using System.Threading.Tasks;
using BoldBI.UpgradeCenter.Models;
using BoldBI.UpgradeCenter.Models.ViewModels;
using BoldBI.UpgradeCenter.Security;
using BoldBI.UpgradeCenter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using UiUpgradeJob = BoldBI.UpgradeCenter.Models.UpgradeJob;

namespace BoldBI.UpgradeCenter.Controllers
{
    /// <summary>
    /// Handles all Upgrade Center page navigation and user-initiated actions.
    ///
    /// Route prefix: /upgrade-center
    /// </summary>
    [Authorize(Policy = UpgradeCenterPolicies.AdminOnly)]
    [Route("upgrade-center")]
    public class UpgradeCenterController : Controller
    {
        private readonly IUpgradeService _upgradeService;
        private readonly IUpgradeJobStore _jobStore;
        private readonly IInstallationInfoProvider _installationInfo;
        private readonly IUpgradeCenterAuthorizationService _authService;
        private readonly IUpgradeCenterConfigurationProvider _configurationProvider;
        private readonly IUpgradeImpactService _upgradeImpactService;
        private readonly IUpgradeHistoryStore _historyStore;
        private readonly IUpgradeRollbackStore _rollbackStore;
        private readonly IPlaywrightReportStore _playwrightReportStore;
        private readonly IOptions<UpgradeCenterOptions> _options;

        public UpgradeCenterController(
            IUpgradeService upgradeService,
            IUpgradeJobStore jobStore,
            IInstallationInfoProvider installationInfo,
            IUpgradeCenterAuthorizationService authService,
            IUpgradeCenterConfigurationProvider configurationProvider,
            IUpgradeImpactService upgradeImpactService,
            IUpgradeHistoryStore historyStore,
            IUpgradeRollbackStore rollbackStore,
            IPlaywrightReportStore playwrightReportStore,
            IOptions<UpgradeCenterOptions> options)
        {
            _upgradeService  = upgradeService;
            _jobStore        = jobStore;
            _installationInfo = installationInfo;
            _authService     = authService;
            _configurationProvider = configurationProvider;
            _upgradeImpactService = upgradeImpactService;
            _historyStore = historyStore;
            _rollbackStore = rollbackStore;
            _playwrightReportStore = playwrightReportStore;
            _options = options;
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET /upgrade-center  — Dashboard (Screen 2) or Unauthorized (Screen 1)
        // ─────────────────────────────────────────────────────────────────────

        [HttpGet("")]
        [HttpGet("index")]
        public async Task<IActionResult> Index()
        {
            var user = await _authService.GetCurrentUserAsync(User);

            if (!user.IsAuthorized)
                return View("Unauthorized", user);

            var installation = await _installationInfo.GetInstallationInfoAsync();
            var releases     = await _upgradeService.GetAvailableReleasesAsync(installation.InstalledVersion);
            var history      = await GetHistoryEntriesAsync(HttpContext.RequestAborted);
            var activeJob    = ResolveActiveJob();
            var rollbackable = ResolveMostRecentRollbackableJob();

            var vm = new DashboardViewModel
            {
                CurrentUser        = user,
                Installation       = installation,
                AvailableReleases  = releases,
                History            = history,
                ActiveJob          = activeJob,
                RollbackableJob    = rollbackable
            };

            // Pass an error/success banner message through from a TempData entry.
            if (TempData["ErrorMessage"] is string err)
            {
                ViewData["ErrorMessage"] = err;
            }

            return View("Dashboard", vm);
        }

        [AllowAnonymous]
        [HttpGet("unauthorized")]
        public async Task<IActionResult> UnauthorizedPage()
        {
            var user = await _authService.GetCurrentUserAsync(User);
            var runtimeConfiguration = await _configurationProvider.GetConfigurationAsync(HttpContext.RequestAborted);
            ViewData["ReturnToBoldBiUrl"] = string.IsNullOrWhiteSpace(runtimeConfiguration.IdpBaseUrl)
                ? "/"
                : runtimeConfiguration.IdpBaseUrl;

            return View("Unauthorized", user);
        }

        [HttpGet("logout")]
        public async Task<IActionResult> Logout(CancellationToken cancellationToken)
        {
            var runtimeConfiguration = await _configurationProvider.GetConfigurationAsync(cancellationToken);
            var idpBaseUrl = runtimeConfiguration.IdpBaseUrl?.TrimEnd('/');
            if (string.IsNullOrWhiteSpace(idpBaseUrl))
            {
                return RedirectToAction(nameof(UnauthorizedPage));
            }

            var loginPath = string.IsNullOrWhiteSpace(_options.Value.Authentication.LoginPath)
                ? "/accounts/login"
                : _options.Value.Authentication.LoginPath;
            var logoutUrl = CombineUrl(idpBaseUrl, "accounts/logout");
            var returnUrl = CombineUrl(idpBaseUrl, loginPath);
            Response.Headers.CacheControl = "no-store, no-cache, max-age=0";
            Response.Headers.Pragma = "no-cache";
            return Redirect($"{logoutUrl}?returnUrl={Uri.EscapeDataString(returnUrl)}");
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET /upgrade-center/confirm?version=12.4.0  — Confirm modal (Screen 3)
        // ─────────────────────────────────────────────────────────────────────

        [HttpGet("confirm")]
        public async Task<IActionResult> Confirm(
            string? version,
            string? imageRef)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return View("Unauthorized", user);

            var installation = await _installationInfo.GetInstallationInfoAsync();

            // Determine what we are upgrading to
            Release? release = null;
            if (!string.IsNullOrWhiteSpace(version))
            {
                var releases = await _upgradeService.GetAvailableReleasesAsync(installation.InstalledVersion);
                release = releases.Find(r => r.Version == version);
            }

            var targetVersion = release?.Version ?? version ?? imageRef ?? "—";
            UpgradeImpactResult? impact = null;
            if (string.IsNullOrWhiteSpace(imageRef) &&
                !string.IsNullOrWhiteSpace(targetVersion) &&
                targetVersion != "—")
            {
                impact = await _upgradeImpactService.GetImpactAsync(targetVersion, HttpContext.RequestAborted);
            }

            var vm = new ConfirmUpgradeViewModel
            {
                CurrentUser          = user,
                FromVersion          = installation.InstalledVersion,
                ToVersion            = targetVersion,
                HasSchemaChanges     = impact?.DatabaseSchemaChangesExpected ?? release?.HasSchemaChanges ?? false,
                CustomImageReference = imageRef,
                ReleaseType          = string.IsNullOrWhiteSpace(imageRef)
                                           ? ReleaseType.Standard
                                           : ReleaseType.CustomPatch,
                SchemaImpactKnown    = impact?.SchemaImpactKnown ?? false,
                AffectedTableCount   = impact?.AffectedTableCount ?? 0,
                AffectedTables       = impact?.AffectedTables ?? Array.Empty<string>(),
                DatabaseTypes        = impact?.DatabaseTypes ?? Array.Empty<string>(),
                Warnings             = impact?.Warnings ?? Array.Empty<string>()
            };

            return View("Confirm", vm);
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /upgrade-center/start  — Start an upgrade job
        // ─────────────────────────────────────────────────────────────────────

        [HttpPost("start")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> StartUpgrade(
            string? version,
            string? imageRef,
            string? patchVersion,
            string? imageRepository)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return View("Unauthorized", user);

            // Block new upgrade if any job is already running (single-job constraint).
            var activeJob = ResolveActiveJob();
            if (activeJob != null)
            {
                var what = activeJob.IsRollback ? "rollback" : "upgrade";
                TempData["ErrorMessage"] =
                    $"An {what} is already in progress (job {activeJob.JobId}). " +
                    "Please wait until it finishes before starting a new one.";
                return RedirectToAction(nameof(Index));
            }

            UiUpgradeJob job;

            var requestedPatchVersion = !string.IsNullOrWhiteSpace(patchVersion) ? patchVersion : imageRef;
            if (!string.IsNullOrWhiteSpace(requestedPatchVersion))
            {
                var validation = await _upgradeService.ValidatePatchImageAsync(requestedPatchVersion, imageRepository);
                if (!validation.IsValid)
                {
                    TempData["PatchError"] = validation.ErrorMessage;
                    return RedirectToAction(nameof(Index));
                }

                job = await _upgradeService.StartCustomPatchUpgradeAsync(requestedPatchVersion, imageRepository, user.DisplayName);
            }
            else if (!string.IsNullOrWhiteSpace(version))
            {
                job = await _upgradeService.StartUpgradeAsync(version, user.DisplayName);
            }
            else
            {
                return BadRequest("Either 'version' or 'imageRef' must be supplied.");
            }

            return RedirectToAction(nameof(Monitor), new { jobId = job.JobId });
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET /upgrade-center/monitor/{jobId}  — Monitoring page (Screens 4 / 5 / 6)
        // ─────────────────────────────────────────────────────────────────────

        [HttpGet("monitor/{jobId}")]
        public async Task<IActionResult> Monitor(string jobId)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return View("Unauthorized", user);

            if (!_jobStore.TryGet(jobId, out var backendJob)) return NotFound($"Job '{jobId}' not found.");

            var job = UpgradeJobViewModelMapper.ToViewModel(backendJob);
            ApplyPlaywrightReportLinks(job);
            var vm = BuildMonitoringViewModel(user, job);
            await ApplyRollbackEligibilityAsync(job);
            return View("Monitor", vm);
        }

        // ─────────────────────────────────────────────────────────────────────
        // GET /upgrade-center/monitor/{jobId}/status  — AJAX polling endpoint
        // Returns a partial view so the page can refresh just the job content.
        // ─────────────────────────────────────────────────────────────────────

        [HttpGet("monitor/{jobId}/status")]
        public async Task<IActionResult> MonitorStatus(string jobId)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return Forbid();

            if (!_jobStore.TryGet(jobId, out var backendJob)) return NotFound();

            var job = UpgradeJobViewModelMapper.ToViewModel(backendJob);
            ApplyPlaywrightReportLinks(job);
            var vm = BuildMonitoringViewModel(user, job);
            await ApplyRollbackEligibilityAsync(job);
            return PartialView("_MonitoringContent", vm);
        }

        [HttpGet("reports/{jobId}/{stage}/html")]
        public async Task<IActionResult> PlaywrightHtmlReport(string jobId, string stage)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return Forbid();

            if (!_jobStore.TryGet(jobId, out _))
            {
                return NotFound();
            }

            if (!TryParsePlaywrightStage(stage, out var mode))
            {
                return NotFound();
            }

            if (!_playwrightReportStore.TryOpenRead(jobId, mode, out var stream) || stream is null)
            {
                return NotFound();
            }

            Response.Headers["X-Content-Type-Options"] = "nosniff";
            return File(stream, "text/html; charset=utf-8");
        }

        [HttpGet("rollback/{jobId}/confirm")]
        public async Task<IActionResult> ConfirmRollback(string jobId)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return View("Unauthorized", user);

            var activeJob = ResolveActiveJob();
            if (activeJob != null)
            {
                var what = activeJob.IsRollback ? "rollback" : "upgrade";
                TempData["ErrorMessage"] =
                    $"An {what} is already in progress (job {activeJob.JobId}). " +
                    "Please wait until it finishes before starting a rollback.";
                return RedirectToAction(nameof(Monitor), new { jobId });
            }

            var rollbackEntry = _rollbackStore.GetLatest();
            if (rollbackEntry is null ||
                !rollbackEntry.CanExecute ||
                !string.Equals(rollbackEntry.UpgradeJobId, jobId, StringComparison.OrdinalIgnoreCase))
            {
                TempData["ErrorMessage"] =
                    "Rollback is only available for the most recent successful upgrade. " +
                    "Older versions cannot be rolled back directly.";
                return RedirectToAction(nameof(Monitor), new { jobId });
            }

            if (!_jobStore.TryGet(jobId, out var backendJob))
            {
                return NotFound($"Job '{jobId}' not found.");
            }

            var rollbackFromVersion = !string.IsNullOrWhiteSpace(rollbackEntry.TargetVersion)
                ? rollbackEntry.TargetVersion
                : backendJob.TargetVersion;
            var restoreToVersion = !string.IsNullOrWhiteSpace(rollbackEntry.PreviousVersion)
                ? rollbackEntry.PreviousVersion
                : backendJob.CurrentVersion;

            var vm = new ConfirmRollbackViewModel
            {
                CurrentUser = user,
                JobId = jobId,
                RollbackId = rollbackEntry.Id,
                RollbackFromVersion = FormatVersionValue(rollbackFromVersion),
                RestoreToVersion = FormatVersionValue(restoreToVersion),
                RollbackPointCreatedAt = rollbackEntry.CreatedAt.UtcDateTime,
                BackupDatabaseCount = rollbackEntry.BackupResult.BackupMappings.Count(mapping => mapping.Succeeded),
                KubernetesImageCount = rollbackEntry.KubernetesResult?.PreviousImages.Count ?? 0,
                Warnings =
                [
                    $"Bold BI will be restored from v{FormatVersionValue(rollbackFromVersion)} to v{FormatVersionValue(restoreToVersion)}.",
                    "Database tables captured in the rollback point will be restored to their pre-upgrade state.",
                    "Recorded Kubernetes deployment images will be reverted and rollout health will be validated.",
                    "Do not stop the Upgrade Center pod while rollback is restoring database tables or deployment images."
                ]
            };

            return View("ConfirmRollback", vm);
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /upgrade-center/rollback/{jobId}  — Start rollback
        // ─────────────────────────────────────────────────────────────────────

        [HttpPost("rollback/{jobId}")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Rollback(string jobId)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return View("Unauthorized", user);

            // Block new rollback if any job is already running (single-job constraint).
            var activeJob = ResolveActiveJob();
            if (activeJob != null)
            {
                var what = activeJob.IsRollback ? "rollback" : "upgrade";
                TempData["ErrorMessage"] =
                    $"An {what} is already in progress (job {activeJob.JobId}). " +
                    "Please wait until it finishes before starting a rollback.";
                return RedirectToAction(nameof(Monitor), new { jobId = jobId });
            }

            // Only allow rollback of the most recent completed upgrade.
            var mostRecent = ResolveMostRecentRollbackableJob();
            if (mostRecent == null || mostRecent.JobId != jobId)
            {
                TempData["ErrorMessage"] =
                    "Rollback is only available for the most recent successful upgrade. " +
                    "Older versions cannot be rolled back directly.";
                return RedirectToAction(nameof(Monitor), new { jobId = jobId });
            }

            var rollbackJob = await _upgradeService.StartRollbackAsync(jobId, user.DisplayName);
            return RedirectToAction(nameof(Monitor), new { jobId = rollbackJob.JobId });
        }

        // ─────────────────────────────────────────────────────────────────────
        // POST /upgrade-center/validate-patch  — AJAX patch image validation
        // ─────────────────────────────────────────────────────────────────────

        [HttpPost("validate-patch")]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> ValidatePatch(
            [FromForm] string patchVersion,
            [FromForm] string? imageRepository,
            [FromForm] string? imageRef)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return Forbid();

            var requestedPatchVersion = !string.IsNullOrWhiteSpace(patchVersion) ? patchVersion : imageRef ?? string.Empty;
            var result = await _upgradeService.ValidatePatchImageAsync(requestedPatchVersion, imageRepository);

            var vm = new PatchValidationViewModel
            {
                IsValid        = result.IsValid,
                ErrorMessage   = result.ErrorMessage,
                ParsedVersion  = result.ParsedVersion,
                ImageRepository = result.ImageRepository,
                ImageReference = result.ImageReference,
                Images = result.Images
            };

            return PartialView("_PatchValidationResult", vm);
        }

        // ─────────────────────────────────────────────────────────────────────
        // Private helpers
        // ─────────────────────────────────────────────────────────────────────

        private static string CombineUrl(string baseUrl, string relativePath)
        {
            return $"{baseUrl.TrimEnd('/')}/{relativePath.TrimStart('/')}";
        }

        private static MonitoringViewModel BuildMonitoringViewModel(
            UpgradeCenterUser user, UiUpgradeJob job)
        {
            var vm = new MonitoringViewModel
            {
                CurrentUser = user,
                Job         = job
            };

            if (job.IsRollback)
            {
                vm.Phase1Label = "Revert deployment";
                vm.Phase2Label = "Restore schema";
                vm.Phase3Label = "Verify health";
            }

            return vm;
        }

        private void ApplyPlaywrightReportLinks(UiUpgradeJob job)
        {
            foreach (var stage in job.Stages)
            {
                if (string.Equals(stage.Name, "Pre-Upgrade Validation", StringComparison.OrdinalIgnoreCase) &&
                    _playwrightReportStore.Exists(job.JobId, PlaywrightValidationMode.Pre))
                {
                    stage.HtmlReportUrl = Url.Action(nameof(PlaywrightHtmlReport), "UpgradeCenter", new { jobId = job.JobId, stage = "pre-upgrade" });
                }
                else if (string.Equals(stage.Name, "Post-Upgrade Validation", StringComparison.OrdinalIgnoreCase) &&
                         _playwrightReportStore.Exists(job.JobId, PlaywrightValidationMode.Post))
                {
                    stage.HtmlReportUrl = Url.Action(nameof(PlaywrightHtmlReport), "UpgradeCenter", new { jobId = job.JobId, stage = "post-upgrade" });
                }
            }
        }

        private static bool TryParsePlaywrightStage(string stage, out PlaywrightValidationMode mode)
        {
            if (string.Equals(stage, "pre-upgrade", StringComparison.OrdinalIgnoreCase))
            {
                mode = PlaywrightValidationMode.Pre;
                return true;
            }

            if (string.Equals(stage, "post-upgrade", StringComparison.OrdinalIgnoreCase))
            {
                mode = PlaywrightValidationMode.Post;
                return true;
            }

            mode = PlaywrightValidationMode.Pre;
            return false;
        }

        /// <summary>
        /// Sets the <c>IsLatestRollbackable</c> flag in ViewData so the
        /// monitoring view can decide whether to render the Rollback button.
        /// Only the most recent completed, in-window upgrade may be rolled back.
        /// </summary>
        private Task ApplyRollbackEligibilityAsync(UiUpgradeJob job)
        {
            bool isLatest = false;
            if (!job.IsRollback && job.CanRollBack)
            {
                var latest = _rollbackStore.GetLatest();
                isLatest = latest?.CanExecute == true &&
                    string.Equals(latest.UpgradeJobId, job.JobId, StringComparison.OrdinalIgnoreCase);
            }
            ViewData["IsLatestRollbackable"] = isLatest;
            return Task.CompletedTask;
        }

        private UiUpgradeJob? ResolveActiveJob()
        {
            return _jobStore.TryGetLatestActive(out var activeJob)
                ? UpgradeJobViewModelMapper.ToViewModel(activeJob)
                : null;
        }

        private UiUpgradeJob? ResolveMostRecentRollbackableJob()
        {
            var latest = _rollbackStore.GetLatest();
            if (latest is null || !latest.CanExecute || string.IsNullOrWhiteSpace(latest.UpgradeJobId))
            {
                return null;
            }

            if (!_jobStore.TryGet(latest.UpgradeJobId, out var job))
            {
                return null;
            }

            return UpgradeJobViewModelMapper.ToViewModel(job);
        }

        private async Task<List<UpgradeHistoryEntry>> GetHistoryEntriesAsync(CancellationToken cancellationToken)
        {
            var records = await _historyStore.ListAsync(20, cancellationToken);
            return records
                .Select(record => new UpgradeHistoryEntry
                {
                    JobId = record.JobId,
                    Label = $"{FormatVersion(record.PreviousVersion)} \u2192 {FormatVersion(record.TargetVersion)}",
                    InitiatedBy = string.IsNullOrWhiteSpace(record.InitiatedByName)
                        ? record.InitiatedByUserId
                        : record.InitiatedByName,
                    Status = UpgradeJobViewModelMapper.ToViewModelStatus(record.Status, record.OperationType),
                    CompletedAt = (record.CompletedAt ?? record.StartedAt).UtcDateTime
                })
                .ToList();
        }

        private static string FormatVersion(string? version)
        {
            return string.IsNullOrWhiteSpace(version) ? "Unknown" : $"v{version.TrimStart('v', 'V')}";
        }

        private static string FormatVersionValue(string? version)
        {
            return string.IsNullOrWhiteSpace(version) ? "Unknown" : version.TrimStart('v', 'V');
        }
    }
}
