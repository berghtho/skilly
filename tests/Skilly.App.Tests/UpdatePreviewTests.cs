using System.IO;
using Skilly.Infrastructure;
using Skilly.Providers;
using Skilly.Providers.Apm;
using Skilly.Providers.GitHub;
using Skilly.Providers.SkillsCli;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.App.Tests;

public sealed class UpdatePreviewTests
{
    [Fact]
    public void GitHub_preview_compares_scripts_and_markdown_without_mutation_then_applies_reviewed_payload()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        File.AppendAllText(Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha", "SKILL.md"), "\nNew instruction.\n");
        File.WriteAllText(Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha", "scripts", "run.ps1"), "'new script'\n");
        fixture.SetCommit(GitHubProviderFixture.LaterCommitSha);
        new ProviderCheckRunner(fixture.Provider, fixture.StateStore).Refresh();
        var record = fixture.StateStore.Load().Records.Single();
        var stateBytes = File.ReadAllBytes(fixture.StatePath);
        var preview = fixture.Provider.PreviewUpdate(record).ValueOrThrow();
        var skill = Assert.Single(preview.Skills);
        Assert.Contains(skill.Files, file => file.Path == "scripts/run.ps1" && file.AfterText.Contains("new script"));
        Assert.Contains(skill.Files, file => file.Path == "SKILL.md" && file.Diff.Contains("+ New instruction."));
        Assert.Equal(stateBytes, File.ReadAllBytes(fixture.StatePath));
        Assert.Equal(record.InstalledPayloadHash, PayloadHasher.HashFolder(record.CanonicalPath));
        fixture.Provider.Update(record, preview: preview).ValueOrThrow();
        Assert.Equal(skill.TargetHash, PayloadHasher.HashFolder(record.CanonicalPath));
    }

    [Fact]
    public void Reviewed_GitHub_update_refuses_local_changes_before_any_mutation()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        File.AppendAllText(Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha", "SKILL.md"), "\nChanged upstream.\n");
        fixture.SetCommit(GitHubProviderFixture.LaterCommitSha);
        new ProviderCheckRunner(fixture.Provider, fixture.StateStore).Refresh();
        var record = fixture.StateStore.Load().Records.Single();
        var preview = fixture.Provider.PreviewUpdate(record).ValueOrThrow();
        var file = Path.Combine(record.CanonicalPath, "SKILL.md");
        File.AppendAllText(file, "Local work");
        var expected = File.ReadAllBytes(file);
        Assert.False(fixture.Provider.Update(record, preview: preview).Succeeded);
        Assert.Equal(expected, File.ReadAllBytes(file));
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Skills_preview_is_read_only_and_update_rejects_drift_after_review(bool drift)
    {
        using var fixture = new SkillsCliProviderFixture();
        var inspection = fixture.Provider.Inspect(SkillsCliProviderFixture.Source).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        fixture.WriteSkill("alpha", "Alpha from skills provider.", "Reviewed change");
        var state = fixture.StateStore.Load();
        var record = state.Records.Single();
        record.LatestCheck = Snapshot(fixture.Provider.Check(record).ValueOrThrow());
        fixture.StateStore.Save(state);
        var before = File.ReadAllBytes(fixture.StatePath);
        var providerLock = File.ReadAllBytes(fixture.ProviderLockPath);
        var preview = fixture.Provider.PreviewUpdate(record).ValueOrThrow();
        Assert.Equal(before, File.ReadAllBytes(fixture.StatePath));
        Assert.Equal(providerLock, File.ReadAllBytes(fixture.ProviderLockPath));
        Assert.Contains("Reviewed change", Assert.Single(Assert.Single(preview.Skills).Files).AfterText);
        if (drift) fixture.WriteSkill("alpha", "Alpha from skills provider.", "Unreviewed change");
        var result = fixture.Provider.Update(record, preview: preview);
        Assert.Equal(!drift, result.Succeeded);
        Assert.Equal(drift ? record.InstalledPayloadHash : preview.Skills[0].TargetHash, PayloadHasher.HashFolder(record.CanonicalPath));
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Apm_preview_includes_whole_package_and_rejects_unreviewed_payload(bool drift)
    {
        List<string>? journalIds = null;
        using var fixture = new ApmProviderFixture(state =>
        {
            if (state.PendingOperation is { OperationType: MutationType.Update } pending)
                journalIds = pending.AffectedInstallationIds.ToList();
        });
        var inspection = fixture.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        fixture.Provider.Install(inspection, inspection.Skills).ValueOrThrow();
        fixture.WriteSkill("alpha", "Alpha from APM.", "Reviewed package change");
        var state = fixture.StateStore.Load();
        var record = state.Records.Single(record => Path.GetFileName(record.CanonicalPath) == "alpha");
        record.LatestCheck = Snapshot(fixture.Provider.Check(record).ValueOrThrow());
        fixture.StateStore.Save(state);
        var before = File.ReadAllBytes(fixture.StatePath);
        var manifest = File.ReadAllBytes(fixture.ManifestPath);
        var preview = fixture.Provider.PreviewUpdate(record).ValueOrThrow();
        Assert.Equal(2, preview.Skills.Count);
        Assert.True(preview.CanApply);
        Assert.Equal(before, File.ReadAllBytes(fixture.StatePath));
        Assert.Equal(manifest, File.ReadAllBytes(fixture.ManifestPath));
        if (drift) fixture.WriteSkill("alpha", "Alpha from APM.", "Unreviewed package change");
        Assert.Equal(!drift, fixture.Provider.Update(record, preview: preview).Succeeded);
        Assert.Equal(preview.Skills.Select(skill => skill.InstallationId).Order(), journalIds!.Order());
        foreach (var skill in preview.Skills)
            Assert.Equal(drift ? skill.StartingHash : skill.TargetHash, PayloadHasher.HashFolder(skill.LocalPath));
    }

    [Fact]
    public void Apm_preview_shows_added_Skills_but_blocks_membership_changes()
    {
        using var fixture = new ApmProviderFixture();
        Directory.Delete(Path.Combine(fixture.SourceRoot, "skills", "beta"), true);
        var inspection = fixture.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        fixture.Provider.Install(inspection, inspection.Skills).ValueOrThrow();
        fixture.WriteSkill("beta", "New package member.");
        var state = fixture.StateStore.Load();
        var record = Assert.Single(state.Records);
        record.LatestCheck = Snapshot(fixture.Provider.Check(record).ValueOrThrow());
        fixture.StateStore.Save(state);
        var before = File.ReadAllBytes(fixture.StatePath);
        var preview = fixture.Provider.PreviewUpdate(record).ValueOrThrow();
        Assert.False(preview.CanApply);
        Assert.Contains(preview.Skills, skill => skill.Name == "beta" && skill.Files.All(file => file.Change == "Added"));
        Assert.False(fixture.Provider.Update(record, preview: preview).Succeeded);
        Assert.Equal(before, File.ReadAllBytes(fixture.StatePath));
        Assert.False(Directory.Exists(fixture.Canonical("beta")));
    }

    [Fact]
    public void File_preview_reports_add_remove_and_binary_changes_without_executing_content()
    {
        var old = new[] { new PreviewFile("removed.md", 3, "old", "old"), new PreviewFile("image.png", 4, "binary-old", null) };
        var next = new[] { new PreviewFile("new.ps1", 7, "new", "exit 99"), new PreviewFile("image.png", 5, "binary-new", null) };
        var changes = PreviewFiles.Compare(old, next);
        Assert.Contains(changes, file => file.Path == "removed.md" && file.Change == "Removed");
        Assert.Contains(changes, file => file.Path == "new.ps1" && file.Change == "Added" && file.Diff.Contains("+ exit 99"));
        Assert.Contains(changes, file => file.Path == "image.png" && file.Diff.Contains("binary-new"));
    }

    [Fact]
    public void History_preserves_results_and_marks_interruption_without_retries()
    {
        var root = Path.Combine(Path.GetTempPath(), "skilly-history-" + Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "history.json");
        try
        {
            var store = new OperationHistoryStore(path);
            store.Save([new OperationEntry("run", DateTimeOffset.Now, "Update all", "alpha", "C:/skills/alpha", "Running", "token=private-value"),
                new OperationEntry("run", DateTimeOffset.Now, "Update all", "beta", "C:/skills/beta", "Updated", "Verified")]);
            Assert.Null(store.Notice);
            Assert.DoesNotContain("private-value", File.ReadAllText(path));
            var loaded = store.Load();
            Assert.Equal("Interrupted", loaded[0].Status);
            Assert.Equal("Updated", loaded[1].Status);
            store.Save(Enumerable.Range(0, 1000).Select(index => new OperationEntry("large", DateTimeOffset.Now, "Update all",
                "skill" + index, "C:/skills/alpha", "Updated", new string('\u00e4', 4000))));
            Assert.True(new FileInfo(path).Length <= 4 * 1024 * 1024);
            Assert.NotEmpty(store.Load());
            File.WriteAllText(path, "broken-json");
            Assert.Empty(store.Load());
            Assert.NotNull(store.Notice);
        }
        finally { if (File.Exists(path)) File.Delete(path); if (Directory.Exists(root)) Directory.Delete(root); }
    }

    private static CheckSnapshot Snapshot(CheckResult check) => new()
    {
        Status = check.Status, InstalledRevision = check.InstalledRevision, AvailableRevision = check.AvailableRevision,
        AvailablePayloadHash = check.AvailablePayloadHash, AvailableContentIdentity = check.AvailableContentIdentity, CheckedAt = check.CheckedAt,
    };
}
