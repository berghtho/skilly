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

public sealed class SkillsCliProviderFixture : IDisposable
{
    private readonly Dictionary<string, string?> _environment;

    public SkillsCliProviderFixture(Action<SkillyState>? beforeStateSave = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "skilly-skills-cli-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        SourceRoot = Path.Combine(Root, "source");
        StatePath = Path.Combine(Root, "local-app-data", "Skilly", "state.json");
        ProviderLockPath = Path.Combine(Root, "provider-state", "skills", ".skill-lock.json");
        InvocationsPath = Path.Combine(Root, "invocations.jsonl");
        Directory.CreateDirectory(Home);
        WriteSkill("alpha", "Alpha from skills provider.");
        WriteSkill("beta", "Beta from skills provider.");

        var fake = Path.Combine(
            PackagedAppFixture.FindRepoRoot(),
            "tests",
            "FakeSkills",
            "bin",
            BuildConfiguration,
            "net10.0-windows",
            "FakeSkills.exe");
        Assert.True(File.Exists(fake), $"FakeSkills was not built at '{fake}'.");
        _environment = new Dictionary<string, string?>
        {
            ["USERPROFILE"] = Home,
            ["HOME"] = Home,
            ["XDG_STATE_HOME"] = Path.Combine(Root, "provider-state"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(Home, ".claude"),
            ["FAKE_SKILLS_SOURCE_ROOT"] = SourceRoot,
            ["FAKE_SKILLS_INVOCATIONS"] = InvocationsPath,
        };
        Log = new RollingLog(Path.Combine(Root, "logs"));
        StateStore = new StateStore(Log, StatePath, beforeStateSave);
        var runner = new ProcessRunner(Log, _environment);
        Client = new SkillsCliClient(runner, fake, fake, fake, fake);
        Provider = new SkillsCliProvider(Client, StateStore, Log, Home, ProviderLockPath);
    }

    public const string Source = "https://example.test/acme/library.git";

    public string Root { get; }
    public string Home { get; }
    public string SourceRoot { get; }
    public string StatePath { get; }
    public string ProviderLockPath { get; }
    public string InvocationsPath { get; }
    public RollingLog Log { get; }
    public StateStore StateStore { get; }
    public SkillsCliClient Client { get; }
    public SkillsCliProvider Provider { get; }

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
        => File.ReadAllLines(InvocationsPath)
            .Select(line => JsonSerializer.Deserialize<string[]>(line)!)
            .ToList();

    public void Dispose() => PackagedAppFixture.TryDeleteDirectory(Root);
}

public sealed class SkillsCliProviderTests
{
    [Fact]
    public void All_five_operations_use_the_exact_pin_and_reconcile_content_lock_state_and_exposure()
    {
        using var fixture = new SkillsCliProviderFixture();
        Assert.True(fixture.Provider.GetReadiness().IsReady);

        var inspection = fixture.Provider.Inspect(SkillsCliProviderFixture.Source).ValueOrThrow();
        Assert.Equal(["alpha", "beta"], inspection.Skills.Select(static skill => skill.Name));
        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(File.Exists(fixture.ProviderLockPath));

        var installed = fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        Assert.Equal(1, installed.SucceededCount);
        Assert.True(Junction.IsJunctionTo(fixture.Claude("alpha"), fixture.Canonical("alpha")));
        var record = Assert.Single(fixture.StateStore.Load().Records);
        Assert.Equal("skills", record.Provenance.SourceProvider);
        Assert.Equal("1.5.23", record.Provenance.ProviderVersion);
        Assert.Equal("alpha", record.Provenance.ProviderSkillName);
        Assert.Contains("skills@1.5.23", record.ProviderEvidence);

        var installedHash = PayloadHasher.HashFolder(record.CanonicalPath);
        var stateBeforeCheck = File.ReadAllBytes(fixture.StatePath);
        var lockBeforeCheck = File.ReadAllBytes(fixture.ProviderLockPath);
        var current = fixture.Provider.Check(record).ValueOrThrow();
        Assert.Equal(UpdateStatus.Current, current.Status);
        Assert.Equal(installedHash, PayloadHasher.HashFolder(record.CanonicalPath));
        Assert.Equal(stateBeforeCheck, File.ReadAllBytes(fixture.StatePath));
        Assert.Equal(lockBeforeCheck, File.ReadAllBytes(fixture.ProviderLockPath));

        fixture.WriteSkill("alpha", "Alpha from skills provider.", "\nChanged upstream.\n");
        var available = fixture.Provider.Check(record).ValueOrThrow();
        Assert.Equal(UpdateStatus.UpdateAvailable, available.Status);
        Assert.Equal(installedHash, PayloadHasher.HashFolder(record.CanonicalPath));
        Assert.Equal(stateBeforeCheck, File.ReadAllBytes(fixture.StatePath));
        Assert.Equal(lockBeforeCheck, File.ReadAllBytes(fixture.ProviderLockPath));
        record = Assert.Single(fixture.StateStore.Load().Records);
        record.LatestCheck = Snapshot(available);
        var state = fixture.StateStore.Load();
        state.Records.Single().LatestCheck = Snapshot(available);
        fixture.StateStore.Save(state);

        var updated = fixture.Provider.Update(state.Records.Single()).ValueOrThrow();
        Assert.Equal(available.AvailableRevision, updated.InstalledRevision);
        Assert.Contains("Changed upstream", File.ReadAllText(Path.Combine(fixture.Canonical("alpha"), "SKILL.md")));
        Assert.True(Junction.IsJunctionTo(fixture.Claude("alpha"), fixture.Canonical("alpha")));
        var persisted = Assert.Single(fixture.StateStore.Load().Records);
        Assert.Equal(OperationOutcome.Updated, persisted.LastOperationOutcome);
        Assert.Equal(UpdateStatus.Current, persisted.LatestCheck!.Status);

        fixture.Provider.Uninstall(persisted).ValueOrThrow();
        Assert.False(Directory.Exists(fixture.Canonical("alpha")));
        Assert.False(Directory.Exists(fixture.Claude("alpha")));
        Assert.Empty(fixture.StateStore.Load().Records);
        Assert.Empty(new SkillsCliLock(fixture.ProviderLockPath).Read());

        var invocations = fixture.Invocations();
        Assert.All(invocations.Where(static args => args.Length > 1), static args =>
        {
            Assert.Equal("--yes", args[0]);
            Assert.Equal("skills@1.5.23", args[1]);
            Assert.DoesNotContain("skills@latest", args);
        });
        var exactAdd = new[]
        {
            "--yes", "skills@1.5.23", "add", SkillsCliProviderFixture.Source, "--global", "--yes", "--skill", "alpha",
            "--agent", "opencode", "--agent", "codex", "--agent", "claude-code", "--agent", "github-copilot",
        };
        Assert.Contains(invocations, args => args.SequenceEqual(exactAdd));
        Assert.DoesNotContain(invocations, static args => args.Length > 2 && args[2] == "check");
        Assert.Contains(invocations, static args => args.Length > 2 && args[2] == "update");
        Assert.Contains(invocations, static args => args.Length > 2 && args[2] == "remove");
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("lock")]
    [InlineData("zero")]
    public void Install_false_success_is_rejected_and_restored(string mode)
    {
        using var fixture = new SkillsCliProviderFixture();
        var inspection = fixture.Provider.Inspect(SkillsCliProviderFixture.Source).ValueOrThrow();
        fixture.Set("FAKE_SKILLS_COPY_FALLBACK", mode == "copy" ? "1" : null);
        fixture.Set("FAKE_SKILLS_LOCK_FAILURE", mode == "lock" ? "1" : null);
        fixture.Set("FAKE_SKILLS_FALSE_SUCCESS_OPERATION", mode == "zero" ? "add" : null);

        var result = fixture.Provider.Install(inspection, [inspection.Skills[0]]);

        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(fixture.Canonical("alpha")));
        Assert.False(Directory.Exists(fixture.Claude("alpha")));
        Assert.Empty(fixture.StateStore.Load().Records);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
        Assert.False(File.Exists(fixture.ProviderLockPath));
    }

