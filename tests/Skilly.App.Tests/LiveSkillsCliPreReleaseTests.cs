using System.IO;
using Skilly.Infrastructure;
using Skilly.Providers.SkillsCli;
using Skilly.State;

namespace Skilly.App.Tests;

public sealed class LiveSkillsCliFactAttribute : FactAttribute
{
    public LiveSkillsCliFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SKILLY_RUN_LIVE_SKILLS_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Set SKILLY_RUN_LIVE_SKILLS_TESTS=1 and SKILLY_LIVE_SKILLS_SOURCE after satisfying Node/Git/network/auth prerequisites.";
        }
    }
}

[Trait("Category", "LiveSkillsCliPreRelease")]
public sealed class LiveSkillsCliPreReleaseTests
{
    [LiveSkillsCliFact]
    public void Pinned_provider_supports_inspect_install_read_only_check_and_uninstall_in_an_isolated_home()
    {
        var source = Environment.GetEnvironmentVariable("SKILLY_LIVE_SKILLS_SOURCE");
        Assert.False(string.IsNullOrWhiteSpace(source), "SKILLY_LIVE_SKILLS_SOURCE must identify a provider-supported Skill Library.");
        var root = Path.Combine(Path.GetTempPath(), "skilly-live-skills-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        try
        {
            var environment = new Dictionary<string, string?>
            {
                ["USERPROFILE"] = home,
                ["HOME"] = home,
                ["XDG_STATE_HOME"] = Path.Combine(root, "provider-state"),
                ["CLAUDE_CONFIG_DIR"] = Path.Combine(home, ".claude"),
            };
            var log = new RollingLog(Path.Combine(root, "logs"));
            var state = new StateStore(log, Path.Combine(root, "state", "state.json"));
            var client = new SkillsCliClient(new ProcessRunner(log, environment));
            var provider = new SkillsCliProvider(
                client,
                state,
                log,
                home,
                Path.Combine(root, "provider-state", "skills", ".skill-lock.json"));

            Assert.True(provider.GetReadiness().IsReady, provider.GetReadiness().Diagnostic);
            var inspection = provider.Inspect(source!).ValueOrThrow();
            var selected = Assert.Single(inspection.Skills.Take(1));
            provider.Install(inspection, [selected]).ValueOrThrow();
            var record = Assert.Single(state.Load().Records);
            var check = provider.Check(record).ValueOrThrow();
            Assert.Equal(Skilly.State.UpdateStatus.Current, check.Status);
            SkillsCliClient.RequireExit(client.Update(record.Provenance.ProviderSkillName!), "Pinned live update compatibility probe");
            Assert.Equal(Skilly.State.UpdateStatus.Current, provider.Check(record).ValueOrThrow().Status);
            LiveGateEvidence.Write("skills-provider", new
            {
                provider = SkillsCliClient.Package,
                installedRevision = record.InstalledRevision,
                sourceSkillPath = record.Provenance.SourceSkillPath,
                installedFileCount = record.InstalledFileCount,
            });
            provider.Uninstall(record).ValueOrThrow();
            Assert.Empty(state.Load().Records);
            Assert.False(Directory.Exists(Path.Combine(root, "state", "recovery")));
        }
        finally
        {
            PackagedAppFixture.TryDeleteDirectory(root);
        }
    }
}
