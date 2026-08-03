using System.Security.Cryptography;

namespace BoldBI.UpgradeCenter.Services;

public interface IPlaywrightReportUploadTokenStore
{
    string Register(string jobId, PlaywrightValidationMode mode);

    bool Validate(string jobId, PlaywrightValidationMode mode, string token);

    void Revoke(string jobId, PlaywrightValidationMode mode);
}

public sealed class InMemoryPlaywrightReportUploadTokenStore : IPlaywrightReportUploadTokenStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<string, string> tokens = new(StringComparer.Ordinal);

    public string Register(string jobId, PlaywrightValidationMode mode)
    {
        var token = Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));
        lock (syncRoot)
        {
            tokens[Key(jobId, mode)] = token;
        }

        return token;
    }

    public bool Validate(string jobId, PlaywrightValidationMode mode, string token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return false;
        }

        lock (syncRoot)
        {
            return tokens.TryGetValue(Key(jobId, mode), out var expected) &&
                CryptographicOperations.FixedTimeEquals(
                    System.Text.Encoding.UTF8.GetBytes(expected),
                    System.Text.Encoding.UTF8.GetBytes(token));
        }
    }

    public void Revoke(string jobId, PlaywrightValidationMode mode)
    {
        lock (syncRoot)
        {
            tokens.Remove(Key(jobId, mode));
        }
    }

    private static string Key(string jobId, PlaywrightValidationMode mode)
    {
        return $"{jobId.Trim()}:{mode}";
    }
}