    [Fact]
    public void Update_and_uninstall_zero_exit_no_ops_are_not_success()
    {
        using var fixture = new SkillsCliProviderFixture();
        var inspection = fixture.Provider.Inspect(SkillsCliProviderFixture.Source).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        fixture.WriteSkill("alpha", "Alpha changed.", "\nnew\n");
        var state = fixture.StateStore.Load();
        state.Records.Single().LatestCheck = Snapshot(fixture.Provider.Check(state.Records.Single()).ValueOrThrow());
        fixture.StateStore.Save(state);
        var before = PayloadHasher.HashFolder(state.Records.Single().CanonicalPath);
        fixture.Set("FAKE_SKILLS_FALSE_SUCCESS_OPERATION", "update");

        var update = fixture.Provider.Update(state.Records.Single());

        Assert.False(update.Succeeded);
        Assert.Equal(before, PayloadHasher.HashFolder(fixture.Canonical("alpha")));
        Assert.True(Junction.IsJunctionTo(fixture.Claude("alpha"), fixture.Canonical("alpha")));
        Assert.Single(fixture.StateStore.Load().Records);
        fixture.Set("FAKE_SKILLS_FALSE_SUCCESS_OPERATION", "remove");

        var uninstall = fixture.Provider.Uninstall(fixture.StateStore.Load().Records.Single());

        Assert.False(uninstall.Succeeded);
        Assert.Equal(before, PayloadHasher.HashFolder(fixture.Canonical("alpha")));
        Assert.True(Junction.IsJunctionTo(fixture.Claude("alpha"), fixture.Canonical("alpha")));
        Assert.Single(fixture.StateStore.Load().Records);
    }

