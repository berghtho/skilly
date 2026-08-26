using System.IO;
using Skilly.Infrastructure;
using Skilly.Providers.Apm;
using Skilly.State;

namespace Skilly.App.Tests;

public sealed class LiveApmFactAttribute : FactAttribute
{
    public LiveApmFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SKILLY_RUN_LIVE_APM_TESTS"), "1", StringComparison.Ordinal))
            Skip = "Set SKILLY_RUN_LIVE_APM_TESTS=1 and SKILLY_LIVE_APM_SOURCE after pinning Microsoft apm-cli 0.28.0 and satisfying network/auth prerequisites.";
    }
}

[Trait("Category", "LiveApmPreRelease")]
public sealed class LiveApmPreReleaseTests
{
    [LiveApmFact]
    public void Pinned_Microsoft_APM_supports_the_adapter_contract_in_an_isolated_home()
    {
        var source = Environment.GetEnvironmentVariable("SKILLY_LIVE_APM_SOURCE");
        Assert.False(string.IsNullOrWhiteSpace(source), "SKILLY_LIVE_APM_SOURCE must identify an APM Skill Library.");
        var root = Path.Combine(Path.GetTempPath(), "skilly-live-apm-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        try
        {
            var environment = new Dictionary<string, string?>
            {
                ["USERPROFILE"] = home,
                ["HOME"] = home,
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude"),
                ["APM_PROGRESS"] = "never",
            };
            var log = new RollingLog(Path.Combine(root, "logs"));
            var state = new StateStore(log, Path.Combine(root, "state", "state.json"));
            var client = new ApmClient(new ProcessRunner(log, environment));
            Assert.Equal("0.28.0", client.RequireSupportedVersion());
            var provider = new ApmProvider(client, state, log, home);
            var inspection = provider.Inspect(source!).ValueOrThrow();
            provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
            var record = Assert.Single(state.Load().Records);
            Assert.NotEqual(UpdateStatus.CheckFailed, provider.Check(record).ValueOrThrow().Status);
            ApmClient.RequireExit(client.Update(record.Provenance.Repository), "Pinned APM live affirmative update compatibility probe");
            ApmClient.RequireExit(client.Uninstall(record.Provenance.Repository), "Pinned APM live uninstall compatibility probe");
            Assert.False(Directory.Exists(record.CanonicalPath));
        }
        finally
        {
            PackagedAppFixture.TryDeleteDirectory(root);
        }
    }
}
