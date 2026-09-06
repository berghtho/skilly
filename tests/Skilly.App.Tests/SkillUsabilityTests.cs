using Skilly.Infrastructure;
using Skilly.Providers;
using Skilly.Providers.Apm;
using Skilly.Providers.GitHub;
using Skilly.Providers.SkillsCli;
using Skilly.Skills;
using Skilly.ViewModels;

namespace Skilly.App.Tests;

public sealed class SkillUsabilityTests
{
    [Fact]
    public void Large_markdown_previews_are_bounded_without_changing_source_content()
    {
        var path = System.IO.Path.GetTempFileName();
        try
        {
            var content = new string('a', SkillMarkdownPreview.MaxCharacters) + "TAIL";
            System.IO.File.WriteAllText(path, content);
            var preview = SkillMarkdownPreview.ReadFile(path);
            Assert.Equal(SkillMarkdownPreview.FromText(content), preview);
            Assert.Contains("Preview truncated", preview);
            Assert.DoesNotContain("TAIL", preview);
            Assert.Equal(content, System.IO.File.ReadAllText(path));
        }
        finally { System.IO.File.Delete(path); }
    }

    [Fact]
    public void GitHub_preview_uses_inspected_content_and_filtering_preserves_install_selection()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        var view = new SourceInspectionViewModel(inspection);
        view.Browser.SearchText = "Alpha from GitHub";
        var alpha = Assert.Single(view.Browser.VisibleSkills);
        view.Browser.PreviewSkill = alpha;
        Assert.Contains("# Alpha", view.Browser.PreviewText);
        Assert.Equal(0, view.SelectedCount);
        view.SelectAll(true);
        view.Browser.SearchText = "Beta from GitHub";
        Assert.Single(view.Browser.VisibleSkills);
        Assert.Equal(1, view.SelectedCount);
        Assert.Contains("1 selected outside", view.Browser.ResultSummary);
        Assert.Null(view.Browser.PreviewSkill);
        view.SelectAll(true);
        Assert.Equal(2, view.SelectedCount);
        view.SelectAll(false);
        Assert.Equal(0, view.SelectedCount);
        Assert.False(System.IO.File.Exists(fixture.StatePath));
    }

    [Fact]
    public void Existing_destinations_remain_inspectable_but_cannot_be_selected_for_install()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        var view = new SourceInspectionViewModel(inspection, occupiedFolders: new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "ALPHA" });
        view.Browser.SearchText = "Alpha";
        var row = Assert.Single(view.Browser.VisibleSkills);
        view.Browser.PreviewSkill = row;
        Assert.Contains("# Alpha", view.Browser.PreviewText);
        view.SelectAll(true);
        view.ExactSelection = "Alpha Display";
        Assert.False(view.SelectExact());
        Assert.Equal(0, view.SelectedCount);
        Assert.False(view.CanInstall);
    }

    [Fact]
    public void Apm_keeps_preview_text_after_isolated_inspection_cleanup_and_blocks_occupied_destinations()
    {
        using var fixture = new ApmProviderFixture();
        var inspection = fixture.Provider.Inspect(ApmProviderFixture.Source).ValueOrThrow();
        var view = new ApmSourceInspectionViewModel(inspection, occupiedFolders: new HashSet<string> { "alpha" });
        view.Browser.SearchText = "Alpha from APM";
        view.Browser.PreviewSkill = Assert.Single(view.Browser.VisibleSkills);
        Assert.Contains("description: Alpha from APM.", view.Browser.PreviewText);
        view.SelectAll(true);
        Assert.Equal(0, view.SelectedCount);
        view.Browser.SearchText = "Beta";
        view.SelectAll(true);
        Assert.Equal(1, view.SelectedCount);
        Assert.False(System.IO.File.Exists(fixture.StatePath));
    }

    [Fact]
    public void Skills_description_search_and_unavailable_markdown_are_explicit()
    {
        var view = new SkillsCliSourceInspectionViewModel(new SkillsCliInspection("acme/library", "acme/library", "test",
            [new SkillsCliSourceSkill("alpha", "Review code") { AlreadyInstalled = true }, new SkillsCliSourceSkill("beta", "Draw diagrams")]));
        view.Browser.SearchText = "diagrams";
        view.Browser.PreviewSkill = Assert.Single(view.Browser.VisibleSkills);
        Assert.Contains("does not supply SKILL.md", view.Browser.PreviewText);
        view.SelectAll(true);
        Assert.Equal(1, view.SelectedCount);
        view.Browser.SearchText = "no matches";
        Assert.Empty(view.Browser.VisibleSkills);
        Assert.Contains("0 of 2 shown", view.Browser.ResultSummary);
        view.SelectAll(false);
        Assert.Equal(0, view.SelectedCount);
    }

    [Theory]
    [InlineData("https://github.com/acme/library", "https://github.com/acme/library")]
    [InlineData("acme/library", "https://github.com/acme/library")]
    [InlineData("file:///C:/malicious.cmd", null)]
    [InlineData("javascript:alert(1)", null)]
    [InlineData("https://secret@github.com/acme/library", null)]
    [InlineData("https://github.com/acme/library?token=secret", null)]
    [InlineData("cmd.exe /c calc", null)]
    public void Source_links_accept_only_web_addresses_without_credentials(string source, string? expected)
        => Assert.Equal(expected, SkillNavigation.SourceUrl(source));

    [Theory]
    [InlineData(InstallationHealth.Collision, "conflicting")]
    [InlineData(InstallationHealth.ExposureProblem, "Harness Exposure")]
    [InlineData(InstallationHealth.InvalidMetadata, "SKILL.md")]
    [InlineData(InstallationHealth.Missing, "backup")]
    public void Unhealthy_installations_have_specific_next_steps(InstallationHealth health, string expected)
    {
        var row = new InventoryRow(new InventoryEntry
        {
            FolderName = "alpha", LocalPath = @"C:\missing\alpha", RootKind = RootKind.CanonicalAgents,
            Kind = EntryKind.RealFolder, ManagementStatus = ManagementStatus.Unmanaged, Health = health,
            Metadata = new SkillMetadata(MetadataReadStatus.Valid, "alpha", "Test", null),
            Exposures = Enum.GetValues<Harness>().ToDictionary(harness => harness, _ => HarnessExposure.None()),
        });
        Assert.Contains(expected, row.ActionState);
        Assert.False(row.CanOpenFolder);
        Assert.False(row.CanReadSkillMarkdown);
        Assert.False(row.CanOpenSource);
    }
}
