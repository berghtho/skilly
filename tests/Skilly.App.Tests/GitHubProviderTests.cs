using System.IO;
using System.Text.Json;
using Skilly.Infrastructure;
using Skilly.Providers.GitHub;
using Skilly.Skills;
using Skilly.State;
using Skilly.ViewModels;

namespace Skilly.App.Tests;

public sealed class GitHubProviderFixture : IDisposable
{
    public const string CommitSha = "1234567890abcdef1234567890abcdef12345678";

    public GitHubProviderFixture(string? failPattern = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "skilly-github-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        FixtureRoot = Path.Combine(Root, "github-fixture");
        StatePath = Path.Combine(Root, "local-app-data", "Skilly", "state.json");
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Path.Combine(FixtureRoot, "files", "skills", "alpha", "scripts"));
        Directory.CreateDirectory(Path.Combine(FixtureRoot, "files", "skills", "beta"));

        File.WriteAllText(Path.Combine(FixtureRoot, "repository.json"), "{\"default_branch\":\"main\"}");
        File.WriteAllText(Path.Combine(FixtureRoot, "commit.json"), $"{{\"sha\":\"{CommitSha}\"}}");
        WriteTree(truncated: false);
        File.WriteAllText(
            Path.Combine(FixtureRoot, "files", "skills", "alpha", "SKILL.md"),
            "---\nname: Alpha Display\ndescription: Alpha from GitHub.\n---\n\n# Alpha\n");
        File.WriteAllText(
            Path.Combine(FixtureRoot, "files", "skills", "alpha", "scripts", "run.ps1"),
            "'alpha'\n");
        File.WriteAllText(
            Path.Combine(FixtureRoot, "files", "skills", "beta", "SKILL.md"),
            "---\nname: beta\ndescription: Beta from GitHub.\n---\n\n# Beta\n");

        var fakeGh = Path.Combine(
            PackagedAppFixture.FindRepoRoot(),
            "tests",
            "FakeGh",
            "bin",
            "Debug",
            "net10.0",
            "FakeGh.exe");
        Assert.True(File.Exists(fakeGh), $"FakeGh was not built at '{fakeGh}'.");

        var environment = new Dictionary<string, string?>
        {
            ["FAKE_GH_FIXTURE_ROOT"] = FixtureRoot,
            ["FAKE_GH_STATE_PATH"] = StatePath,
            ["FAKE_GH_FAIL_PATTERN"] = failPattern,
        };
        Log = new RollingLog(Path.Combine(Root, "logs"));
        var client = new GhClient(new ProcessRunner(Log, environment), fakeGh);
        StateStore = new StateStore(Log, StatePath);
        var inspector = new SourceInspector(client, Log);
        var installer = new GitHubInstaller(client, StateStore, Log, Home);
        Provider = new GitHubProvider(client, inspector, installer);
    }

    public string Root { get; }

    public string Home { get; }

    public string FixtureRoot { get; }

    public string StatePath { get; }

    public RollingLog Log { get; }

    public StateStore StateStore { get; }

    public GitHubProvider Provider { get; }

    public GitHubSourceReference Reference
    {
        get
        {
            Assert.True(GitHubSourceReference.TryParse(
                "https://github.com/acme/library/tree/main/skills",
                out var reference,
                out var error), error);
            return reference;
        }
    }

    public void RequirePendingOperationBeforeContent()
        => File.WriteAllText(Path.Combine(FixtureRoot, "require-pending.flag"), string.Empty);

    public void WriteTree(bool truncated)
    {
        var payload = new
        {
            truncated,
            tree = new[]
            {
                new { path = "skills/alpha/SKILL.md", type = "blob" },
                new { path = "skills/alpha/scripts/run.ps1", type = "blob" },
                new { path = "skills/beta/SKILL.md", type = "blob" },
                new { path = "unrelated/SKILL.md", type = "blob" },
            },
        };
        File.WriteAllText(Path.Combine(FixtureRoot, "tree.json"), JsonSerializer.Serialize(payload));
    }

    public string CanonicalPath(string name) => Path.Combine(Home, ".agents", "skills", name);

    public string ClaudePath(string name) => Path.Combine(Home, ".claude", "skills", name);

    public void Dispose() => PackagedAppFixture.TryDeleteDirectory(Root);
}

