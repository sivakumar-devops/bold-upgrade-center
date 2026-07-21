using System.Text.Json.Serialization;

namespace BoldBI.UpgradeCenter.Services;

public sealed class UpgradeDatabaseScriptResponse
{
    [JsonPropertyName("version")]
    public string? Version { get; set; }

    [JsonPropertyName("database")]
    public string? Database { get; set; }

    [JsonPropertyName("hasScriptChanges")]
    public bool HasScriptChanges { get; set; }

    [JsonPropertyName("scriptQuery")]
    public string? ScriptQuery { get; set; }
}
