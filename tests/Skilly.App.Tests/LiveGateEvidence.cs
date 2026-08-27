using System.IO;
using System.Text.Json;

namespace Skilly.App.Tests;

internal static class LiveGateEvidence
{
    public static void Write(string gate, object evidence)
    {
        var directory = Environment.GetEnvironmentVariable("SKILLY_LIVE_EVIDENCE_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory))
        {
            return;
        }

        Directory.CreateDirectory(directory);
        var payload = new
        {
            gate,
            observedAt = DateTimeOffset.UtcNow,
            evidence,
        };
        File.WriteAllText(
            Path.Combine(directory, gate + ".json"),
            JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    }
}
