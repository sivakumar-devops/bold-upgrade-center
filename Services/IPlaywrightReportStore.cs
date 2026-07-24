namespace BoldBI.UpgradeCenter.Services;

public interface IPlaywrightReportStore
{
    bool TrySaveFromLogs(string jobId, PlaywrightValidationMode mode, string logs);

    bool Exists(string jobId, PlaywrightValidationMode mode);

    bool TryOpenRead(string jobId, PlaywrightValidationMode mode, out Stream? stream);
}
