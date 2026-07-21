namespace BoldBI.UpgradeCenter.Models.ViewModels
{
    /// <summary>
    /// View-model for the Upgrade Monitoring page (Screens 4, 5 and 6).
    /// The same view is reused for upgrade jobs and rollback jobs; the
    /// Status on the job drives which banner and rail state is rendered.
    /// </summary>
    public class MonitoringViewModel
    {
        public UpgradeCenterUser CurrentUser { get; set; } = new();
        public UpgradeJob Job { get; set; } = new();

        // ── Phase labels for the three-step rail ──
        // Default labels for an upgrade job.  Override for rollback jobs.

        public string Phase1Label { get; set; } = "Pre-upgrade";
        public string Phase2Label { get; set; } = "Upgrade";
        public string Phase3Label { get; set; } = "Post-upgrade";
    }
}
