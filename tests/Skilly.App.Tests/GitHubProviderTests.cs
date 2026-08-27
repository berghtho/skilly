using System.IO;
using System.Text.Json;
using Skilly.Infrastructure;
using Skilly.Providers;
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

    public GitHubProviderFixture(string? failPattern = null, Action<SkillyState>? beforeStateSave = null)
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
            BuildConfiguration,
            "net10.0",
            "FakeGh.exe");
        Assert.True(File.Exists(fakeGh), $"FakeGh was not built at '{fakeGh}'.");
        var fakeGit = Path.Combine(
            PackagedAppFixture.FindRepoRoot(),
            "tests",
            "FakeGit",
            "bin",
            BuildConfiguration,
            "net10.0",
            "FakeGit.exe");
        Assert.True(File.Exists(fakeGit), $"FakeGit was not built at '{fakeGit}'.");

        _environment = new Dictionary<string, string?>
        {
            ["FAKE_GH_FIXTURE_ROOT"] = FixtureRoot,
            ["FAKE_GH_STATE_PATH"] = StatePath,
            ["FAKE_GH_FAIL_PATTERN"] = failPattern,
            ["FAKE_GH_FALSE_SUCCESS_PATTERN"] = null,
            ["FAKE_GH_INVOCATIONS"] = Path.Combine(FixtureRoot, "gh-invocations.jsonl"),
            ["FAKE_GIT_INVOCATIONS"] = Path.Combine(FixtureRoot, "git-invocations.jsonl"),
            ["FAKE_GIT_FAIL_PATTERN"] = failPattern?.Contains("scripts/", StringComparison.Ordinal) == true ? "checkout" : null,
        };
        Log = new RollingLog(Path.Combine(Root, "logs"));
        var client = new GhClient(new ProcessRunner(Log, _environment), fakeGh, fakeGit);
        StateStore = new StateStore(Log, StatePath, beforeStateSave);
        var inspector = new SourceInspector(client, Log);
        var installer = new GitHubInstaller(client, StateStore, Log, Home);
        var checker = new GitHubChecker(client);
        Lifecycle = new GitHubLifecycle(checker, StateStore, Log);
        Provider = new GitHubProvider(
            client,
            inspector,
            installer,
            checker,
            new GitHubUpdater(checker, StateStore, Log),
            Lifecycle,
            new GitHubAdoptionVerifier(client, Home));
    }

    public string Root { get; }

    public string Home { get; }

    public string FixtureRoot { get; }

    public string StatePath { get; }

    public RollingLog Log { get; }

    public StateStore StateStore { get; }

    public GitHubProvider Provider { get; }

    public GitHubLifecycle Lifecycle { get; }

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
        var payload = new
        {
            truncated,
            excludedPath = includeAlpha ? null : "skills/alpha",
        };
        File.WriteAllText(Path.Combine(FixtureRoot, "tree-control.json"), JsonSerializer.Serialize(payload));
    }

    public void SetCommit(string sha)
        => File.WriteAllText(Path.Combine(FixtureRoot, "commit.json"), $"{{\"sha\":\"{sha}\"}}");

    public void SetBranch(string name)
        => File.WriteAllText(Path.Combine(FixtureRoot, "heads.json"), $"[{{\"ref\":\"refs/heads/{name}\"}}]");

    public void SetTag(string name)
        => File.WriteAllText(Path.Combine(FixtureRoot, "tags.json"), $"[{{\"ref\":\"refs/tags/{name}\"}}]");

    public void ClearBranches()
        => File.WriteAllText(Path.Combine(FixtureRoot, "heads.json"), "[]");

    public void FailRequestsContaining(string? pattern)
    {
        _environment["FAKE_GH_FAIL_PATTERN"] = pattern;
        _environment["FAKE_GIT_FAIL_PATTERN"] = pattern?.Contains("scripts/", StringComparison.Ordinal) == true ? "checkout" : null;
    }

    public void ReturnNotFoundFor(string? pattern) => _environment["FAKE_GH_NOT_FOUND_PATTERN"] = pattern;

    public void ReturnFalseSuccessFor(string? pattern) => _environment["FAKE_GH_FALSE_SUCCESS_PATTERN"] = pattern;

    public void MakeContentUnavailable(string? pattern) => _environment["FAKE_GH_CONTENT_UNAVAILABLE_PATTERN"] = pattern;

    public void FailAuthentication(bool fail = true) => _environment["FAKE_GH_AUTH_FAILURE"] = fail ? "1" : null;

    public void SetCredentialCanary(string value) => _environment["FAKE_GH_CREDENTIAL_CANARY"] = value;

    public void OverrideGitHead(string? value) => _environment["FAKE_GIT_HEAD_OVERRIDE"] = value;

    public void OverrideTreeIdentity(string? value) => _environment["FAKE_GH_TREE_IDENTITY_OVERRIDE"] = value;

    public void AddSourceSkill(string repositoryPath, string declaredName, int additionalFiles = 0)
    {
        var directory = Path.Combine(FixtureRoot, "files", repositoryPath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, "SKILL.md"),
            $"---\nname: {declaredName}\ndescription: Deterministic pstack fixture Skill.\n---\n");
        for (var index = 0; index < additionalFiles; index++)
        {
            var path = Path.Combine(directory, "references", $"fixture-{index:D2}.md");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, $"fixture {index}\n");
        }
    }

    public string GhInvocationsPath => Path.Combine(FixtureRoot, "gh-invocations.jsonl");

    public string GitInvocationsPath => Path.Combine(FixtureRoot, "git-invocations.jsonl");

    public string CanonicalPath(string name) => Path.Combine(Home, ".agents", "skills", name);

    public string ClaudePath(string name) => Path.Combine(Home, ".claude", "skills", name);

    private static string BuildConfiguration => new DirectoryInfo(AppContext.BaseDirectory).Parent!.Name;

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

    [Fact]
    public void Normalizes_repository_identity_and_rejects_unsafe_URL_forms()
    {
        Assert.True(GitHubSourceReference.TryParse(
            "https://GITHUB.com/Acme/Library.git/tree/feature%2Dname/skills/catalog/",
            out var reference,
            out var error), error);
        Assert.Equal("github.com/acme/library/tree/feature-name/skills/catalog", reference.Normalized);
        Assert.Equal("github.com/acme/library", reference.NormalizedSource);

        Assert.False(GitHubSourceReference.TryParse("http://github.com/acme/library", out _, out _));
        Assert.False(GitHubSourceReference.TryParse("https://user@github.com/acme/library", out _, out _));
        Assert.False(GitHubSourceReference.TryParse("https://github.com/acme/library?token=secret", out _, out _));
        Assert.False(GitHubSourceReference.TryParse("https://github.com/acme/library/tree/main/%2e%2e/secret", out _, out _));
        Assert.False(GitHubSourceReference.TryParse("https://github.com/acme/library/tree/main/../secret", out _, out _));
    }
}

