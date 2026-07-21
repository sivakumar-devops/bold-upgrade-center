using System;
using System.Threading.Tasks;
using BoldBI.UpgradeCenter.Models;
using BoldBI.UpgradeCenter.Models.ViewModels;
using BoldBI.UpgradeCenter.Security;
using BoldBI.UpgradeCenter.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Options;
using Legacy = Bold.UpgradeCenter.Services;

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
        private readonly Legacy.IUpgradeImpactService _upgradeImpactService;
        private readonly IOptions<UpgradeCenterOptions> _options;

        public UpgradeCenterController(
            IUpgradeService upgradeService,
            IUpgradeJobStore jobStore,
            IInstallationInfoProvider installationInfo,
            IUpgradeCenterAuthorizationService authService,
            IUpgradeCenterConfigurationProvider configurationProvider,
            Legacy.IUpgradeImpactService upgradeImpactService,
            IOptions<UpgradeCenterOptions> options)
        {
            _upgradeService  = upgradeService;
            _jobStore        = jobStore;
            _installationInfo = installationInfo;
            _authService     = authService;
            _configurationProvider = configurationProvider;
            _upgradeImpactService = upgradeImpactService;
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
            var history      = await _jobStore.GetHistoryAsync();
            var activeJob    = await _jobStore.GetActiveJobAsync();
            var rollbackable = await _jobStore.GetMostRecentRollbackableJobAsync();

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
            Legacy.UpgradeImpactResult? impact = null;
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
            string? imageRef)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return View("Unauthorized", user);

            // Block new upgrade if any job is already running (single-job constraint).
            var activeJob = await _jobStore.GetActiveJobAsync();
            if (activeJob != null)
            {
                var what = activeJob.IsRollback ? "rollback" : "upgrade";
                TempData["ErrorMessage"] =
                    $"An {what} is already in progress (job {activeJob.JobId}). " +
                    "Please wait until it finishes before starting a new one.";
                return RedirectToAction(nameof(Index));
            }

            UpgradeJob job;

            if (!string.IsNullOrWhiteSpace(imageRef))
            {
                var validation = await _upgradeService.ValidatePatchImageAsync(imageRef);
                if (!validation.IsValid)
                {
                    TempData["PatchError"] = validation.ErrorMessage;
                    return RedirectToAction(nameof(Index));
                }

                job = await _upgradeService.StartCustomPatchUpgradeAsync(imageRef, user.DisplayName);
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

            var job = await _jobStore.GetJobAsync(jobId);
            if (job == null) return NotFound($"Job '{jobId}' not found.");

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

            var job = await _jobStore.GetJobAsync(jobId);
            if (job == null) return NotFound();

            var vm = BuildMonitoringViewModel(user, job);
            await ApplyRollbackEligibilityAsync(job);
            return PartialView("_MonitoringContent", vm);
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
            var activeJob = await _jobStore.GetActiveJobAsync();
            if (activeJob != null)
            {
                var what = activeJob.IsRollback ? "rollback" : "upgrade";
                TempData["ErrorMessage"] =
                    $"An {what} is already in progress (job {activeJob.JobId}). " +
                    "Please wait until it finishes before starting a rollback.";
                return RedirectToAction(nameof(Monitor), new { jobId = jobId });
            }

            // Only allow rollback of the most recent completed upgrade.
            var mostRecent = await _jobStore.GetMostRecentRollbackableJobAsync();
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
        public async Task<IActionResult> ValidatePatch([FromForm] string imageRef)
        {
            var user = await _authService.GetCurrentUserAsync(User);
            if (!user.IsAuthorized) return Forbid();

            var result = await _upgradeService.ValidatePatchImageAsync(imageRef);

            var vm = new PatchValidationViewModel
            {
                IsValid        = result.IsValid,
                ErrorMessage   = result.ErrorMessage,
                ParsedVersion  = result.ParsedVersion,
                ImageReference = result.ImageReference
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
            UpgradeCenterUser user, UpgradeJob job)
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

        /// <summary>
        /// Sets the <c>IsLatestRollbackable</c> flag in ViewData so the
        /// monitoring view can decide whether to render the Rollback button.
        /// Only the most recent completed, in-window upgrade may be rolled back.
        /// </summary>
        private async Task ApplyRollbackEligibilityAsync(UpgradeJob job)
        {
            bool isLatest = false;
            if (!job.IsRollback && job.CanRollBack)
            {
                var latest = await _jobStore.GetMostRecentRollbackableJobAsync();
                isLatest = latest != null && latest.JobId == job.JobId;
            }
            ViewData["IsLatestRollbackable"] = isLatest;
        }
    }
}
