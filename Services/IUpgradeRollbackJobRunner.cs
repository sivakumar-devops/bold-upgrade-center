namespace BoldBI.UpgradeCenter.Services;

public interface IUpgradeRollbackJobRunner
{
    void Start(string rollbackJobId, string rollbackId);
}