public sealed class GitHubProviderTests
{
    private static readonly string[] CursorPstackSkills =
    [
        "architect", "arena", "automate-me", "blast-radius", "bro", "create-verification-skill",
        "figure-it-out", "how", "interrogate", "maintain-verification-skill", "no-comments", "poteto-mode",
        "principle-boundary-discipline", "principle-build-the-lever", "principle-encode-lessons-in-structure",
        "principle-exhaust-the-design-space", "principle-experience-first", "principle-fix-root-causes",
        "principle-foundational-thinking", "principle-guard-the-context-window", "principle-laziness-protocol",
        "principle-make-operations-idempotent", "principle-migrate-callers-then-delete-legacy-apis",
        "principle-minimize-reader-load", "principle-model-the-domain", "principle-never-block-on-the-human",
        "principle-outcome-oriented-execution", "principle-prove-it-works", "principle-redesign-from-first-principles",
        "principle-separate-before-serializing-shared-state", "principle-sequence-verifiable-units",
        "principle-subtract-before-you-add", "principle-type-system-discipline", "recall", "reflect", "setup-pstack",
        "show-me-your-work", "swarm", "tdd", "teach", "technical-writing", "typescript-best-practices", "unslop", "why",
    ];

    [Fact]
    public void Ambiguous_tree_URL_tests_longest_ref_prefixes_and_pins_the_selected_commit()
    {
        using var fixture = new GitHubProviderFixture();
        fixture.SetBranch("feature/windows");
        Assert.True(GitHubSourceReference.TryParse(
            "https://github.com/acme/library/tree/feature/windows/skills",
            out var reference,
            out var error), error);

        var inspection = fixture.Provider.Inspect(reference).ValueOrThrow();

        Assert.Equal("feature/windows", inspection.RequestedTrackingRule);
        Assert.Equal("skills", inspection.Reference.RequestedPath);
        Assert.Equal(GitHubProviderFixture.CommitSha, inspection.Commit.Sha);
        var invocations = File.ReadAllText(fixture.GhInvocationsPath);
        Assert.Contains("feature%2Fwindows%2Fskills", invocations);
        Assert.Contains("feature%2Fwindows", invocations);
    }

