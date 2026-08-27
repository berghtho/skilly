using System.IO;
using Skilly.Infrastructure;
using Skilly.Providers.Apm;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.App.Tests;

public sealed class LiveApmFactAttribute : FactAttribute
{
    public LiveApmFactAttribute()
    {
        if (!string.Equals(Environment.GetEnvironmentVariable("SKILLY_RUN_LIVE_APM_TESTS"), "1", StringComparison.Ordinal))
            Skip = "Set SKILLY_RUN_LIVE_APM_TESTS=1 and a live source or mutable fixture template after pinning Microsoft apm-cli 0.28.0 and satisfying network/auth prerequisites.";
    }
}

[Trait("Category", "LiveApmPreRelease")]
public sealed class LiveApmPreReleaseTests
{
    [LiveApmFact]
    public void Pinned_Microsoft_APM_supports_the_adapter_contract_in_an_isolated_home()
    {
        var root = Path.Combine(Path.GetTempPath(), "sla-" + Guid.NewGuid().ToString("N")[..8]);
        var home = Path.Combine(root, "home");
        Directory.CreateDirectory(home);
        try
        {
            var fixture = LiveMutableFixture.Prepare(root, "SKILLY_LIVE_APM_SOURCE", "SKILLY_LIVE_APM_FIXTURE_TEMPLATE");
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
            var inspection = provider.Inspect(fixture.Source).ValueOrThrow();
            provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
            var record = Assert.Single(state.Load().Records);
            Assert.True(Junction.IsJunctionTo(record.IntendedClaudeJunctionPath!, record.CanonicalPath));
            Assert.False(File.GetAttributes(record.CanonicalPath).HasFlag(FileAttributes.ReparsePoint));
            var operation = "provider-level Managed Reinstall (source was not a controlled mutable fixture)";
            if (fixture.IsMutable)
            {
                fixture.Advance(Path.GetFileName(record.CanonicalPath));
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
            Assert.NotEqual(UpdateStatus.CheckFailed, provider.Check(record).ValueOrThrow().Status);
            var installedRevision = record.InstalledRevision;
            var sourceSkillPath = record.Provenance.SourceSkillPath;
            var installedFileCount = record.InstalledFileCount;
            provider.Uninstall(record).ValueOrThrow();
            Assert.Empty(state.Load().Records);
            Assert.False(Directory.Exists(record.CanonicalPath));
            Assert.False(Directory.Exists(record.IntendedClaudeJunctionPath));
            Assert.False(Directory.Exists(Path.Combine(root, "state", "recovery")));
            LiveGateEvidence.Write("apm-provider", new
            {
                provider = "microsoft/apm apm-cli",
                version = record.Provenance.ProviderVersion,
                packageIdentity = record.Provenance.Repository,
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