    [Fact]
    public void Managed_Reinstall_dispatches_to_skills_provider_replaces_without_merge_and_reconciles_every_postcondition()
    {
        using var fixture = new SkillsCliProviderFixture();
        using var github = new GitHubProviderFixture();
        using var apm = new ApmProviderFixture();
        var inspection = fixture.Provider.Inspect(SkillsCliProviderFixture.Source).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        File.WriteAllText(Path.Combine(record.CanonicalPath, "local-only.txt"), "must not merge");
        fixture.WriteSkill("alpha", "Alpha replacement.", "\nprovider replacement\n");
        var dispatcher = new ManagedReinstallDispatcher(github.Provider, fixture.Provider, apm.Provider);
        Assert.True(new InventoryRow(Assert.Single(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()).Entries)).CanManagedReinstall);

        var plan = Assert.IsType<SkillsCliManagedReinstallPlan>(dispatcher.Plan(record).ValueOrThrow());

        Assert.Equal(record.CanonicalPath, plan.ExactPath);
        Assert.Equal(PayloadHasher.HashFolder(record.CanonicalPath), plan.StartingPayloadHash);
        Assert.NotEqual(record.InstalledRevision, plan.Revision);
        dispatcher.Execute(plan).ValueOrThrow();

        Assert.False(File.Exists(Path.Combine(record.CanonicalPath, "local-only.txt")));
        Assert.Contains("provider replacement", File.ReadAllText(Path.Combine(record.CanonicalPath, "SKILL.md")));
        Assert.True(Junction.IsJunctionTo(record.IntendedClaudeJunctionPath!, record.CanonicalPath));
        var persisted = Assert.Single(fixture.StateStore.Load().Records);
        Assert.Equal(OperationOutcome.Reinstalled, persisted.LastOperationOutcome);
        Assert.Equal(plan.Revision, persisted.InstalledRevision);
        Assert.Equal(plan.PayloadHash, PayloadHasher.HashFolder(persisted.CanonicalPath));
        Assert.Null(fixture.StateStore.Load().PendingOperation);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "recovery")));
        var invocations = fixture.Invocations();
        Assert.Contains(invocations, args => args.Length > 3 && args[2] == "remove" && args[3] == "alpha");
        Assert.True(invocations.Count(args => args.Length > 3 && args[2] == "add" && args.Contains("alpha")) >= 3);
    }

    [Fact]
    public void Skills_Managed_Reinstall_false_success_restores_local_content_provider_lock_exposure_and_authority()
    {
        using var fixture = new SkillsCliProviderFixture();
        var inspection = fixture.Provider.Inspect(SkillsCliProviderFixture.Source).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        var localFile = Path.Combine(record.CanonicalPath, "local-only.txt");
        File.WriteAllText(localFile, "preserve on failure");
        var startingHash = PayloadHasher.HashFolder(record.CanonicalPath);
        var startingLock = File.ReadAllBytes(fixture.ProviderLockPath);
        var plan = fixture.Provider.PlanManagedReinstall(record).ValueOrThrow();
        fixture.Set("FAKE_SKILLS_FALSE_SUCCESS_OPERATION", "add");

        var result = fixture.Provider.ManagedReinstall(plan);

        Assert.False(result.Succeeded);
        Assert.Equal("preserve on failure", File.ReadAllText(localFile));
        Assert.Equal(startingHash, PayloadHasher.HashFolder(record.CanonicalPath));
        Assert.Equal(startingLock, File.ReadAllBytes(fixture.ProviderLockPath));
        Assert.True(Junction.IsJunctionTo(record.IntendedClaudeJunctionPath!, record.CanonicalPath));
        Assert.Single(fixture.StateStore.Load().Records);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    [Fact]
    public void Managed_Reinstall_refuses_local_changes_made_after_the_explicit_plan()
    {
        using var fixture = new SkillsCliProviderFixture();
        var inspection = fixture.Provider.Inspect(SkillsCliProviderFixture.Source).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        File.AppendAllText(Path.Combine(record.CanonicalPath, "SKILL.md"), "\nfirst confirmed local edit\n");
        var plan = fixture.Provider.PlanManagedReinstall(record).ValueOrThrow();
        var removesBefore = fixture.Invocations().Count(args => args.Length > 2 && args[2] == "remove");
        File.AppendAllText(Path.Combine(record.CanonicalPath, "SKILL.md"), "\nchanged after confirmation\n");

        var result = fixture.Provider.ManagedReinstall(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("changed after", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("changed after confirmation", File.ReadAllText(Path.Combine(record.CanonicalPath, "SKILL.md")));
        Assert.Equal(removesBefore, fixture.Invocations().Count(args => args.Length > 2 && args[2] == "remove"));
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    [Fact]
    public void Readiness_and_source_failures_are_provider_scoped()
    {
        using var fixture = new SkillsCliProviderFixture();
        fixture.Set("FAKE_SKILLS_NODE_VERSION", "v22.19.0");
        var unsupported = fixture.Provider.GetReadiness();
        Assert.False(unsupported.IsReady);
        Assert.Contains("22.20.0", unsupported.Diagnostic);

        fixture.Set("FAKE_SKILLS_NODE_VERSION", null);
        fixture.Set("FAKE_SKILLS_FAIL_OPERATION", "add");
        var failed = fixture.Provider.Inspect(SkillsCliProviderFixture.Source);
        Assert.False(failed.Succeeded);
        Assert.Contains("exit code 17", failed.Diagnostics);
        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Home, ".agents")));
    }

    [Fact]
    public void Provider_lock_write_and_authority_commit_failures_never_report_success()
    {
        var armed = false;
        var rejected = false;
        using var fixture = new SkillsCliProviderFixture(state =>
        {
            if (armed && !rejected && state.PendingOperation is null && state.Records.Count == 1)
            {
                rejected = true;
                throw new IOException("injected authority commit failure");
            }
        });
        var inspection = fixture.Provider.Inspect(SkillsCliProviderFixture.Source).ValueOrThrow();
        armed = true;

        var result = fixture.Provider.Install(inspection, [inspection.Skills[0]]);

        Assert.False(result.Succeeded);
        Assert.False(Directory.Exists(fixture.Canonical("alpha")));
        Assert.False(Directory.Exists(fixture.Claude("alpha")));
        Assert.Empty(fixture.StateStore.Load().Records);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    [Fact]
    public void Interrupted_provider_install_is_restored_on_restart_without_mutating_retry()
    {
        using var fixture = new SkillsCliProviderFixture();
        var inspection = fixture.Provider.Inspect(SkillsCliProviderFixture.Source).ValueOrThrow();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var interrupted = fixture.Provider.Install(inspection, [inspection.Skills[0]], cancellation.Token);

        Assert.False(interrupted.Succeeded);
        Assert.NotNull(fixture.StateStore.Load().PendingOperation);
        var invocationCount = fixture.Invocations().Count;

        var recovered = fixture.Provider.RecoverPendingOperation();

        Assert.Equal(Skilly.Providers.GitHub.RecoveryDisposition.Restored, recovered.Disposition);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
        Assert.False(Directory.Exists(fixture.Canonical("alpha")));
        Assert.Equal(invocationCount, fixture.Invocations().Count);
    }

    [Fact]
    public void Skills_readiness_problem_does_not_hide_inventory_or_other_provider_state()
    {
        using var fixture = new SkillsCliProviderFixture();
        Directory.CreateDirectory(fixture.Canonical("local"));
        File.WriteAllText(Path.Combine(fixture.Canonical("local"), "SKILL.md"), "---\nname: local\ndescription: Local inventory.\n---\n");
        fixture.Set("FAKE_SKILLS_NODE_VERSION", "v22.19.0");
        var viewModel = new Skilly.ViewModels.MainViewModel();
        viewModel.LoadInventory(new InventoryScanner().Scan(fixture.Home));
        viewModel.SetGitHubReadiness(new Skilly.Providers.ProviderReadiness(true, "GitHub", "GitHub ready."));
        viewModel.SetSkillsReadiness(fixture.Provider.GetReadiness());

        Assert.True(viewModel.HasProviderReadinessProblem);
        Assert.Contains("skills@1.5.23 provider unavailable", viewModel.ProviderReadiness);
        Assert.Single(viewModel.Rows);
        Assert.True(viewModel.MutationsAllowed);
    }

    [Fact]
    public void Check_and_mutation_are_blocked_when_provider_lock_no_longer_matches_Provenance()
    {
        using var fixture = new SkillsCliProviderFixture();
        var inspection = fixture.Provider.Inspect(SkillsCliProviderFixture.Source).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        var installedHash = PayloadHasher.HashFolder(record.CanonicalPath);
        File.WriteAllText(
            fixture.ProviderLockPath,
            File.ReadAllText(fixture.ProviderLockPath).Replace("example.test/acme", "evil.test/other", StringComparison.Ordinal));

        var check = fixture.Provider.Check(record);
        var uninstall = fixture.Provider.Uninstall(record);

        Assert.False(check.Succeeded);
        Assert.False(uninstall.Succeeded);
        Assert.Contains("requested normalized Skill Library", check.Diagnostics);
        Assert.Equal(installedHash, PayloadHasher.HashFolder(record.CanonicalPath));
        Assert.True(Junction.IsJunctionTo(record.IntendedClaudeJunctionPath!, record.CanonicalPath));
        Assert.Single(fixture.StateStore.Load().Records);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    private static CheckSnapshot Snapshot(Skilly.Providers.GitHub.CheckResult check) => new()
    {
        Status = check.Status,
        InstalledRevision = check.InstalledRevision,
        AvailableRevision = check.AvailableRevision,
        AvailableRevisionDate = check.AvailableRevisionDate,
        AvailablePayloadHash = check.AvailablePayloadHash,
        AvailableContentIdentity = check.AvailableContentIdentity,
        CheckedAt = check.CheckedAt,
        Warning = check.Warning,
    };
}