    [Fact]
    public void Cursor_pstack_decision_fixture_discovers_all_expected_Source_Skills_and_exact_Poteto_aliases()
    {
        using var fixture = new GitHubProviderFixture();
        foreach (var folder in CursorPstackSkills)
        {
            fixture.AddSourceSkill(
                $"pstack/skills/{folder}",
                folder == "poteto-mode" ? "Poteto Mode" : folder,
                folder == "poteto-mode" ? 44 : 0);
        }
        Assert.True(GitHubSourceReference.TryParse(
            "https://github.com/cursor/plugins/tree/main/pstack/skills",
            out var reference,
            out var error), error);

        var inspection = fixture.Provider.Inspect(reference).ValueOrThrow();

        Assert.Equal(CursorPstackSkills, inspection.Skills.Select(static skill => skill.SkillPath));
        var poteto = Assert.Single(inspection.Skills, static skill => skill.SkillPath == "poteto-mode");
        Assert.Equal("Poteto Mode", poteto.DeclaredName);
        Assert.Equal(45, poteto.FilePaths.Count);
        Assert.True(poteto.MatchesAlias("poteto-mode"));
        Assert.True(poteto.MatchesAlias("Poteto Mode"));
        Assert.False(poteto.MatchesAlias("poteto mode"));

        var byPath = new SourceInspectionViewModel(inspection) { ExactSelection = "poteto-mode" };
        var byName = new SourceInspectionViewModel(inspection) { ExactSelection = "Poteto Mode" };
        Assert.True(byPath.SelectExact());
        Assert.True(byName.SelectExact());
        Assert.Equal(
            Assert.Single(byPath.Skills, static skill => skill.IsSelected).Skill.FilePaths,
            Assert.Single(byName.Skills, static skill => skill.IsSelected).Skill.FilePaths);
    }

    [Fact]
    public void Authenticated_API_acquisition_fetches_only_the_selected_validated_folder()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        File.WriteAllText(fixture.GhInvocationsPath, string.Empty);

        fixture.Provider.Install(inspection, [inspection.Skills.Single(static skill => skill.SkillPath == "alpha")]).ValueOrThrow();

