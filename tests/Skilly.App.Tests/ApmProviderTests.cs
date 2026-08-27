using System.IO;
using System.Text.Json;
using Skilly.Infrastructure;
using Skilly.Providers;
using Skilly.Providers.Apm;
using Skilly.Providers.GitHub;
using Skilly.Providers.SkillsCli;
using Skilly.Skills;
using Skilly.State;
using Skilly.ViewModels;

namespace Skilly.App.Tests;

public sealed class ApmProviderFixture : IDisposable
{
    private readonly Dictionary<string, string?> _environment;

    public ApmProviderFixture(Action<SkillyState>? beforeStateSave = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "skilly-apm-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        SourceRoot = Path.Combine(Root, "source");
        StatePath = Path.Combine(Root, "local-app-data", "Skilly", "state.json");
        InvocationsPath = Path.Combine(Root, "invocations.jsonl");
        Directory.CreateDirectory(Home);
        WriteSkill("alpha", "Alpha from APM.");
        WriteSkill("beta", "Beta from APM.");
        var fake = Path.Combine(PackagedAppFixture.FindRepoRoot(), "tests", "FakeApm", "bin", BuildConfiguration, "net10.0-windows", "FakeApm.exe");
        Assert.True(File.Exists(fake), $"FakeApm was not built at '{fake}'.");
        _environment = new Dictionary<string, string?>
        {
            ["USERPROFILE"] = Home,
            ["HOME"] = Home,
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(Home, ".claude"),
            ["FAKE_APM_SOURCE_ROOT"] = SourceRoot,
            ["FAKE_APM_SOURCE"] = Source,
            ["FAKE_APM_INVOCATIONS"] = InvocationsPath,
            ["APM_PROGRESS"] = "never",
        };
        LogDirectory = Path.Combine(Root, "logs");
        Log = new RollingLog(LogDirectory);
        StateStore = new StateStore(Log, StatePath, beforeStateSave);
        Client = new ApmClient(new ProcessRunner(Log, _environment), fake);
        Provider = new ApmProvider(Client, StateStore, Log, Home);
    }

    public const string Source = "https://github.com/acme/apm-library.git";
    public string Root { get; }
    public string Home { get; }
    public string SourceRoot { get; }
    public string StatePath { get; }
    public string InvocationsPath { get; }
    public string LogDirectory { get; }
    public RollingLog Log { get; }
    public StateStore StateStore { get; }
    public ApmClient Client { get; }
    public ApmProvider Provider { get; }
    public string ManifestPath => Path.Combine(Home, ".apm", "apm.yml");
    public string LockPath => Path.Combine(Home, ".apm", "apm.lock.yaml");
    public string Canonical(string name) => Path.Combine(Home, ".agents", "skills", name);
    public string Claude(string name) => Path.Combine(Home, ".claude", "skills", name);

    private static string BuildConfiguration => new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;

    public void WriteSkill(string name, string description, string extra = "")
    {
        var path = Path.Combine(SourceRoot, "skills", name);
        Directory.CreateDirectory(path);
        File.WriteAllText(Path.Combine(path, "SKILL.md"), $"---\nname: {name}\ndescription: {description}\n---\n{extra}");
    }

    public void Set(string name, string? value) => _environment[name] = value;

    public IReadOnlyList<string[]> Invocations()
        => File.ReadAllLines(InvocationsPath).Select(line => JsonSerializer.Deserialize<string[]>(line)!).ToList();

    public void Dispose() => PackagedAppFixture.TryDeleteDirectory(Root);
}

