namespace BoldBI.UpgradeCenter.Models.ViewModels
{
    /// <summary>
    /// View-model for the Confirm Upgrade modal (Screen 3).
    /// </summary>
    public class ConfirmUpgradeViewModel
    {
        public UpgradeCenterUser CurrentUser { get; set; } = new();

        public string FromVersion { get; set; } = string.Empty;
        public string ToVersion { get; set; } = string.Empty;

        /// <summary>
        /// When true the modal shows the automatic schema-backup notice.
        /// </summary>
        public bool HasSchemaChanges { get; set; }

        /// <summary>
        /// For custom patches — the raw image reference the admin typed.
        /// Null for standard catalog releases.
        /// </summary>
        public string? CustomImageReference { get; set; }

        public ReleaseType ReleaseType { get; set; } = ReleaseType.Standard;
    }
}
