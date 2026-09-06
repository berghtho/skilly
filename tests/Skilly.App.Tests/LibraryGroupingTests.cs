using Skilly.Skills;
using Skilly.State;
using Skilly.ViewModels;

namespace Skilly.App.Tests;

public sealed class LibraryGroupingTests
{
    [Fact]
    public void Status_sort_prioritizes_health_then_updates_and_keeps_stale_checks_visible()
    {
        var current = Record("github", "acme", "toolbox", "github.com/acme/toolbox", "current");
        current.LatestCheck = new CheckSnapshot { Status = UpdateStatus.Current, InstalledRevision = "v1", CheckedAt = DateTimeOffset.Now };
        var update = Record("github", "acme", "toolbox", "github.com/acme/toolbox", "update");
        update.LatestCheck = new CheckSnapshot { Status = UpdateStatus.UpdateAvailable, InstalledRevision = "v1", CheckedAt = DateTimeOffset.Now };
        var stale = Record("github", "acme", "toolbox", "github.com/acme/toolbox", "stale");
        stale.LatestCheck = new CheckSnapshot { Status = UpdateStatus.Current, InstalledRevision = "v1", CheckedAt = DateTimeOffset.Now, IsStale = true, Failure = "Network unavailable" };
        var model = new MainViewModel();
        model.LoadInventory(Snapshot(Entry("current", current), Entry("update", update), Entry("stale", stale), Entry("broken", health: InstallationHealth.ExposureProblem)));
        model.SortBy(InventorySortColumn.Status);
        Assert.Equal(["broken", "update", "stale", "current"], model.Rows.Cast<InventoryRow>().Select(row => row.Name));
        Assert.False(model.Rows.Cast<InventoryRow>().Single(row => row.Name == "current").HasStatusDeviation);
        var staleRow = model.Rows.Cast<InventoryRow>().Single(row => row.Name == "stale");
        Assert.Equal("Outline", staleRow.UpdateKind);
        Assert.Equal("Check Failed", staleRow.StatusUpdateText);
        Assert.False(staleRow.CanUpdate);
        stale.LatestCheck.IsStale = false;
        stale.LatestCheck.Status = UpdateStatus.SourceUnavailable;
        Assert.Equal("Source Unavailable", staleRow.StatusUpdateText);
        model.SortBy(InventorySortColumn.Status);
        Assert.Equal("current", ((InventoryRow)model.Rows[0]).Name);
    }

    [Fact]
    public void Reload_refreshes_selected_segment_count_and_update_all_count_without_losing_filter()
    {
        var model = new MainViewModel();
        model.LoadInventory(Snapshot(Entry("alpha")));
        model.SelectedFilter = model.Filters.Single(filter => filter.Name == "Healthy");
        model.LoadInventory(Snapshot(Entry("alpha"), Entry("beta")));
        Assert.Same(model.Filters.Single(filter => filter.Name == "Healthy"), model.SelectedFilter);
        Assert.Equal(2, model.SelectedFilter.Count);
        Assert.Equal(0, model.UpdatableCount);
    }

    [Fact]
    public void Library_key_uses_normalized_source_for_github_and_repository_for_other_providers()
    {
        var github = new InventoryRow(Entry("alpha", Record("github", "acme", "toolbox", "github.com/acme/toolbox", "alpha")));
        var managedSkills = new InventoryRow(Entry("beta", Record("skills", "acme", "acme/library", "https://example.test/acme/library", "beta")));
        var attributedSkills = new InventoryRow(Entry(
            "gamma",
            attribution: new ProviderAttribution("skills", "1.5.23", "acme/library", "acme/library", "gamma", "latest", default)));
        var unattributed = new InventoryRow(Entry("delta"));

        Assert.Equal("github|github.com/acme/toolbox", github.LibraryKey);
        Assert.Equal("acme/toolbox", github.LibraryLabel);
        Assert.Equal("GitHub", github.LibraryProviderLabel);
        Assert.Equal("skills|acme/library", managedSkills.LibraryKey);
        Assert.Equal(managedSkills.LibraryKey, attributedSkills.LibraryKey);
        Assert.Null(unattributed.LibraryKey);
    }

    [Fact]
    public void Grouped_view_lists_each_library_header_before_its_members_and_unattributed_last()
    {
        var viewModel = new MainViewModel { GroupByLibrary = true };
        viewModel.LoadInventory(Snapshot(
            Entry("alpha", Record("github", "acme", "toolbox", "github.com/acme/toolbox", "alpha")),
            Entry("beta", Record("github", "acme", "toolbox", "github.com/acme/toolbox", "beta")),
            Entry("gamma", Record("apm", "contoso", "contoso.pack", "contoso.pack", "gamma")),
            Entry("delta")));

        var rows = viewModel.Rows.ToList();
        Assert.Equal(7, rows.Count);
        var githubHeader = Assert.IsType<LibraryGroupRow>(rows[0]);
        Assert.Equal("acme/toolbox", githubHeader.Label);
        Assert.Equal(2, githubHeader.Members.Count);
        Assert.Equal(["alpha", "beta"], rows.Skip(1).Take(2).Cast<InventoryRow>().Select(static row => row.Name));
        var apmHeader = Assert.IsType<LibraryGroupRow>(rows[3]);
        Assert.Equal("contoso.pack", apmHeader.Label);
        Assert.Equal("Microsoft APM", apmHeader.ProviderLabel);
        var unattributedHeader = Assert.IsType<LibraryGroupRow>(rows[5]);
        Assert.Equal("No recorded Skill Library", unattributedHeader.Label);
        Assert.Null(unattributedHeader.Key);
        Assert.False(unattributedHeader.CanUpdateLibrary);
    }

