using System.IO;
using Skilly.Infrastructure;
using Skilly.Providers.SkillsCli;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.App.Tests;

public sealed class LiveSkillsCliFactAttribute : FactAttribute
{
    public LiveSkillsCliFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SKILLY_RUN_LIVE_SKILLS_TESTS"), "1", StringComparison.Ordinal))
        {
            Skip = "Set SKILLY_RUN_LIVE_SKILLS_TESTS=1 and a live source or mutable fixture template after satisfying Node/Git/network/auth prerequisites.";
        }
    }
}

[Trait("Category", "LiveSkillsCliPreRelease")]
public sealed class LiveSkillsCliPreReleaseTests
{
    [LiveSkillsCliFact]
    public void Pinned_provider_supports_inspect_install_read_only_check_and_uninstall_in_an_isolated_home()
    {
        var root = Path.Combine(Path.GetTempPath(), "skilly-live-skills-" + Guid.NewGuid().ToString("N"));
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        try
        {
            var fixture = LiveMutableFixture.Prepare(root, "SKILLY_LIVE_SKILLS_SOURCE", "SKILLY_LIVE_SKILLS_FIXTURE_TEMPLATE");
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
            var inspection = provider.Inspect(fixture.Source).ValueOrThrow();
            var selected = Assert.Single(inspection.Skills.Take(1));
            provider.Install(inspection, [selected]).ValueOrThrow();
            var record = Assert.Single(state.Load().Records);
            Assert.True(Junction.IsJunctionTo(record.IntendedClaudeJunctionPath!, record.CanonicalPath));
            Assert.False(File.GetAttributes(record.CanonicalPath).HasFlag(FileAttributes.ReparsePoint));
            var operation = "provider-level Managed Reinstall (source was not a controlled mutable fixture)";
            if (fixture.IsMutable)
            {
                fixture.Advance(selected.FolderName);
                var check = provider.Check(record).ValueOrThrow();
                Assert.Equal(UpdateStatus.UpdateAvailable, check.Status);
                var persisted = state.Load();
                persisted.Records.Single().LatestCheck = LiveGateEvidence.Snapshot(check);
                state.Save(persisted);
                provider.Update(persisted.Records.Single()).ValueOrThrow();
                operation = "provider-level Update against a copied mutable fixture";
            }
            else
            {
                var plan = provider.PlanManagedReinstall(record).ValueOrThrow();
                provider.ManagedReinstall(plan).ValueOrThrow();
            }
            record = Assert.Single(state.Load().Records);
            Assert.Equal(UpdateStatus.Current, provider.Check(record).ValueOrThrow().Status);
            var installedRevision = record.InstalledRevision;
            var sourceSkillPath = record.Provenance.SourceSkillPath;
            var installedFileCount = record.InstalledFileCount;
            provider.Uninstall(record).ValueOrThrow();
            Assert.Empty(state.Load().Records);
            Assert.False(Directory.Exists(record.CanonicalPath));
            Assert.False(Directory.Exists(record.IntendedClaudeJunctionPath));
            Assert.Empty(new SkillsCliLock(Path.Combine(root, "provider-state", "skills", ".skill-lock.json")).Read());
            Assert.False(Directory.Exists(Path.Combine(root, "state", "recovery")));
            LiveGateEvidence.Write("skills-provider", new
            {
                provider = SkillsCliClient.Package,
                installedRevision,
                sourceSkillPath,
                installedFileCount,
                mutation = operation,
                updateVerified = fixture.IsMutable,
                installTopologyVerified = true,
                providerUninstallVerified = true,
            });
        }
        finally
        {
            PackagedAppFixture.TryDeleteDirectory(root);
        }
    }

}
