using System.Text.Json;

namespace Bold.UpgradeCenter.Services;

internal static class EcsPlaywrightBootstrap
{
    public static string Script { get; } = Read("bootstrap.cjs") + "\nrunBundledRunner(" +
        JsonSerializer.Serialize(new Dictionary<string, string>
        {
            ["./entrypoint.cjs"] = Read("entrypoint.cjs"),
            ["./shared-state.cjs"] = Read("shared-state.cjs")
        }) + ", process.argv.slice(1));";

    private static string Read(string name)
    {
        using var stream = typeof(EcsPlaywrightBootstrap).Assembly.GetManifestResourceStream("Bold.UpgradeCenter.EcsRunner." + name)
            ?? throw new InvalidOperationException("Embedded ECS validation bootstrap is unavailable.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