public sealed class GitHubSourceReferenceTests
{
    [Fact]
    public void Parses_repository_and_tree_urls_without_fuzzy_path_handling()
    {
        Assert.True(GitHubSourceReference.TryParse(
            "https://github.com/acme/library/tree/main/skills/catalog",
            out var tree,
            out _));
        Assert.Equal("acme", tree.Owner);
        Assert.Equal("library", tree.Repository);
        Assert.Equal("main", tree.RequestedRef);
        Assert.Equal("skills/catalog", tree.RequestedPath);

        Assert.True(GitHubSourceReference.TryParse("https://github.com/acme/library", out var repository, out _));
        Assert.Null(repository.RequestedRef);
        Assert.Null(repository.RequestedPath);

        Assert.False(GitHubSourceReference.TryParse("https://example.com/acme/library", out _, out _));
        Assert.False(GitHubSourceReference.TryParse("https://github.com/acme/library/blob/main/SKILL.md", out _, out _));
    }
}

public sealed class GitHubProviderTests
{
    [Fact]
    public void Inspection_enumerates_path_scoped_Source_Skills_without_mutation()
    {
        using var fixture = new GitHubProviderFixture();

        var inspection = fixture.Provider.Inspect(fixture.Reference);

        Assert.Equal(2, inspection.Skills.Count);
        Assert.Equal(GitHubProviderFixture.CommitSha, inspection.Commit.Sha);
        Assert.Equal("main", inspection.RequestedTrackingRule);
        Assert.Collection(
            inspection.Skills,
            alpha =>
            {
                Assert.Equal("skills/alpha", alpha.SkillPath);
                Assert.Equal("alpha", alpha.FolderName);
                Assert.True(alpha.MatchesAlias("alpha"));
                Assert.True(alpha.MatchesAlias("Alpha Display"));
                Assert.False(alpha.MatchesAlias("alpha display"));
                Assert.Equal(2, alpha.FilePaths.Count);
            },
            beta => Assert.Equal("skills/beta", beta.SkillPath));
        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Home, ".agents")));
    }

    [Fact]
    public void Install_records_pending_before_payload_then_verifies_topology_and_authority()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference);
        fixture.RequirePendingOperationBeforeContent();

        var result = fixture.Provider.Install(inspection, inspection.Skills);

        Assert.Equal(2, result.SucceededCount);
        Assert.True(File.Exists(Path.Combine(fixture.CanonicalPath("alpha"), "scripts", "run.ps1")));
        Assert.True(Junction.IsJunctionTo(fixture.ClaudePath("alpha"), fixture.CanonicalPath("alpha")));
        Assert.True(Junction.IsJunctionTo(fixture.ClaudePath("beta"), fixture.CanonicalPath("beta")));

        var state = fixture.StateStore.Load();
        Assert.Null(state.PendingOperation);
        Assert.Equal(2, state.Records.Count);
        Assert.All(state.Records, record =>
        {
            Assert.Equal("github", record.Provenance.SourceProvider);
            Assert.Equal("main", record.Provenance.TrackingRule);
            Assert.Equal(GitHubProviderFixture.CommitSha, record.Provenance.ResolvedCommit);
            Assert.Equal(GitHubProviderFixture.CommitSha, record.InstalledRevision);
            Assert.Equal(PayloadHasher.HashFolder(record.CanonicalPath), record.InstalledPayloadHash);
        });

        var inventory = new InventoryScanner().Scan(fixture.Home, state);
        Assert.Equal(2, inventory.Entries.Count);
        Assert.All(inventory.Entries, entry =>
        {
            Assert.Equal(ManagementStatus.Managed, entry.ManagementStatus);
            Assert.Equal(InstallationHealth.Healthy, entry.Health);
            Assert.Equal(ExposureState.VerifiedJunction, entry.Exposures[Harness.ClaudeCode].State);
        });

        var json = File.ReadAllText(fixture.StatePath);
        Assert.Contains("\"schemaVersion\"", json);
        Assert.Contains("\"records\"", json);
        Assert.Contains("\"provenance\"", json);
        Assert.DoesNotContain("pendingOperation\": null", json);
    }

    [Fact]
    public void Collision_blocks_install_before_state_or_content_changes()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference);
        Directory.CreateDirectory(fixture.CanonicalPath("alpha"));
        File.WriteAllText(Path.Combine(fixture.CanonicalPath("alpha"), "keep.txt"), "keep");

        var error = Assert.Throws<ProviderFailure>(() => fixture.Provider.Install(inspection, [inspection.Skills[0]]));

        Assert.Contains("Collision", error.Message);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.CanonicalPath("alpha"), "keep.txt")));
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public void Existing_real_Claude_folder_is_failure_and_is_never_replaced_by_a_junction()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference);
        Directory.CreateDirectory(fixture.ClaudePath("alpha"));
        File.WriteAllText(Path.Combine(fixture.ClaudePath("alpha"), "keep.txt"), "keep");

        var error = Assert.Throws<ProviderFailure>(() => fixture.Provider.Install(inspection, [inspection.Skills[0]]));

        Assert.Contains("Claude", error.Message);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.ClaudePath("alpha"), "keep.txt")));
        Assert.False(Directory.Exists(fixture.CanonicalPath("alpha")));
        var state = fixture.StateStore.Load();
        Assert.Null(state.PendingOperation);
        Assert.Empty(state.Records);
    }

    [Fact]
    public void Provider_nonzero_exit_is_failure_and_does_not_mutate()
    {
        using var fixture = new GitHubProviderFixture("/commits/");

        var error = Assert.Throws<GhApiException>(() => fixture.Provider.Inspect(fixture.Reference));

        Assert.Contains("exit code 17", error.Message);
        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Home, ".agents")));
    }

    [Fact]
    public void Payload_failure_after_journaling_rolls_back_created_paths_and_clears_pending()
    {
        using var fixture = new GitHubProviderFixture("scripts/run.ps1");
        var inspection = fixture.Provider.Inspect(fixture.Reference);
        fixture.RequirePendingOperationBeforeContent();

        var error = Assert.Throws<ProviderFailure>(() => fixture.Provider.Install(inspection, [inspection.Skills[0]]));

        Assert.Contains("failed", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(Directory.Exists(fixture.CanonicalPath("alpha")));
        Assert.False(Directory.Exists(fixture.ClaudePath("alpha")));
        var state = fixture.StateStore.Load();
        Assert.Null(state.PendingOperation);
        Assert.Empty(state.Records);
    }

    [Fact]
    public void Truncated_tree_is_rejected_instead_of_becoming_partial_discovery()
    {
        using var fixture = new GitHubProviderFixture();
        fixture.WriteTree(truncated: true);

        var error = Assert.Throws<GhApiException>(() => fixture.Provider.Inspect(fixture.Reference));

        Assert.Contains("truncated", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public void Install_is_unavailable_when_nothing_is_selected()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference);

        Assert.Throws<ProviderFailure>(() => fixture.Provider.Install(inspection, []));
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public void Selection_supports_none_and_all_while_invalid_Source_Skills_remain_unselectable()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference);
        var invalid = new SourceSkill("skills/invalid", "invalid", null, null, false, "missing name", ["skills/invalid/SKILL.md"]);
        var viewModel = new SourceInspectionViewModel(inspection with
        {
            Skills = [.. inspection.Skills, invalid],
        });

        Assert.False(viewModel.CanInstall);
        viewModel.SelectAll(true);
        Assert.True(viewModel.CanInstall);
        Assert.Equal(2, viewModel.SelectedCount);
        Assert.False(viewModel.Skills[^1].IsSelected);
        viewModel.SelectAll(false);
        Assert.False(viewModel.CanInstall);
    }
}
