using System.IO;
using Skilly.Infrastructure;
using Skilly.Providers.GitHub;

namespace Skilly.App.Tests;

public sealed class LiveGitHubFactAttribute : FactAttribute
{
    public LiveGitHubFactAttribute()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SKILLY_RUN_LIVE_GITHUB_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Skip = "Set SKILLY_RUN_LIVE_GITHUB_TESTS=1 after authenticating gh to run pre-release live gates.";
        }
    }
}

[Trait("Category", "LiveGitHubPreRelease")]
public sealed class LiveGitHubPreReleaseTests
{
    private const int ExpectedCursorSkillCount = 45;

    [LiveGitHubFact]
    public void Current_Cursor_pstack_source_is_complete_at_one_immutable_revision()
    {
        using var context = new LiveGitHubContext();
        Assert.True(GitHubSourceReference.TryParse(
            "https://github.com/cursor/plugins/tree/main/pstack/skills",
            out var reference,
            out var error), error);

        var inspection = context.Inspector.Inspect(reference, context.Client.GetVersion());

        var expectedRevision = Environment.GetEnvironmentVariable("SKILLY_EXPECTED_CURSOR_REVISION");
        if (!string.IsNullOrWhiteSpace(expectedRevision))
        {
            Assert.Equal(expectedRevision, inspection.Commit.Sha);
        }
        Assert.True(inspection.Skills.Count == ExpectedCursorSkillCount,
            $"Cursor pstack returned {inspection.Skills.Count} Skills at {inspection.Commit.Sha}; expected exactly {ExpectedCursorSkillCount}. Reconcile an intentional upstream change before changing this invariant.");
        Assert.All(inspection.Skills, static skill =>
        {
            Assert.True(skill.MetadataValid, skill.MetadataError);
            Assert.NotEmpty(skill.ContentIdentity);
        });
        var poteto = Assert.Single(inspection.Skills, static skill => skill.SkillPath == "poteto-mode");
        Assert.True(poteto.MatchesAlias("poteto-mode"));
        Assert.True(poteto.MatchesAlias("Poteto Mode"));
        LiveGateEvidence.Write("cursor-pstack", new
        {
            source = reference.Normalized,
            revision = inspection.Commit.Sha,
            skillCount = inspection.Skills.Count,
            provider = context.Client.GetVersion(),
        });
    }

    [LiveGitHubFact]
    public void Authenticated_private_source_supports_discovery_and_selected_folder_acquisition()
    {
        var source = Environment.GetEnvironmentVariable("SKILLY_LIVE_PRIVATE_GITHUB_URL");
        Assert.False(string.IsNullOrWhiteSpace(source),
            "SKILLY_LIVE_PRIVATE_GITHUB_URL must identify an accessible private repository or Skill subdirectory.");
        using var context = new LiveGitHubContext();
        Assert.True(GitHubSourceReference.TryParse(source!, out var reference, out var error), error);

        context.Client.EnsureAuthenticated(reference.Host);
        var inspection = context.Inspector.Inspect(reference, context.Client.GetVersion());
        Assert.Equal("private", inspection.Repository.Visibility);
        var selected = Assert.Single(inspection.Skills.Take(1));
        var files = context.Client.FetchFolder(
            reference.Owner,
            reference.Repository,
            inspection.Commit.Sha,
            selected.RepositoryPath,
            selected.FilePaths);

        Assert.Contains(files, static file => file.RelativePath == "SKILL.md");
        Assert.Equal(selected.FilePaths.Count, files.Count);
        LiveGateEvidence.Write("private-github", new
        {
            repositoryVisibility = inspection.Repository.Visibility,
            revision = inspection.Commit.Sha,
            selectedSkillPath = selected.SkillPath,
            selectedFileCount = files.Count,
            provider = context.Client.GetVersion(),
        });
    }

    private sealed class LiveGitHubContext : IDisposable
    {
        private readonly string _root = Path.Combine(Path.GetTempPath(), "skilly-live-github-" + Guid.NewGuid().ToString("N"));

        public LiveGitHubContext()
        {
            Directory.CreateDirectory(_root);
            var log = new RollingLog(Path.Combine(_root, "logs"));
            Client = new GhClient(new ProcessRunner(log));
            Client.EnsureAuthenticated();
            Inspector = new SourceInspector(Client, log);
        }

        public GhClient Client { get; }

        public SourceInspector Inspector { get; }

        public void Dispose() => PackagedAppFixture.TryDeleteDirectory(_root);
    }
}