public sealed class ApmProviderTests
{
    [Fact]
    public void All_five_operations_use_global_canonical_topology_and_reconcile_APM_and_Skilly_state()
    {
        using var fixture = new ApmProviderFixture();
        var readiness = fixture.Provider.GetReadiness();
        Assert.True(readiness.IsReady, readiness.Diagnostic);
        Assert.Equal("0.28.0", readiness.Version);

        var inspection = fixture.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        Assert.Equal(["alpha", "beta"], inspection.Skills.Select(skill => skill.Name));
        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(File.Exists(fixture.ManifestPath));

        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        Assert.Equal("apm", record.Provenance.SourceProvider);
        Assert.Equal("0.28.0", record.Provenance.ProviderVersion);
        Assert.Equal("acme/apm-library", record.Provenance.Repository);
        Assert.Contains("microsoft/apm", record.ProviderEvidence);
        Assert.True(Junction.IsJunctionTo(fixture.Claude("alpha"), fixture.Canonical("alpha")));
        Assert.DoesNotContain("claude", File.ReadAllText(fixture.ManifestPath), StringComparison.OrdinalIgnoreCase);

        var stateBefore = File.ReadAllBytes(fixture.StatePath);
        var manifestBefore = File.ReadAllBytes(fixture.ManifestPath);
        var lockBefore = File.ReadAllBytes(fixture.LockPath);
        var hashBefore = PayloadHasher.HashFolder(record.CanonicalPath);
        var current = fixture.Provider.Check(record).ValueOrThrow();
        Assert.Equal(UpdateStatus.Current, current.Status);
        Assert.Equal(stateBefore, File.ReadAllBytes(fixture.StatePath));
        Assert.Equal(manifestBefore, File.ReadAllBytes(fixture.ManifestPath));
        Assert.Equal(lockBefore, File.ReadAllBytes(fixture.LockPath));
        Assert.Equal(hashBefore, PayloadHasher.HashFolder(record.CanonicalPath));

        fixture.WriteSkill("alpha", "Alpha from APM.", "\nChanged upstream.\n");
        var available = fixture.Provider.Check(record).ValueOrThrow();
        Assert.Equal(UpdateStatus.UpdateAvailable, available.Status);
        var state = fixture.StateStore.Load();
        state.Records.Single().LatestCheck = Snapshot(available);
        fixture.StateStore.Save(state);
        var update = fixture.Provider.Update(state.Records.Single()).ValueOrThrow();
        Assert.NotEqual(record.InstalledRevision, update.InstalledRevision);
        Assert.Contains("Changed upstream", File.ReadAllText(Path.Combine(fixture.Canonical("alpha"), "SKILL.md")));
        Assert.True(Junction.IsJunctionTo(fixture.Claude("alpha"), fixture.Canonical("alpha")));

        fixture.Provider.Uninstall(fixture.StateStore.Load().Records.Single()).ValueOrThrow();
        Assert.False(Directory.Exists(fixture.Canonical("alpha")));
        Assert.False(Directory.Exists(fixture.Claude("alpha")));
        Assert.True(File.Exists(fixture.ManifestPath));
        Assert.DoesNotContain("acme/apm-library", File.ReadAllText(fixture.ManifestPath), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.StateStore.Load().Records);

        var invocations = fixture.Invocations();
        Assert.Contains(invocations, args => args.SequenceEqual(["install", "--global", ApmProviderFixture.Source, "--target", "copilot", "--skill", "alpha"]));
        Assert.Contains(invocations, args => args.SequenceEqual(["outdated", "--global"]));
        Assert.Contains(invocations, args => args.SequenceEqual(["update", "--global", "acme/apm-library", "--yes", "--target", "copilot"]));
        Assert.Contains(invocations, args => args.SequenceEqual(["uninstall", "--global", "acme/apm-library"]));
        Assert.DoesNotContain(invocations, args => args.Contains("self-update"));
        Assert.DoesNotContain(invocations, args => args[0] == "uninstall" && args.Contains("--dry-run"));
        Assert.DoesNotContain(invocations, args => args.Contains("claude"));
    }