        var invocations = File.ReadAllText(fixture.GhInvocationsPath);
        Assert.Contains("skills/alpha/SKILL.md", invocations);
        Assert.Contains("skills/alpha/scripts/run.ps1", invocations);
        Assert.DoesNotContain("skills/beta", invocations);
        Assert.DoesNotContain("unrelated", invocations);
    }

    [Fact]
    public void API_acquisition_failure_uses_authenticated_partial_sparse_checkout_detached_at_resolved_commit()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.MakeContentUnavailable("scripts/run.ps1");

        fixture.Provider.Install(inspection, [inspection.Skills.Single(static skill => skill.SkillPath == "alpha")]).ValueOrThrow();

        Assert.True(File.Exists(Path.Combine(fixture.CanonicalPath("alpha"), "scripts", "run.ps1")));
        var ghInvocations = File.ReadAllText(fixture.GhInvocationsPath);
        var gitInvocations = File.ReadAllText(fixture.GitInvocationsPath);
        Assert.Contains("\"repo\",\"clone\",\"acme/library\"", ghInvocations);
        Assert.Contains("--filter=blob:none", ghInvocations);
        Assert.Contains("--no-checkout", ghInvocations);
        Assert.Contains("\"sparse-checkout\",\"set\",\"--\",\"skills/alpha\"", gitInvocations);
        Assert.Contains($"\"checkout\",\"--detach\",\"{GitHubProviderFixture.CommitSha}\"", gitInvocations);
        Assert.Contains("\"rev-parse\",\"HEAD\"", gitInvocations);
        Assert.Contains("\"rev-parse\",\"--abbrev-ref\",\"HEAD\"", gitInvocations);
    }

    [Fact]
    public void Sparse_checkout_false_success_at_the_wrong_commit_is_rejected_and_rolled_back()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.MakeContentUnavailable("scripts/run.ps1");
        fixture.OverrideGitHead(GitHubProviderFixture.LaterCommitSha);

        var result = fixture.Provider.Install(
            inspection,
            [inspection.Skills.Single(static skill => skill.SkillPath == "alpha")]);

        Assert.False(result.Succeeded);
        Assert.Contains("already resolved immutable commit", result.Diagnostics);
        Assert.False(Directory.Exists(fixture.CanonicalPath("alpha")));
        Assert.Null(fixture.StateStore.Load().PendingOperation);
        Assert.Empty(fixture.StateStore.Load().Records);
    }

    [Fact]
    public void Public_and_private_sources_use_active_gh_auth_without_persisting_or_logging_credentials()
    {
        using var fixture = new GitHubProviderFixture();
        const string credentialCanary = "credential-canary-must-not-escape";
        fixture.SetCredentialCanary(credentialCanary);
        Assert.True(GitHubSourceReference.TryParse(
            "https://github.com/private-owner/private-library/tree/main/skills",
            out var privateReference,
            out var error), error);

        Assert.True(fixture.Provider.GetReadiness().IsReady);
        var inspection = fixture.Provider.Inspect(privateReference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();

        var invocations = File.ReadAllText(fixture.GhInvocationsPath);
        Assert.Contains("\"auth\",\"status\",\"--hostname\",\"github.com\"", invocations);
        Assert.DoesNotContain("auth\",\"token", invocations);
        Assert.DoesNotContain(credentialCanary, File.ReadAllText(fixture.StatePath));
        Assert.DoesNotContain(credentialCanary, string.Join("\n", Directory.GetFiles(Path.Combine(fixture.Root, "logs")).Select(ReadShared)));
    }

    [Fact]
    public void GitHub_auth_failure_is_provider_scoped_and_does_not_hide_inventory_or_enter_recovery()
    {
        using var fixture = new GitHubProviderFixture();
        Directory.CreateDirectory(fixture.CanonicalPath("local-skill"));
        File.WriteAllText(
            Path.Combine(fixture.CanonicalPath("local-skill"), "SKILL.md"),
            "---\nname: local-skill\ndescription: Local inventory remains visible.\n---\n");
        fixture.FailAuthentication();
        var readiness = fixture.Provider.GetReadiness();
        var viewModel = new MainViewModel();
        viewModel.LoadInventory(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()));
        viewModel.SetGitHubReadiness(readiness);

        Assert.False(readiness.IsReady);
        Assert.True(viewModel.HasProviderReadinessProblem);
        Assert.Contains("GitHub provider unavailable", viewModel.GitHubReadiness);
        Assert.Single(viewModel.Rows);
        Assert.True(viewModel.MutationsAllowed);
        Assert.False(viewModel.RecoveryRequired);
        Assert.False(fixture.Provider.Inspect(fixture.Reference).Succeeded);
    }
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
        var invocations = File.ReadAllText(fixture.GhInvocationsPath);
        Assert.DoesNotContain($"git/trees/{GitHubProviderFixture.CommitSha}?recursive=1", invocations);
        Assert.DoesNotContain("unrelated/SKILL.md", invocations);
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
    public void Selected_folder_tree_identity_change_reports_Update_Available_even_when_payload_bytes_match()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        fixture.SetCommit(GitHubProviderFixture.LaterCommitSha);
        fixture.OverrideTreeIdentity("tree-mode-change-with-identical-bytes");

        var check = fixture.Provider.Check(record).ValueOrThrow();

        Assert.Equal(UpdateStatus.UpdateAvailable, check.Status);
        Assert.Equal(record.InstalledPayloadHash, check.AvailablePayloadHash);
        Assert.NotEqual(record.Provenance.SelectedContentIdentity, check.AvailableContentIdentity);
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
        var runner = new ProviderCheckRunner(fixture.Provider, fixture.StateStore);

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

        new ProviderCheckRunner(fixture.Provider, fixture.StateStore).Refresh();

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

        new ProviderCheckRunner(fixture.Provider, fixture.StateStore).Refresh();

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
        new ProviderCheckRunner(fixture.Provider, fixture.StateStore).Refresh();
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
        Assert.Equal(checkedRecord.LatestCheck!.AvailableContentIdentity, persisted.Provenance.SelectedContentIdentity);
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
        new ProviderCheckRunner(fixture.Provider, fixture.StateStore).Refresh();
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
        new ProviderCheckRunner(fixture.Provider, fixture.StateStore).Refresh();
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
        new ProviderCheckRunner(fixture.Provider, fixture.StateStore).Refresh();
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
        new ProviderCheckRunner(fixture.Provider, fixture.StateStore).Refresh();
        var viewModel = new MainViewModel();

        viewModel.LoadInventory(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()));
        viewModel.SelectedRow = Assert.Single(viewModel.Rows);

        Assert.Equal("GitHub - acme/library", viewModel.SelectedRow.Provenance);
        Assert.Equal(fixture.Reference.Original, viewModel.SelectedRow.Source);
        Assert.Equal("alpha", viewModel.SelectedRow.SourceSkillPath);
        Assert.Equal("main (Branch)", viewModel.SelectedRow.TrackingRule);
        Assert.Equal("Update Available", viewModel.SelectedRow.UpdateStatus);
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
        Assert.Equal("Locally Modified", viewModel.SelectedRow.Health);
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
            Assert.False(string.IsNullOrWhiteSpace(record.Provenance.SelectedContentIdentity));
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
        Assert.Contains("\"selectedContentIdentity\"", json);
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

    private static string ReadShared(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
}