    [Fact]
    public void Collapsing_a_library_hides_member_rows_and_survives_inventory_reload()
    {
        var viewModel = new MainViewModel { GroupByLibrary = true };
        InventoryEntry[] entries =
        [
            Entry("alpha", Record("github", "acme", "toolbox", "github.com/acme/toolbox", "alpha")),
            Entry("beta", Record("github", "acme", "toolbox", "github.com/acme/toolbox", "beta")),
        ];
        viewModel.LoadInventory(Snapshot(entries));

        var header = Assert.IsType<LibraryGroupRow>(viewModel.Rows[0]);
        header.IsExpanded = false;

        var collapsedHeader = Assert.IsType<LibraryGroupRow>(Assert.Single(viewModel.Rows));
        Assert.False(collapsedHeader.IsExpanded);

        viewModel.LoadInventory(Snapshot(entries));
        var reloadedHeader = Assert.IsType<LibraryGroupRow>(Assert.Single(viewModel.Rows));
        Assert.False(reloadedHeader.IsExpanded);

        reloadedHeader.IsExpanded = true;
        Assert.Equal(3, viewModel.Rows.Count);
    }

    [Fact]
    public void Ungrouped_view_stays_flat()
    {
        var viewModel = new MainViewModel();
        viewModel.LoadInventory(Snapshot(
            Entry("alpha", Record("github", "acme", "toolbox", "github.com/acme/toolbox", "alpha")),
            Entry("delta")));

        Assert.All(viewModel.Rows, static row => Assert.IsType<InventoryRow>(row));
        Assert.Equal(2, viewModel.Rows.Count);
    }

    [Fact]
    public void Library_change_diff_reports_added_removed_and_missing_members()
    {
        List<LibraryMemberState> before =
        [
            new(@"C:\home\.agents\skills\alpha", "alpha", Present: true),
            new(@"C:\home\.agents\skills\beta", "beta", Present: true),
            new(@"C:\home\.agents\skills\gamma", "gamma", Present: true),
        ];
        List<LibraryMemberState> after =
        [
            new(@"C:\HOME\.agents\skills\ALPHA", "alpha", Present: true),
            new(@"C:\home\.agents\skills\gamma", "gamma", Present: false),
            new(@"C:\home\.agents\skills\delta", "delta", Present: true),
        ];

        var changes = LibraryChangeDiff.Compute(before, after);

        Assert.True(changes.HasChanges);
        Assert.Equal(["delta"], changes.AddedSkills);
        Assert.Equal(["beta", "gamma"], changes.RemovedSkills);
    }

    [Fact]
    public void Library_change_diff_reports_no_changes_for_identical_membership()
    {
        List<LibraryMemberState> members = [new(@"C:\home\.agents\skills\alpha", "alpha", Present: true)];

        var changes = LibraryChangeDiff.Compute(members, members);

        Assert.False(changes.HasChanges);
        Assert.Empty(changes.AddedSkills);
        Assert.Empty(changes.RemovedSkills);
    }

    private static InventorySnapshot Snapshot(params InventoryEntry[] entries)
        => new(entries, DateTimeOffset.Now);

    private static InventoryEntry Entry(
        string folder,
        ManagementRecord? record = null,
        ProviderAttribution? attribution = null,
        InstallationHealth health = InstallationHealth.Healthy)
        => new()
        {
            FolderName = folder,
            LocalPath = $@"C:\home\.agents\skills\{folder}",
            RootKind = RootKind.CanonicalAgents,
            Kind = EntryKind.RealFolder,
            ManagementStatus = record is null ? ManagementStatus.Unmanaged : ManagementStatus.Managed,
            Health = health,
            Metadata = new SkillMetadata(MetadataReadStatus.Valid, folder, "Test skill.", null),
            Exposures = new Dictionary<Harness, HarnessExposure>
            {
                [Harness.OpenCode] = HarnessExposure.Canonical(),
                [Harness.Codex] = HarnessExposure.Canonical(),
                [Harness.ClaudeCode] = HarnessExposure.Canonical(),
                [Harness.GitHubCopilot] = HarnessExposure.Canonical(),
            },
            ManagementRecord = record,
            ProviderAttribution = attribution,
        };

    private static ManagementRecord Record(
        string provider,
        string owner,
        string repository,
        string normalizedSource,
        string folder)
        => new()
        {
            InstallationId = $"install-{folder}",
            CanonicalPath = $@"C:\home\.agents\skills\{folder}",
            Provenance = new ProvenanceInfo
            {
                SourceProvider = provider,
                OriginalReference = normalizedSource,
                NormalizedSource = normalizedSource,
                Host = "github.com",
                Owner = owner,
                Repository = repository,
                SourceSkillPath = folder,
                TrackingRule = "main",
                ResolvedCommit = "abcdef1234567890",
                SelectedContentIdentity = "content-identity",
                ProviderVersion = "1.0.0",
            },
            InstalledRevision = "abcdef1234567890",
            InstalledPayloadHash = "payload-hash",
            InstalledFileCount = 1,
            ProviderEvidence = "evidence",
        };
}