    [Theory]
    [InlineData("install")]
    [InlineData("update")]
    [InlineData("uninstall")]
    public void Command_failure_and_false_success_restore_provider_content_exposure_and_authority(string operation)
    {
        using var fixture = new ApmProviderFixture();
        var inspection = fixture.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        if (operation == "install")
        {
            fixture.Set("FAKE_APM_FALSE_SUCCESS_OPERATION", "install");
            Assert.False(fixture.Provider.Install(inspection, [inspection.Skills[0]]).Succeeded);
            Assert.False(Directory.Exists(fixture.Canonical("alpha")));
            Assert.Empty(fixture.StateStore.Load().Records);
            Assert.False(File.Exists(fixture.LockPath));
            return;
        }

        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var starting = fixture.StateStore.Load().Records.Single();
        var startingHash = PayloadHasher.HashFolder(starting.CanonicalPath);
        if (operation == "update")
        {
            fixture.WriteSkill("alpha", "Changed.", "\nchanged\n");
            var state = fixture.StateStore.Load();
            state.Records.Single().LatestCheck = Snapshot(fixture.Provider.Check(state.Records.Single()).ValueOrThrow());
            fixture.StateStore.Save(state);
            fixture.Set("FAKE_APM_FALSE_SUCCESS_OPERATION", "update");
            Assert.False(fixture.Provider.Update(state.Records.Single()).Succeeded);
        }
        else
        {
            fixture.Set("FAKE_APM_FALSE_SUCCESS_OPERATION", "uninstall");
            Assert.False(fixture.Provider.Uninstall(starting).Succeeded);
        }
        Assert.Equal(startingHash, PayloadHasher.HashFolder(fixture.Canonical("alpha")));
        Assert.True(Junction.IsJunctionTo(fixture.Claude("alpha"), fixture.Canonical("alpha")));
        Assert.Single(fixture.StateStore.Load().Records);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    [Fact]
    public void Managed_Reinstall_dispatches_to_APM_replaces_package_content_without_merge_and_reconciles_provider_state()
    {
        using var fixture = new ApmProviderFixture();
        using var github = new GitHubProviderFixture();
        using var skills = new SkillsCliProviderFixture();
        var inspection = fixture.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        File.WriteAllText(Path.Combine(record.CanonicalPath, "local-only.txt"), "must not merge");
        fixture.WriteSkill("alpha", "Alpha APM replacement.", "\nprovider replacement\n");
        var dispatcher = new ManagedReinstallDispatcher(github.Provider, skills.Provider, fixture.Provider);
        Assert.True(new InventoryRow(Assert.Single(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()).Entries)).CanManagedReinstall);

        var plan = Assert.IsType<ApmManagedReinstallPlan>(dispatcher.Plan(record).ValueOrThrow());

        Assert.Equal(record.CanonicalPath, plan.ExactPath);
        Assert.Equal([record.CanonicalPath], plan.AffectedPaths);
        Assert.Equal(PayloadHasher.HashFolder(record.CanonicalPath), plan.StartingPayloadHash);
        Assert.NotEqual(record.InstalledRevision, plan.Revision);
        dispatcher.Execute(plan).ValueOrThrow();

        Assert.False(File.Exists(Path.Combine(record.CanonicalPath, "local-only.txt")));
        Assert.Contains("provider replacement", File.ReadAllText(Path.Combine(record.CanonicalPath, "SKILL.md")));
        Assert.True(Junction.IsJunctionTo(record.IntendedClaudeJunctionPath!, record.CanonicalPath));
        var persisted = Assert.Single(fixture.StateStore.Load().Records);
        Assert.Equal(OperationOutcome.Reinstalled, persisted.LastOperationOutcome);
        Assert.Equal(plan.Revision, persisted.InstalledRevision);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "recovery")));
        Assert.Contains(fixture.Invocations(), args => args.SequenceEqual(["uninstall", "--global", "acme/apm-library"]));
        Assert.True(fixture.Invocations().Count(args => args.Length > 0 && args[0] == "install") >= 4);
    }

    [Fact]
    public void APM_Managed_Reinstall_false_success_restores_local_content_manifest_lock_exposure_and_authority()
    {
        using var fixture = new ApmProviderFixture();
        var inspection = fixture.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        var localFile = Path.Combine(record.CanonicalPath, "local-only.txt");
        File.WriteAllText(localFile, "preserve on failure");
        var startingHash = PayloadHasher.HashFolder(record.CanonicalPath);
        var startingManifest = File.ReadAllBytes(fixture.ManifestPath);
        var startingLock = File.ReadAllBytes(fixture.LockPath);
        var plan = fixture.Provider.PlanManagedReinstall(record).ValueOrThrow();
        fixture.Set("FAKE_APM_FALSE_SUCCESS_OPERATION", "install");

        var result = fixture.Provider.ManagedReinstall(plan);

        Assert.False(result.Succeeded);
        Assert.Equal("preserve on failure", File.ReadAllText(localFile));
        Assert.Equal(startingHash, PayloadHasher.HashFolder(record.CanonicalPath));
        Assert.Equal(startingManifest, File.ReadAllBytes(fixture.ManifestPath));
        Assert.Equal(startingLock, File.ReadAllBytes(fixture.LockPath));
        Assert.True(Junction.IsJunctionTo(record.IntendedClaudeJunctionPath!, record.CanonicalPath));
        Assert.Single(fixture.StateStore.Load().Records);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("lock")]
    [InlineData("destination")]
    [InlineData("hash")]
    [InlineData("file")]
    public void Wrong_topology_or_lock_output_is_rejected_and_restored(string mode)
    {
        using var fixture = new ApmProviderFixture();
        var inspection = fixture.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        fixture.Set("FAKE_APM_CLAUDE_COPY", mode == "copy" ? "1" : null);
        fixture.Set("FAKE_APM_BAD_LOCK", mode == "lock" ? "1" : null);
        fixture.Set("FAKE_APM_EXTRA_DEPLOYMENT", mode == "destination" ? "1" : null);
        fixture.Set("FAKE_APM_BAD_HASH", mode == "hash" ? "1" : null);
        fixture.Set("FAKE_APM_EXTRA_FILE", mode == "file" ? "1" : null);
        var result = fixture.Provider.Install(inspection, [inspection.Skills[0]]);
        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(fixture.Canonical("alpha")));
        Assert.False(Directory.Exists(fixture.Claude("alpha")));
        Assert.Empty(fixture.StateStore.Load().Records);
        Assert.False(File.Exists(fixture.LockPath));
    }

    [Fact]
    public void Check_distinguishes_available_source_failure_and_command_or_output_failure_without_mutation()
    {
        using var fixture = new ApmProviderFixture();
        var inspection = fixture.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = fixture.StateStore.Load().Records.Single();
        var before = PayloadHasher.HashFolder(record.CanonicalPath);

        fixture.Set("FAKE_APM_OUTDATED_MODE", "outdated");
        Assert.Equal(UpdateStatus.UpdateAvailable, fixture.Provider.Check(record).ValueOrThrow().Status);
        fixture.Set("FAKE_APM_OUTDATED_MODE", "unknown");
        Assert.Equal(UpdateStatus.SourceUnavailable, fixture.Provider.Check(record).ValueOrThrow().Status);
        fixture.Set("FAKE_APM_OUTDATED_MODE", "rich");
        fixture.WriteSkill("alpha", "Rich table change.", "\nrich\n");
        Assert.Equal(UpdateStatus.UpdateAvailable, fixture.Provider.Check(record).ValueOrThrow().Status);
        fixture.Set("FAKE_APM_OUTDATED_MODE", null);
        fixture.Set("FAKE_APM_BAD_OUTDATED", "1");
        Assert.Contains("unrecognized output", fixture.Provider.Check(record).Diagnostics);
        fixture.Set("FAKE_APM_BAD_OUTDATED", null);
        fixture.Set("FAKE_APM_FAIL_OPERATION", "outdated");
        Assert.Contains("exit code 19", fixture.Provider.Check(record).Diagnostics);
        Assert.Equal(before, PayloadHasher.HashFolder(record.CanonicalPath));
        Assert.True(Junction.IsJunctionTo(record.IntendedClaudeJunctionPath!, record.CanonicalPath));
    }

    [Theory]
    [InlineData("0.27.9", false)]
    [InlineData("0.28.0", true)]
    [InlineData("1.0.0", true)]
    public void Readiness_requires_branded_Microsoft_APM_at_minimum_version(string version, bool ready)
    {
        using var fixture = new ApmProviderFixture();
        fixture.Set("FAKE_APM_VERSION", version);
        Assert.Equal(ready, fixture.Provider.GetReadiness().IsReady);
        fixture.Set("FAKE_APM_WRONG_BRAND", "1");
        Assert.False(fixture.Provider.GetReadiness().IsReady);
    }

    [Fact]
    public void Embedded_credentials_are_rejected_before_source_logging_or_state_persistence()
    {
        using var fixture = new ApmProviderFixture();
        const string secret = "super-secret-token";
        var result = fixture.Provider.Inspect($"https://user:{secret}@github.com/acme/private.git");
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(fixture.StatePath));
        var logs = string.Join('\n', Directory.EnumerateFiles(fixture.LogDirectory).Select(ReadShared));
        Assert.DoesNotContain(secret, logs, StringComparison.Ordinal);
    }

    [Fact]
    public void Interrupted_install_recovers_from_snapshot_without_mutating_retry()
    {
        using var fixture = new ApmProviderFixture();
        var inspection = fixture.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        Assert.False(fixture.Provider.Install(inspection, [inspection.Skills[0]], cancellation.Token).Succeeded);
        Assert.NotNull(fixture.StateStore.Load().PendingOperation);
        var count = fixture.Invocations().Count;
        var recovered = fixture.Provider.RecoverPendingOperation();
        Assert.Equal(Skilly.Providers.GitHub.RecoveryDisposition.Restored, recovered.Disposition);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
        Assert.Equal(count, fixture.Invocations().Count);
        Assert.False(Directory.Exists(fixture.Canonical("alpha")));
    }

    [Fact]
    public void Missing_APM_is_a_provider_scoped_notice_and_does_not_block_inventory_or_other_operations()
    {
        using var fixture = new ApmProviderFixture();
        Directory.CreateDirectory(fixture.Canonical("local"));
        File.WriteAllText(Path.Combine(fixture.Canonical("local"), "SKILL.md"), "---\nname: local\ndescription: Local inventory.\n---\n");
        fixture.Set("FAKE_APM_WRONG_BRAND", "1");
        var viewModel = new Skilly.ViewModels.MainViewModel();
        viewModel.LoadInventory(new InventoryScanner().Scan(fixture.Home));
        viewModel.SetGitHubReadiness(new Skilly.Providers.ProviderReadiness(true, "GitHub", "GitHub ready."));
        viewModel.SetSkillsReadiness(new Skilly.Providers.ProviderReadiness(true, "skills", "skills ready."));
        viewModel.SetApmReadiness(fixture.Provider.GetReadiness());
        Assert.True(viewModel.HasProviderReadinessProblem);
        Assert.Contains("Microsoft microsoft/apm apm-cli provider unavailable", viewModel.ProviderReadiness);
        Assert.Single(viewModel.Rows);
        Assert.True(viewModel.MutationsAllowed);
    }

    [Fact]
    public void Root_single_Skill_package_installs_without_an_invalid_collection_selector()
    {
        using var fixture = new ApmProviderFixture();
        Directory.Delete(Path.Combine(fixture.SourceRoot, "skills", "beta"), true);
        var inspection = fixture.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        var skill = Assert.Single(inspection.Skills);
        Assert.Null(skill.ProviderSelectionName);
        fixture.Provider.Install(inspection, [skill]).ValueOrThrow();
        Assert.Contains(fixture.Invocations(), args => args.SequenceEqual(["install", "--global", ApmProviderFixture.Source, "--target", "copilot"]));
    }

    [Fact]
    public void Manifest_source_drift_blocks_Check_and_mutation_without_changing_content()
    {
        using var fixture = new ApmProviderFixture();
        var inspection = fixture.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = fixture.StateStore.Load().Records.Single();
        var before = PayloadHasher.HashFolder(record.CanonicalPath);
        File.WriteAllText(fixture.ManifestPath, File.ReadAllText(fixture.ManifestPath).Replace("github.com/acme/apm-library", "github.com/evil/acme/apm-library", StringComparison.Ordinal));
        Assert.False(fixture.Provider.Check(record).Succeeded);
        Assert.False(fixture.Provider.Uninstall(record).Succeeded);
        Assert.Equal(before, PayloadHasher.HashFolder(record.CanonicalPath));
        Assert.True(Junction.IsJunctionTo(record.IntendedClaudeJunctionPath!, record.CanonicalPath));
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    private static CheckSnapshot Snapshot(Skilly.Providers.GitHub.CheckResult check) => new()
    {
        Status = check.Status,
        InstalledRevision = check.InstalledRevision,
        AvailableRevision = check.AvailableRevision,
        AvailablePayloadHash = check.AvailablePayloadHash,
        AvailableContentIdentity = check.AvailableContentIdentity,
        CheckedAt = check.CheckedAt,
        Warning = check.Warning,
    };

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
