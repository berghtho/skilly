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
    public const string LaterCommitSha = "abcdef1234567890abcdef1234567890abcdef12";

    private readonly Dictionary<string, string?> _environment;

    public GitHubProviderFixture(string? failPattern = null)
    {
        Root = Path.Combine(Path.GetTempPath(), "skilly-github-" + Guid.NewGuid().ToString("N"));
        Home = Path.Combine(Root, "home");
        FixtureRoot = Path.Combine(Root, "github-fixture");
        StatePath = Path.Combine(Root, "local-app-data", "Skilly", "state.json");
        Directory.CreateDirectory(Home);
        Directory.CreateDirectory(Path.Combine(FixtureRoot, "files", "skills", "alpha", "scripts"));
        Directory.CreateDirectory(Path.Combine(FixtureRoot, "files", "skills", "beta"));
        Directory.CreateDirectory(Path.Combine(FixtureRoot, "files", "unrelated"));

        File.WriteAllText(
            Path.Combine(FixtureRoot, "files", "SKILL.md"),
            "---\nname: Library Display\ndescription: Repository-root Skill.\n---\n");
        File.WriteAllText(
            Path.Combine(FixtureRoot, "files", "unrelated", "SKILL.md"),
            "---\nname: Unrelated Display\ndescription: Outside the requested subtree.\n---\n");

        File.WriteAllText(Path.Combine(FixtureRoot, "repository.json"), "{\"default_branch\":\"main\"}");
        File.WriteAllText(Path.Combine(FixtureRoot, "commit.json"), $"{{\"sha\":\"{CommitSha}\"}}");
        File.WriteAllText(Path.Combine(FixtureRoot, "heads.json"), "[]");
        File.WriteAllText(Path.Combine(FixtureRoot, "tags.json"), "[]");
        File.WriteAllText(
            Path.Combine(FixtureRoot, "skill-commit.json"),
            "[{\"commit\":{\"committer\":{\"date\":\"2026-01-02T03:04:05Z\"}}}]");
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

        _environment = new Dictionary<string, string?>
        {
            ["FAKE_GH_FIXTURE_ROOT"] = FixtureRoot,
            ["FAKE_GH_STATE_PATH"] = StatePath,
            ["FAKE_GH_FAIL_PATTERN"] = failPattern,
        };
        Log = new RollingLog(Path.Combine(Root, "logs"));
        var client = new GhClient(new ProcessRunner(Log, _environment), fakeGh);
        StateStore = new StateStore(Log, StatePath);
        var inspector = new SourceInspector(client, Log);
        var installer = new GitHubInstaller(client, StateStore, Log, Home);
        var checker = new GitHubChecker(client);
        Provider = new GitHubProvider(client, inspector, installer, checker, new GitHubUpdater(checker, StateStore, Log));
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

    public void WriteTree(bool truncated, bool includeAlpha = true)
    {
        var entries = new List<object>
        {
            new { path = "skills/beta/SKILL.md", type = "blob" },
            new { path = "unrelated/SKILL.md", type = "blob" },
            new { path = "SKILL.md", type = "blob" },
        };
        if (includeAlpha)
        {
            entries.Insert(0, new { path = "skills/alpha/scripts/run.ps1", type = "blob" });
            entries.Insert(0, new { path = "skills/alpha/SKILL.md", type = "blob" });
        }

        var payload = new
        {
            truncated,
            tree = entries,
        };
        File.WriteAllText(Path.Combine(FixtureRoot, "tree.json"), JsonSerializer.Serialize(payload));
    }

    public void SetCommit(string sha)
        => File.WriteAllText(Path.Combine(FixtureRoot, "commit.json"), $"{{\"sha\":\"{sha}\"}}");

    public void SetBranch(string name)
        => File.WriteAllText(Path.Combine(FixtureRoot, "heads.json"), $"[{{\"ref\":\"refs/heads/{name}\"}}]");

    public void SetTag(string name)
        => File.WriteAllText(Path.Combine(FixtureRoot, "tags.json"), $"[{{\"ref\":\"refs/tags/{name}\"}}]");

    public void ClearBranches()
        => File.WriteAllText(Path.Combine(FixtureRoot, "heads.json"), "[]");

    public void FailRequestsContaining(string? pattern) => _environment["FAKE_GH_FAIL_PATTERN"] = pattern;

    public void ReturnNotFoundFor(string? pattern) => _environment["FAKE_GH_NOT_FOUND_PATTERN"] = pattern;

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

        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();

        Assert.Equal(2, inspection.Skills.Count);
        Assert.Equal(GitHubProviderFixture.CommitSha, inspection.Commit.Sha);
        Assert.Equal("main", inspection.RequestedTrackingRule);
        Assert.Collection(
            inspection.Skills,
            alpha =>
            {
                Assert.Equal("alpha", alpha.SkillPath);
                Assert.Equal("skills/alpha", alpha.RepositoryPath);
                Assert.Equal("alpha", alpha.FolderName);
                Assert.True(alpha.MatchesAlias("alpha"));
                Assert.True(alpha.MatchesAlias("Alpha Display"));
                Assert.False(alpha.MatchesAlias("alpha display"));
                Assert.Equal(2, alpha.FilePaths.Count);
            },
            beta => Assert.Equal("beta", beta.SkillPath));
        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Home, ".agents")));
    }

    [Fact]
    public void Inspection_supports_direct_Skill_folder_and_repository_root_Skill()
    {
        using var fixture = new GitHubProviderFixture();
        Assert.True(GitHubSourceReference.TryParse(
            "https://github.com/acme/library/tree/main/skills/alpha",
            out var directReference,
            out var directError), directError);

        var direct = fixture.Provider.Inspect(directReference).ValueOrThrow();
        var directSkill = Assert.Single(direct.Skills);
        Assert.Equal(".", directSkill.SkillPath);
        Assert.Equal("skills/alpha", directSkill.RepositoryPath);
        Assert.Equal("alpha", directSkill.FolderName);

        Assert.True(GitHubSourceReference.TryParse(
            "https://github.com/acme/library",
            out var rootReference,
            out var rootError), rootError);
        var root = fixture.Provider.Inspect(rootReference).ValueOrThrow();
        var rootSkill = Assert.Single(root.Skills, static skill => skill.RepositoryPath.Length == 0);
        Assert.Equal(".", rootSkill.SkillPath);
        Assert.Equal("library", rootSkill.FolderName);
        Assert.Equal("Library Display", rootSkill.DeclaredName);
    }

    [Fact]
    public void Inspection_distinguishes_nondefault_branches_tags_and_exact_commits()
    {
        using var fixture = new GitHubProviderFixture();
        fixture.SetBranch("release");
        Assert.True(GitHubSourceReference.TryParse(
            "https://github.com/acme/library/tree/release/skills",
            out var branchReference,
            out var branchError), branchError);
        Assert.Equal(TrackingRuleKind.Branch, fixture.Provider.Inspect(branchReference).ValueOrThrow().TrackingRuleKind);

        fixture.ClearBranches();
        fixture.SetTag("v1.0.0");
        Assert.True(GitHubSourceReference.TryParse(
            "https://github.com/acme/library/tree/v1.0.0/skills",
            out var tagReference,
            out var tagError), tagError);
        Assert.Equal(TrackingRuleKind.Tag, fixture.Provider.Inspect(tagReference).ValueOrThrow().TrackingRuleKind);

        Assert.True(GitHubSourceReference.TryParse(
            $"https://github.com/acme/library/tree/{GitHubProviderFixture.CommitSha}/skills",
            out var commitReference,
            out var commitError), commitError);
        Assert.Equal(TrackingRuleKind.Commit, fixture.Provider.Inspect(commitReference).ValueOrThrow().TrackingRuleKind);
    }

    [Fact]
    public void Branch_advance_with_unchanged_selected_content_remains_Current_without_mutation()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        var installedHash = PayloadHasher.HashFolder(record.CanonicalPath);
        var stateBefore = File.ReadAllText(fixture.StatePath);
        fixture.SetCommit(GitHubProviderFixture.LaterCommitSha);

        var check = fixture.Provider.Check(record).ValueOrThrow();

        Assert.Equal(UpdateStatus.Current, check.Status);
        Assert.Equal(GitHubProviderFixture.LaterCommitSha, check.AvailableRevision);
        Assert.Equal(installedHash, PayloadHasher.HashFolder(record.CanonicalPath));
        Assert.Equal(stateBefore, File.ReadAllText(fixture.StatePath));
    }

    [Fact]
    public void Branch_advance_with_changed_selected_content_reports_Update_Available_without_mutation()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        var installedHash = PayloadHasher.HashFolder(record.CanonicalPath);
        fixture.SetCommit(GitHubProviderFixture.LaterCommitSha);
        File.AppendAllText(
            Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha", "SKILL.md"),
            "\nChanged upstream.\n");

        var check = fixture.Provider.Check(record).ValueOrThrow();

        Assert.Equal(UpdateStatus.UpdateAvailable, check.Status);
        Assert.NotEqual(installedHash, check.AvailablePayloadHash);
        Assert.Equal(installedHash, PayloadHasher.HashFolder(record.CanonicalPath));
    }

    [Fact]
    public void Exact_commit_tracking_reports_Pinned()
    {
        using var fixture = new GitHubProviderFixture();
        Assert.True(GitHubSourceReference.TryParse(
            $"https://github.com/acme/library/tree/{GitHubProviderFixture.CommitSha}/skills",
            out var reference,
            out var parseError), parseError);
        var inspection = fixture.Provider.Inspect(reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);

        var check = fixture.Provider.Check(record).ValueOrThrow();

        Assert.Equal(UpdateStatus.Pinned, check.Status);
    }

    [Fact]
    public void Moved_tag_remains_Pinned_with_warning_and_missing_pinned_content_is_Source_Unavailable()
    {
        using var fixture = new GitHubProviderFixture();
        fixture.SetTag("v1.0.0");
        Assert.True(GitHubSourceReference.TryParse(
            "https://github.com/acme/library/tree/v1.0.0/skills",
            out var reference,
            out var parseError), parseError);
        var inspection = fixture.Provider.Inspect(reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        var installedHash = PayloadHasher.HashFolder(record.CanonicalPath);
        fixture.SetCommit(GitHubProviderFixture.LaterCommitSha);

        var moved = fixture.Provider.Check(record).ValueOrThrow();

        Assert.Equal(UpdateStatus.Pinned, moved.Status);
        Assert.Contains("different commit", moved.Warning);
        fixture.WriteTree(truncated: false, includeAlpha: false);

        var missing = fixture.Provider.Check(record).ValueOrThrow();

        Assert.Equal(UpdateStatus.SourceUnavailable, missing.Status);
        Assert.Contains("no longer contains SKILL.md", missing.Warning);
        Assert.Equal(GitHubProviderFixture.CommitSha, record.InstalledRevision);
        Assert.Equal(installedHash, PayloadHasher.HashFolder(record.CanonicalPath));
    }

    [Fact]
    public void Check_refresh_persists_dates_and_preserves_prior_result_as_stale_after_provider_failure()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var runner = new GitHubCheckRunner(fixture.Provider, fixture.StateStore);

        var successful = runner.Refresh();
        var prior = Assert.Single(fixture.StateStore.Load().Records).LatestCheck!;

        Assert.Equal(1, successful.CheckedCount);
        Assert.Equal(0, successful.FailureCount);
        Assert.Equal(UpdateStatus.Current, prior.Status);
        Assert.Equal(DateTimeOffset.Parse("2026-01-02T03:04:05Z"), prior.InstalledRevisionDate);
        Assert.False(prior.IsStale);
        fixture.FailRequestsContaining("/commits/");

        var failed = runner.Refresh();
        var stale = Assert.Single(fixture.StateStore.Load().Records).LatestCheck!;

        Assert.Equal(1, failed.FailureCount);
        Assert.Equal(UpdateStatus.Current, stale.Status);
        Assert.True(stale.IsStale);
        Assert.Contains("exit code 17", stale.Failure);
        Assert.True(stale.CheckedAt >= prior.CheckedAt);
        Assert.Equal(prior.AvailableRevision, stale.AvailableRevision);
        var row = new InventoryRow(Assert.Single(
            new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()).Entries));
        Assert.Contains("stale", row.UpdateStatus, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("exit code 17", row.CheckNotice);
    }

    [Fact]
    public void Initial_provider_failure_is_persisted_as_Check_Failed()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        fixture.FailRequestsContaining("/commits/");

        new GitHubCheckRunner(fixture.Provider, fixture.StateStore).Refresh();

        var check = Assert.Single(fixture.StateStore.Load().Records).LatestCheck!;
        Assert.Equal(UpdateStatus.CheckFailed, check.Status);
        Assert.False(check.IsStale);
        Assert.Contains("exit code 17", check.Failure);
    }

    [Fact]
    public void Missing_tracking_ref_is_Source_Unavailable_not_Check_Failed()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        fixture.ReturnNotFoundFor("/commits/main");

        new GitHubCheckRunner(fixture.Provider, fixture.StateStore).Refresh();

        var check = Assert.Single(fixture.StateStore.Load().Records).LatestCheck!;
        Assert.Equal(UpdateStatus.SourceUnavailable, check.Status);
        Assert.Null(check.Failure);
        Assert.Contains("unavailable", check.Warning, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Direct_update_replaces_only_verified_selected_content_and_reports_Current()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        fixture.SetCommit(GitHubProviderFixture.LaterCommitSha);
        var sourceSkillMd = Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha", "SKILL.md");
        File.AppendAllText(sourceSkillMd, "\nChanged upstream.\n");
        new GitHubCheckRunner(fixture.Provider, fixture.StateStore).Refresh();
        var checkedRecord = Assert.Single(fixture.StateStore.Load().Records);
        Assert.Equal(UpdateStatus.UpdateAvailable, checkedRecord.LatestCheck!.Status);

        var update = fixture.Provider.Update(checkedRecord).ValueOrThrow();

        Assert.Equal(GitHubProviderFixture.LaterCommitSha, update.InstalledRevision);
        Assert.Equal(
            File.ReadAllText(sourceSkillMd),
            File.ReadAllText(Path.Combine(fixture.CanonicalPath("alpha"), "SKILL.md")));
        Assert.True(Junction.IsJunctionTo(fixture.ClaudePath("alpha"), fixture.CanonicalPath("alpha")));
        var persisted = Assert.Single(fixture.StateStore.Load().Records);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
        Assert.Equal(GitHubProviderFixture.LaterCommitSha, persisted.InstalledRevision);
        Assert.Equal(GitHubProviderFixture.LaterCommitSha, persisted.Provenance.ResolvedCommit);
        Assert.Equal(PayloadHasher.HashFolder(persisted.CanonicalPath), persisted.InstalledPayloadHash);
        Assert.Equal(OperationOutcome.Updated, persisted.LastOperationOutcome);
        Assert.Equal(UpdateStatus.Current, persisted.LatestCheck!.Status);
        Assert.False(persisted.LatestCheck.IsStale);
        Assert.Contains($"@{GitHubProviderFixture.LaterCommitSha}", persisted.ProviderEvidence);
    }

    [Fact]
    public void Direct_update_refuses_local_drift_without_changing_content_or_authority()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        fixture.SetCommit(GitHubProviderFixture.LaterCommitSha);
        File.AppendAllText(
            Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha", "SKILL.md"),
            "\nChanged upstream.\n");
        new GitHubCheckRunner(fixture.Provider, fixture.StateStore).Refresh();
        var checkedRecord = Assert.Single(fixture.StateStore.Load().Records);
        var localSkillMd = Path.Combine(checkedRecord.CanonicalPath, "SKILL.md");
        File.AppendAllText(localSkillMd, "\nLocal edit.\n");
        var locallyEdited = File.ReadAllText(localSkillMd);

        var update = fixture.Provider.Update(checkedRecord);

        Assert.False(update.Succeeded);
        Assert.Contains("recorded payload hash", update.Diagnostics);
        Assert.Equal(locallyEdited, File.ReadAllText(localSkillMd));
        var persisted = Assert.Single(fixture.StateStore.Load().Records);
        Assert.Equal(GitHubProviderFixture.CommitSha, persisted.InstalledRevision);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    [Fact]
    public void Direct_update_refuses_selected_content_that_changed_after_Check()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        fixture.SetCommit(GitHubProviderFixture.LaterCommitSha);
        var upstreamSkillMd = Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha", "SKILL.md");
        File.AppendAllText(upstreamSkillMd, "\nFirst upstream change.\n");
        new GitHubCheckRunner(fixture.Provider, fixture.StateStore).Refresh();
        var checkedRecord = Assert.Single(fixture.StateStore.Load().Records);
        File.AppendAllText(upstreamSkillMd, "\nChanged again after Check.\n");
        var installedBefore = File.ReadAllText(Path.Combine(checkedRecord.CanonicalPath, "SKILL.md"));

        var update = fixture.Provider.Update(checkedRecord);

        Assert.False(update.Succeeded);
        Assert.Contains("changed after Check", update.Diagnostics);
        Assert.Equal(installedBefore, File.ReadAllText(Path.Combine(checkedRecord.CanonicalPath, "SKILL.md")));
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    [Fact]
    public void Direct_update_refuses_a_missing_Claude_junction()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        fixture.SetCommit(GitHubProviderFixture.LaterCommitSha);
        File.AppendAllText(
            Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha", "SKILL.md"),
            "\nChanged upstream.\n");
        new GitHubCheckRunner(fixture.Provider, fixture.StateStore).Refresh();
        var checkedRecord = Assert.Single(fixture.StateStore.Load().Records);
        Directory.Delete(fixture.ClaudePath("alpha"), recursive: false);

        var update = fixture.Provider.Update(checkedRecord);

        Assert.False(update.Succeeded);
        Assert.Contains("Claude junction", update.Diagnostics);
        Assert.Equal(GitHubProviderFixture.CommitSha, Assert.Single(fixture.StateStore.Load().Records).InstalledRevision);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    [Fact]
    public void Workbench_row_exposes_Check_details_and_only_enables_a_safe_update()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        fixture.SetCommit(GitHubProviderFixture.LaterCommitSha);
        File.AppendAllText(
            Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha", "SKILL.md"),
            "\nChanged upstream.\n");
        new GitHubCheckRunner(fixture.Provider, fixture.StateStore).Refresh();
        var viewModel = new MainViewModel();

        viewModel.LoadInventory(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()));
        viewModel.SelectedRow = Assert.Single(viewModel.Rows);

        Assert.Equal("GitHub - acme/library", viewModel.SelectedRow.Provenance);
        Assert.Equal(fixture.Reference.Original, viewModel.SelectedRow.Source);
        Assert.Equal("alpha", viewModel.SelectedRow.SourceSkillPath);
        Assert.Equal("main (Branch)", viewModel.SelectedRow.TrackingRule);
        Assert.Equal("Update available", viewModel.SelectedRow.UpdateStatus);
        Assert.Contains(GitHubProviderFixture.CommitSha[..12], viewModel.SelectedRow.InstalledRevision);
        Assert.Contains("2026-01-02", viewModel.SelectedRow.AvailableRevision);
        Assert.NotEqual("Not checked", viewModel.SelectedRow.LastChecked);
        Assert.True(viewModel.SelectedRow.CanUpdate);
        Assert.Equal(1, Assert.Single(viewModel.Filters, filter => filter.Name == "Updates").Count);

        File.AppendAllText(
            Path.Combine(viewModel.SelectedRow.Entry.LocalPath, "SKILL.md"),
            "\nLocal drift.\n");
        viewModel.LoadInventory(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()));

        Assert.NotNull(viewModel.SelectedRow);
        Assert.Equal("Locally modified", viewModel.SelectedRow.Health);
        Assert.False(viewModel.SelectedRow.CanUpdate);
        Assert.Contains("locally modified", viewModel.SelectedRow.ActionState, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Install_records_pending_before_payload_then_verifies_topology_and_authority()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.RequirePendingOperationBeforeContent();

        var result = fixture.Provider.Install(inspection, inspection.Skills).ValueOrThrow();

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
            Assert.Equal("gh version 99.0.0-fake", record.Provenance.ProviderVersion);
            Assert.Equal(GitHubProviderFixture.CommitSha, record.InstalledRevision);
            Assert.Equal(PayloadHasher.HashFolder(record.CanonicalPath), record.InstalledPayloadHash);
        });

        using (var observed = JsonDocument.Parse(File.ReadAllText(Path.Combine(fixture.FixtureRoot, "observed-pending.json"))))
        {
            var pending = observed.RootElement.GetProperty("pendingOperation");
            Assert.Equal(4, pending.GetProperty("startingPaths").GetArrayLength());
            Assert.Equal(4, pending.GetProperty("startingHashes").GetArrayLength());
        }

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
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        Directory.CreateDirectory(fixture.CanonicalPath("alpha"));
        File.WriteAllText(Path.Combine(fixture.CanonicalPath("alpha"), "keep.txt"), "keep");

        var result = fixture.Provider.Install(inspection, [inspection.Skills[0]]);

        Assert.False(result.Succeeded);
        Assert.Contains("Collision", result.Diagnostics);
        Assert.Equal("keep", File.ReadAllText(Path.Combine(fixture.CanonicalPath("alpha"), "keep.txt")));
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public void Existing_real_Claude_folder_is_failure_and_is_never_replaced_by_a_junction()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        Directory.CreateDirectory(fixture.ClaudePath("alpha"));
        File.WriteAllText(Path.Combine(fixture.ClaudePath("alpha"), "keep.txt"), "keep");

        var result = fixture.Provider.Install(inspection, [inspection.Skills[0]]);

        Assert.False(result.Succeeded);
        Assert.Contains("Claude", result.Diagnostics);
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

        var result = fixture.Provider.Inspect(fixture.Reference);

        Assert.False(result.Succeeded);
        Assert.Contains("exit code 17", result.Diagnostics);
        Assert.False(File.Exists(fixture.StatePath));
        Assert.False(Directory.Exists(Path.Combine(fixture.Home, ".agents")));
    }

    [Fact]
    public void Payload_failure_after_journaling_rolls_back_created_paths_and_clears_pending()
    {
        using var fixture = new GitHubProviderFixture("scripts/run.ps1");
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.RequirePendingOperationBeforeContent();

        var result = fixture.Provider.Install(inspection, [inspection.Skills[0]]);

        Assert.False(result.Succeeded);
        Assert.Contains("failed", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
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

        var result = fixture.Provider.Inspect(fixture.Reference);

        Assert.False(result.Succeeded);
        Assert.Contains("truncated", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public void Install_is_unavailable_when_nothing_is_selected()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();

        var result = fixture.Provider.Install(inspection, []);
        Assert.False(result.Succeeded);
        Assert.False(File.Exists(fixture.StatePath));
    }

    [Fact]
    public void Selection_supports_none_and_all_while_invalid_Source_Skills_remain_unselectable()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        var invalid = new SourceSkill(
            "invalid",
            "skills/invalid",
            "invalid",
            null,
            null,
            false,
            "missing name",
            ["skills/invalid/SKILL.md"]);
        var viewModel = new SourceInspectionViewModel(inspection with
        {
            Skills = [.. inspection.Skills, invalid],
        });

        Assert.False(viewModel.CanInstall);
        viewModel.ExactSelection = "Alpha Display";
        Assert.True(viewModel.SelectExact());
        Assert.Equal(1, viewModel.SelectedCount);
        viewModel.SelectAll(false);
        viewModel.ExactSelection = "alpha display";
        Assert.False(viewModel.SelectExact());
        viewModel.SelectAll(true);
        Assert.True(viewModel.CanInstall);
        Assert.Equal(2, viewModel.SelectedCount);
        Assert.False(viewModel.Skills[^1].IsSelected);
        viewModel.SelectAll(false);
        Assert.False(viewModel.CanInstall);

        var duplicateAlias = new SourceSkill(
            "nested/other",
            "skills/nested/other",
            "other",
            "Alpha Display",
            "Another exact alias.",
            true,
            null,
            ["skills/nested/other/SKILL.md"]);
        var ambiguous = new SourceInspectionViewModel(inspection with
        {
            Skills = [inspection.Skills[0], duplicateAlias],
        });
        ambiguous.ExactSelection = "Alpha Display";
        Assert.False(ambiguous.SelectExact());
        Assert.Contains("ambiguous", ambiguous.Status, StringComparison.OrdinalIgnoreCase);
        ambiguous.ExactSelection = "alpha";
        Assert.True(ambiguous.SelectExact());
    }
}
