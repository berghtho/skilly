using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Skilly.Providers;
using Skilly.Providers.SkillsCli;
using Skilly.Skills;
using Skilly.ViewModels;

namespace Skilly.App.Tests;

public sealed class SkillUsabilityUiTests
{
    [Fact]
    public void Inspector_search_preview_and_workbench_shortcuts_bind_in_rendered_windows()
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try
            {
                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/Skilly;component/Themes/Industry.xaml", UriKind.Relative) });
                foreach (var key in new[] { "HeadingFont", "BodyFont" })
                {
                    var typeface = new Typeface((FontFamily)app.FindResource(key), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);
                    Assert.True(typeface.TryGetGlyphTypeface(out var glyph));
                    Assert.Contains("Skilly;component/Fonts/", glyph.FontUri.OriginalString, StringComparison.OrdinalIgnoreCase);
                }
                using var skills = new SkillsCliProviderFixture();
                using var github = new GitHubProviderFixture();
                using var apm = new ApmProviderFixture();
                var inspection = new SkillsCliInspection("acme/library", "acme/library", "test",
                    [new SkillsCliSourceSkill("alpha", "Review code") { AlreadyInstalled = true }, new SkillsCliSourceSkill("beta", "Draw diagrams")]);
                var inspector = new SourceInspectionWindow(inspection, skills.Provider);
                Render(inspector, "inspector-default.png");
                var search = Find<TextBox>(inspector, "Skilly.SourceSearch");
                search.Text = "diagrams";
                Pump();
                var list = Find<ListBox>(inspector, "Skilly.SourceSkills");
                Assert.Single(list.Items.Cast<object>());
                list.SelectedIndex = 0;
                Pump();
                Assert.Contains("Draw diagrams", Find<TextBox>(inspector, "Skilly.SourcePreview").Text);
                var model = Assert.IsType<SkillsCliSourceInspectionViewModel>(inspector.DataContext);
                Assert.Equal(0, model.SelectedCount);
                Render(inspector, "inspector-search.png");
                inspector.Width = inspector.MinWidth;
                inspector.Height = inspector.MinHeight;
                Render(inspector, "inspector-minimum.png");
                inspector.Close();

                var entry = new InventoryEntry
                {
                    FolderName = "alpha", LocalPath = Path.Combine(skills.SourceRoot, "skills", "alpha"),
                    RootKind = RootKind.CanonicalAgents, Kind = EntryKind.RealFolder,
                    ManagementStatus = ManagementStatus.Unmanaged, Health = InstallationHealth.InvalidMetadata,
                    HealthDetail = "SKILL.md is missing its description.",
                    Metadata = new SkillMetadata(MetadataReadStatus.Invalid, "alpha", "A locally installed Skill", "missing description"),
                    Exposures = Enum.GetValues<Harness>().ToDictionary(harness => harness, _ => HarnessExposure.None()),
                };
                var snapshot = new InventorySnapshot([entry], DateTimeOffset.Now);
                var mainModel = new MainViewModel();
                mainModel.LoadInventory(snapshot);
                mainModel.SelectedRow = mainModel.Rows.OfType<InventoryRow>().Single();
                var main = new MainWindow(github.Log, mainModel, github.Provider, skills.Provider, apm.Provider,
                    new ProviderCheckRunner(github.Provider, github.StateStore), _ => snapshot,
                    new Skilly.Infrastructure.OperationHistoryStore(Path.Combine(skills.Root, "history.json")));
                Render(main, "workbench-actions.png");
                Assert.True(Find<Button>(main, "Skilly.OpenFolder").IsEnabled);
                Assert.True(Find<Button>(main, "Skilly.ReadSkillMarkdown").IsEnabled);
                Assert.True(Find<Button>(main, "Skilly.CopyPath").IsEnabled);
                Assert.False(Find<Button>(main, "Skilly.OpenSource").IsEnabled);
                main.Width = 1024; main.Height = 720; mainModel.ShowDetails = false;
                Render(main, "workbench-laptop.png");
                main.Width = 920; main.Height = 600;
                Render(main, "workbench-minimum.png");
                mainModel.ResizeColumn(1, 130, 70);
                Render(main, "workbench-resized-column.png");
                Assert.Equal(200, mainModel.ManagementWidth);
                // Render real update/health states and prove inline selection discards a previous batch.
                var demoRows = RedesignRows(entry);
                mainModel.LoadInventory(new InventorySnapshot(demoRows, DateTimeOffset.Now));
                mainModel.SelectedRow = mainModel.Rows.OfType<InventoryRow>().Single(row => row.Name == "code-review");
                main.Width = 1520; main.Height = 850; mainModel.ShowDetails = true;
                Render(main, "workbench-redesign.png");
                var skillRows = Find<ListBox>(main, "Skilly.SkillList");
                skillRows.SelectAll();
                var updateAction = Find<Button>(main, "Skilly.RowUpdate");
                var selectionMethod = typeof(MainWindow).GetMethod("SelectActionRow", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                Assert.True((bool)selectionMethod.Invoke(main, [updateAction])!);
                Assert.Single(mainModel.SelectedRows);
                Assert.Same(updateAction.DataContext, mainModel.SelectedRow);
                var selectedItem = (ListBoxItem)skillRows.ItemContainerGenerator.ContainerFromItem(mainModel.SelectedRow);
                var deselect = new System.Windows.Input.MouseButtonEventArgs(System.Windows.Input.Mouse.PrimaryDevice, 0, System.Windows.Input.MouseButton.Left)
                {
                    RoutedEvent = UIElement.PreviewMouseLeftButtonDownEvent, Source = selectedItem,
                };
                skillRows.RaiseEvent(deselect);
                Assert.Null(mainModel.SelectedRow);
                Assert.Empty(mainModel.SelectedRows);
                Assert.True((bool)selectionMethod.Invoke(main, [updateAction])!);
                mainModel.MaintenanceBusy = true;
                Render(main, "workbench-busy.png");
                Assert.False(updateAction.IsEnabled);
                Assert.False((bool)selectionMethod.Invoke(main, [updateAction])!);
                mainModel.MaintenanceBusy = false;
                mainModel.GroupByLibrary = true;
                mainModel.SelectedRow = mainModel.Rows.OfType<InventoryRow>().Single(row => row.Name == "diagnosing-bugs");
                Render(main, "workbench-grouped.png");
                mainModel.GroupByLibrary = false;
                mainModel.LoadInventory(snapshot);
                var githubInspection = github.Provider.Inspect(github.Reference).ValueOrThrow();
                github.Provider.Install(githubInspection, githubInspection.Skills).ValueOrThrow();
                var records = github.StateStore.Load().Records;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var workflow = typeof(MainWindow).GetMethod("RunUpdateBatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                RunWorkflow((Task)workflow.Invoke(main, [records, "Update all"])!);
                Assert.Equal(2, mainModel.OperationHistory.Count);
                Assert.All(mainModel.OperationHistory, row => Assert.Equal("Preview failed", row.Status));
                foreach (var record in records) Assert.Equal(record.InstalledPayloadHash, PayloadHasher.HashFolder(record.CanonicalPath));

                foreach (var record in records)
                    File.AppendAllText(Path.Combine(github.FixtureRoot, "files", "skills", Path.GetFileName(record.CanonicalPath), "SKILL.md"), "\nReviewed batch change.\n");
                github.SetCommit(GitHubProviderFixture.LaterCommitSha);
                new ProviderCheckRunner(github.Provider, github.StateStore).Refresh();
                records = github.StateStore.Load().Records;
                mainModel.ReviewUpdatesFirst = false;
                mainModel.PropertyChanged += (_, args) =>
                {
                    if (args.PropertyName == nameof(mainModel.OperationProgress) && mainModel.OperationProgress.StartsWith("Updating Skills 1/2"))
                        typeof(MainWindow).GetMethod("OnStopUpdates", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
                            .Invoke(main, [main, new RoutedEventArgs()]);
                };
                RunWorkflow((Task)workflow.Invoke(main, [records, "Update all"])!);
                Assert.Equal(4, mainModel.OperationHistory.Count);
                Assert.Single(mainModel.OperationHistory.Take(2), row => row.Status == "Updated");
                Assert.Single(mainModel.OperationHistory.Take(2), row => row.Status == "Not run");
                Assert.Contains("Updated 1/2 Skills", mainModel.Status.Message);
                Assert.Equal(1, mainModel.ProgressValue);
                Assert.Equal(2, mainModel.ProgressMaximum);
                snapshot = new InventoryScanner().Scan(github.Home, github.StateStore.Load());
                mainModel.LoadInventory(snapshot);
                Render(main, "workbench-inline-update.png");
                var pendingUpdate = Descendants((DependencyObject)main.Content).OfType<Button>().Single(button =>
                    System.Windows.Automation.AutomationProperties.GetAutomationId(button) == "Skilly.RowUpdate"
                    && button.DataContext is InventoryRow { CanUpdate: true });
                var updatedPath = ((InventoryRow)pendingUpdate.DataContext).Entry.LocalPath;
                pendingUpdate.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(() => !mainModel.MaintenanceBusy);
                Assert.Equal("Updated", mainModel.OperationHistory[0].Status);
                Assert.Equal(updatedPath, mainModel.OperationHistory[0].Path);
                Assert.Equal(GitHubProviderFixture.LaterCommitSha, github.StateStore.Load().Records.Single(record => record.CanonicalPath == updatedPath).InstalledRevision);
                main.Close();
                VerifyIndependentUpdateFailures(skills, apm);
                VerifyLibraryFailureAndLaunchCheckYield(github, apm);
                var historyWindow = new OperationHistoryWindow(mainModel.OperationHistory, null) { Width = 920, Height = 600 };
                Render(historyWindow, "update-history.png");
                var historyRows = Find<DataGrid>(historyWindow, "Skilly.OperationHistory");
                historyRows.SelectedIndex = 0;
                Assert.Equal(5, historyRows.Items.Count);
                Render(historyWindow, "update-history.png");
                historyWindow.Close();
                var oldFile = new PreviewFile("SKILL.md", 4, "old", "Old instruction\n");
                var newFile = new PreviewFile("SKILL.md", 4, "new", "New instruction\n");
                var changes = PreviewFiles.Compare([oldFile], [newFile]);
                var update = new UpdatePreview("github:alpha", "github", [new SkillUpdatePreview("alpha", "alpha", "C:/skills/alpha", "v1", "v2", "old", "new", changes)]);
                var preview = new UpdatePreviewWindow([update]);
                Render(preview, "update-preview.png");
                Assert.Contains("+ New instruction", Find<TextBox>(preview, "Skilly.PreviewDiff").Text);
                Assert.True(Find<Button>(preview, "Skilly.ApplyReviewedUpdates").IsEnabled);
                preview.Close();
                var blocked = new UpdatePreviewWindow([update with { Blocker = "APM membership changed." }]);
                Render(blocked, "update-blocked.png");
                Assert.False(Find<Button>(blocked, "Skilly.ApplyReviewedUpdates").IsEnabled);
                blocked.Close();
                var mixed = new UpdatePreviewWindow([update, update with { Blocker = "APM membership changed." }]);
                Render(mixed, "update-partially-blocked.png");
                Assert.Equal(2, Find<ListBox>(mixed, "Skilly.PreviewSkills").Items.Count);
                var applyMixed = Find<Button>(mixed, "Skilly.ApplyReviewedUpdates");
                Assert.True(applyMixed.IsEnabled);
                Assert.Contains("(1)", applyMixed.Content.ToString());
                Assert.Contains(Descendants((DependencyObject)mixed.Content).OfType<TextBlock>(), text => text.Text.EndsWith(" (blocked)"));
                mixed.Close();
                var library = new LibraryChangesWindow("acme/skills", 2, new LibraryChangeSummary(["skills/new-review"], ["skills/old-review"]));
                Render(library, "library-changes.png");
                library.Close();
                var document = new SkillDocumentWindow("C:/skills/code-review/SKILL.md", "---\nname: code-review\ndescription: Review changes against the repository standards.\n---\n\n# Code review\n\nRead the diff, inspect affected callers, and report actionable findings.\n\n<script>Embedded content stays text.</script>");
                Render(document, "skill-document.png");
                document.Close();
                var minimumPreview = new UpdatePreviewWindow([update]) { Width = 720, Height = 520 };
                Render(minimumPreview, "update-preview-minimum.png");
                Assert.True(Find<TextBox>(minimumPreview, "Skilly.PreviewDiff").ActualWidth >= 300);
                minimumPreview.Close();
                using var adoption = new GitHubProviderFixture();
                var adoptionInspection = adoption.Provider.Inspect(adoption.Reference).ValueOrThrow();
                var sourceFolder = Path.Combine(adoption.FixtureRoot, "files", "skills", "alpha");
                foreach (var sourceFile in Directory.GetFiles(sourceFolder, "*", SearchOption.AllDirectories))
                {
                    var destination = Path.Combine(adoption.CanonicalPath("alpha"), Path.GetRelativePath(sourceFolder, sourceFile));
                    Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                    File.Copy(sourceFile, destination);
                }
                var beforeAdoption = PayloadHasher.HashFolder(adoption.CanonicalPath("alpha"));
                var evidence = adoption.Provider.DiscoverAdoptions(adoptionInspection, new InventoryScanner().Scan(adoption.Home, adoption.StateStore.Load())).ValueOrThrow().Evidence;
                var adoptModel = new MainViewModel();
                adoptModel.LoadInventory(new InventoryScanner().Scan(adoption.Home, adoption.StateStore.Load(), evidence));
                var adoptWindow = new MainWindow(adoption.Log, adoptModel, adoption.Provider, skills.Provider, apm.Provider,
                    new ProviderCheckRunner(adoption.Provider, adoption.StateStore), _ => new InventoryScanner().Scan(adoption.Home, adoption.StateStore.Load()),
                    new Skilly.Infrastructure.OperationHistoryStore(Path.Combine(adoption.Root, "history.json")));
                Render(adoptWindow, "workbench-inline-adopt.png");
                var adoptButton = Find<Button>(adoptWindow, "Skilly.RowAdopt");
                Assert.Equal(Visibility.Visible, adoptButton.Visibility);
                adoptButton.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                WaitUntil(() => adoptModel.Status.Message.StartsWith("Adopted", StringComparison.Ordinal));
                Assert.Equal(beforeAdoption, PayloadHasher.HashFolder(adoption.CanonicalPath("alpha")));
                Assert.Equal(Skilly.State.OperationOutcome.Adopted, Assert.Single(adoption.StateStore.Load().Records).LastOperationOutcome);
                adoptWindow.Close();
                // Sharing dialogs use real archive payloads and the same import workflow as the toolbar.
                using var setSource = new InventoryFixture();
                using var setReceiver = new InventoryFixture();
                setSource.WriteSkill(".agents/skills", "shared-review", "Shared review", "Review the complete change and its supporting files.");
                setSource.WriteSkill(".agents/skills", "existing-skill", "Existing Skill", "An installed Skill is skipped on import.");
                setReceiver.WriteSkill(".agents/skills", "existing-skill", "Existing Skill", "Keep this local version.");
                var sourceSets = new SkillSetArchive(new Skilly.State.StateStore(github.Log, setSource.Root("state.json")), setSource.Home);
                var receiverStore = new Skilly.State.StateStore(github.Log, setReceiver.Root("state.json"));
                var receiverSets = new SkillSetArchive(receiverStore, setReceiver.Home);
                var archivePath = setSource.Root("team.skilly.zip");
                sourceSets.Export(archivePath, "Team review Skills", new InventoryScanner().Scan(setSource.Home).Entries);
                using var setPreview = receiverSets.Open(archivePath);
                var exportWindow = new SkillSetWindow("Team review Skills", [
                    new SkillSetChoice("one", "shared-review", "Review changes and supporting files.", "Canonical Skill", null, true),
                    new SkillSetChoice("two", "existing-skill", "A complete local snapshot.", "Canonical Skill", null, true)], false);
                Render(exportWindow, "skill-set-export.png");
                Find<Button>(exportWindow, "Skilly.SetSelectNone").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.False(Find<Button>(exportWindow, "Skilly.ApplySet").IsEnabled);
                Find<Button>(exportWindow, "Skilly.SetSelectAll").RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
                Assert.Equal(2, exportWindow.SelectedIds.Count);
                Find<TextBox>(exportWindow, "Skilly.SetName").Text = "";
                Assert.False(Find<Button>(exportWindow, "Skilly.ApplySet").IsEnabled);
                exportWindow.Close();
                var setWindow = new SkillSetWindow(setPreview.Name, setPreview.Skills.Select(skill => new SkillSetChoice(
                    skill.FolderName, skill.Name, skill.Description, $"{skill.FileCount} files", skill.Conflict, true)).ToList(), true);
                Render(setWindow, "skill-set-import.png");
                var skillTitle = Descendants(setWindow.Content as DependencyObject ?? setWindow).OfType<TextBlock>().Single(block => block.Text == "Shared review");
                Assert.True(skillTitle.ActualWidth > 100 && skillTitle.ActualHeight > 10);
                Assert.Equal("shared-review", Assert.Single(setWindow.SelectedIds));
                Assert.False(Find<CheckBox>(setWindow, "Skilly.SetSkill.existing-skill").IsEnabled);
                setWindow.Width = 580; setWindow.Height = 430;
                Render(setWindow, "skill-set-import-minimum.png");
                Assert.True(skillTitle.ActualWidth > 100 && skillTitle.ActualHeight > 10);
                var importSelection = setWindow.SelectedIds;
                setWindow.Close();
                var receiverModel = new MainViewModel();
                receiverModel.LoadInventory(new InventoryScanner().Scan(setReceiver.Home));
                var sharingMain = new MainWindow(github.Log, receiverModel, github.Provider, skills.Provider, apm.Provider,
                    new ProviderCheckRunner(github.Provider, github.StateStore), _ => new InventoryScanner().Scan(setReceiver.Home, receiverStore.Load()),
                    new Skilly.Infrastructure.OperationHistoryStore(setReceiver.Root("history.json")), receiverSets);
                Render(sharingMain, "skill-set-toolbar.png");
                Assert.True(Find<Button>(sharingMain, "Skilly.ImportSet").IsEnabled);
                Assert.True(Find<Button>(sharingMain, "Skilly.ExportSet").IsEnabled);
                receiverModel.InspectionInProgress = true; Pump();
                Assert.False(Find<Button>(sharingMain, "Skilly.ImportSet").IsEnabled);
                receiverModel.InspectionInProgress = false;
                var importWorkflow = typeof(MainWindow).GetMethod("ImportReviewedSkillSet", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                RunWorkflow((Task)importWorkflow.Invoke(sharingMain, [setPreview, importSelection])!);
                Assert.Contains("Imported 1 Skill(s) as Unmanaged", receiverModel.Status.Message);
                Assert.Equal(2, receiverModel.AllEntries.Count);
                Assert.Empty(receiverStore.Load().Records);
                receiverModel.EnterRecoveryRequired("test recovery"); Pump();
                Assert.False(Find<Button>(sharingMain, "Skilly.ImportSet").IsEnabled);
                Assert.False(Find<Button>(sharingMain, "Skilly.ExportSet").IsEnabled);
                sharingMain.Close();
                app.Shutdown();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromMinutes(3)), "WPF rendering and workflows did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

    private static void VerifyIndependentUpdateFailures(SkillsCliProviderFixture skills, ApmProviderFixture apm)
    {
        foreach (var failDuringApply in new[] { false, true }) VerifyFailure(failDuringApply);

        void VerifyFailure(bool failDuringApply)
        {
            using var github = new GitHubProviderFixture();
            var inspection = github.Provider.Inspect(github.Reference).ValueOrThrow();
            github.Provider.Install(inspection, inspection.Skills).ValueOrThrow();
            foreach (var record in github.StateStore.Load().Records)
                File.AppendAllText(Path.Combine(github.FixtureRoot, "files", "skills", Path.GetFileName(record.CanonicalPath), "SKILL.md"), "\nIndependent update.\n");
            github.SetCommit(GitHubProviderFixture.LaterCommitSha);
            new ProviderCheckRunner(github.Provider, github.StateStore).Refresh();
            var records = github.StateStore.Load().Records.OrderBy(record => record.CanonicalPath).ToList();
            if (!failDuringApply) File.AppendAllText(Path.Combine(records[0].CanonicalPath, "SKILL.md"), "\nLocal edit after Check.\n");
            var model = new MainViewModel { ReviewUpdatesFirst = false };
            if (failDuringApply) model.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(model.OperationProgress) && model.OperationProgress.StartsWith("Updating Skills"))
                    github.FailRequestsContaining(model.OperationProgress.EndsWith("alpha") ? "scripts/" : null);
            };
            InventorySnapshot Scan() => new InventoryScanner().Scan(github.Home, github.StateStore.Load());
            model.LoadInventory(Scan());
            var window = new MainWindow(github.Log, model, github.Provider, skills.Provider, apm.Provider,
                new ProviderCheckRunner(github.Provider, github.StateStore), _ => Scan(),
                new Skilly.Infrastructure.OperationHistoryStore(Path.Combine(github.Root, "history.json")));
            var workflow = typeof(MainWindow).GetMethod("RunUpdateBatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
            RunWorkflow((Task)workflow.Invoke(window, [records, "Update all"])!);
            Assert.Single(model.OperationHistory, row => row.Status == (failDuringApply ? "Failed" : "Preview failed") && row.Skill == "alpha");
            Assert.Single(model.OperationHistory, row => row.Status == "Updated" && row.Skill == "beta");
            Assert.Contains("Independent update", File.ReadAllText(Path.Combine(github.Home, ".agents", "skills", "beta", "SKILL.md")));
            Assert.DoesNotContain("Independent update", File.ReadAllText(Path.Combine(github.Home, ".agents", "skills", "alpha", "SKILL.md")));
            Assert.False(model.RecoveryRequired);
            Assert.Contains("Updated 1/2 Skills", model.Status.Message);
            Assert.Null(github.StateStore.Load().PendingOperation);
            window.Close();
        }
    }

    private static void VerifyLibraryFailureAndLaunchCheckYield(GitHubProviderFixture github, ApmProviderFixture apm)
    {
        using var skills = new SkillsCliProviderFixture();
        var inspection = skills.Provider.Inspect(SkillsCliProviderFixture.Source).ValueOrThrow();
        skills.Provider.Install(inspection, inspection.Skills).ValueOrThrow();
        foreach (var name in new[] { "alpha", "beta" }) skills.WriteSkill(name, "Updated Skill.");
        var runner = new ProviderCheckRunner(github.Provider, skills.StateStore, skills.Provider);
        runner.Refresh();
        var model = new MainViewModel { ReviewUpdatesFirst = false, GroupByLibrary = true };
        model.SetSkillsReadiness(new ProviderReadiness(true, SkillsCliClient.Package, "Provider ready."));
        InventorySnapshot Scan() => new InventoryScanner().Scan(skills.Home, skills.StateStore.Load());
        model.LoadInventory(Scan());
        var window = new MainWindow(skills.Log, model, github.Provider, skills.Provider, apm.Provider, runner, _ => Scan(),
            new Skilly.Infrastructure.OperationHistoryStore(Path.Combine(skills.Root, "history.json")));
        skills.Set("FAKE_SKILLS_INSPECTION_FAILURE", "1");
        var group = Assert.Single(model.Rows.OfType<LibraryGroupRow>());
        typeof(MainWindow).GetMethod("OnUpdateLibrary", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, [new Button { DataContext = group }, new RoutedEventArgs()]);
        WaitUntil(() => model.OperationHistory.Count == 2 && !model.MaintenanceBusy);
        Assert.All(model.OperationHistory, row => Assert.Equal("Updated", row.Status));
        Assert.Contains("Additional Skill discovery failed", model.Status.Message);
        Assert.False(model.HasProviderReadinessProblem);
        Assert.False(model.RecoveryRequired);

        skills.Set("FAKE_SKILLS_INSPECTION_FAILURE", null);
        foreach (var name in new[] { "alpha", "beta" }) skills.WriteSkill(name, "Second update.");
        runner.Refresh();
        model.LoadInventory(Scan());
        var marker = Path.Combine(skills.Root, "check-started.txt");
        skills.Set("FAKE_SKILLS_DELAY_COMMAND", "list");
        skills.Set("FAKE_SKILLS_STARTED_FILE", marker);
        var launch = (Task)typeof(MainWindow).GetMethod("RefreshChecks", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(window, [true])!;
        WaitUntil(() => File.Exists(marker));
        skills.Set("FAKE_SKILLS_DELAY_COMMAND", null);
        var workflow = typeof(MainWindow).GetMethod("RunUpdateBatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        RunWorkflow((Task)workflow.Invoke(window, [skills.StateStore.Load().Records, "Update all"])!);
        RunWorkflow(launch);
        Assert.All(model.OperationHistory.Take(2), row => Assert.Equal("Updated", row.Status));
        Assert.All(skills.StateStore.Load().Records, record =>
        {
            Assert.Equal(Skilly.State.OperationOutcome.Updated, record.LastOperationOutcome);
            Assert.Equal(Skilly.State.UpdateStatus.Current, record.LatestCheck!.Status);
            Assert.Contains("Second update", File.ReadAllText(Path.Combine(record.CanonicalPath, "SKILL.md")));
        });
        Assert.Null(skills.StateStore.Load().PendingOperation);
        window.Close();
    }

    private static void WaitUntil(Func<bool> predicate)
    {
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (!predicate() && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(10); }
        Assert.True(predicate(), "The routed row action did not complete.");
    }

    private static IReadOnlyList<InventoryEntry> RedesignRows(InventoryEntry local)
    {
        var names = new[] { "code-review", "architect", "diagnosing-bugs", "domain-modeling", "research", "show-me-your-work" };
        return names.Select((name, index) => new InventoryEntry
        {
            FolderName = name, LocalPath = local.LocalPath, RootKind = local.RootKind, Kind = local.Kind,
            ManagementStatus = ManagementStatus.Managed,
            Health = index == 2 ? InstallationHealth.ExposureProblem : InstallationHealth.Healthy,
            HealthDetail = index == 2 ? "Claude Code junction is missing." : null,
            Metadata = new SkillMetadata(MetadataReadStatus.Valid, name, "Review changes against repository standards and the originating specification. Report actionable findings with supporting evidence.", null),
            Exposures = Enum.GetValues<Harness>().ToDictionary(harness => harness, harness => index == 2 && harness == Harness.ClaudeCode
                ? new HarnessExposure(ExposureState.MissingJunction, "Claude Code junction is missing.") : HarnessExposure.Canonical()),
            ManagementRecord = new Skilly.State.ManagementRecord
            {
                InstallationId = name, CanonicalPath = local.LocalPath, InstalledRevision = "83beb551ee67", InstalledPayloadHash = "test", InstalledFileCount = 1, ProviderEvidence = "test",
                Provenance = new Skilly.State.ProvenanceInfo
                {
                    SourceProvider = "github", OriginalReference = "https://github.com/acme/skills", NormalizedSource = "github.com/acme/skills", Host = "github.com", Owner = "acme", Repository = "skills",
                    SourceSkillPath = "skills/" + name, TrackingRule = "main", ResolvedCommit = "83beb551ee67", SelectedContentIdentity = "test", ProviderVersion = "test",
                },
                LatestCheck = new Skilly.State.CheckSnapshot
                {
                    Status = index < 2 ? Skilly.State.UpdateStatus.UpdateAvailable : index == 3 ? Skilly.State.UpdateStatus.CheckFailed : Skilly.State.UpdateStatus.Current,
                    InstalledRevision = "83beb551ee67", AvailableRevision = "2e417d9ac284", CheckedAt = DateTimeOffset.Now,
                    Failure = index == 3 ? "The source could not be reached. Refresh checks." : null,
                },
            },
        }).Append(local).ToList();
    }

    private static void RunWorkflow(Task task)
    {
        var deadline = DateTime.UtcNow.AddSeconds(35);
        while (!task.IsCompleted && DateTime.UtcNow < deadline) { Pump(); Thread.Sleep(10); }
        Assert.True(task.IsCompleted, "Update workflow did not finish.");
        task.GetAwaiter().GetResult();
    }

    private static T Find<T>(DependencyObject root, string id) where T : DependencyObject
    {
        if (root is Window window) root = (DependencyObject)window.Content;
        foreach (var child in Descendants(root))
            if (child is T match && System.Windows.Automation.AutomationProperties.GetAutomationId(child) == id) return match;
        throw new InvalidOperationException($"Missing UI element {id}");
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        yield return root;
        for (var index = 0; index < VisualTreeHelper.GetChildrenCount(root); index++)
            foreach (var child in Descendants(VisualTreeHelper.GetChild(root, index))) yield return child;
    }

    private static void Render(Window window, string name)
    {
        var content = (FrameworkElement)window.Content;
        content.Measure(new Size(window.Width, window.Height));
        content.Arrange(new Rect(0, 0, window.Width, window.Height));
        content.UpdateLayout();
        Pump();
        var image = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        image.Render(content);
        var composed = new DrawingVisual();
        using (var drawing = composed.RenderOpen())
        {
            drawing.DrawRectangle(window.Background, null, new Rect(0, 0, window.Width, window.Height));
            drawing.DrawImage(image, new Rect(0, 0, window.Width, window.Height));
        }
        var result = new RenderTargetBitmap((int)window.Width, (int)window.Height, 96, 96, PixelFormats.Pbgra32);
        result.Render(composed);
        var path = Path.Combine(PackagedAppFixture.FindRepoRoot(), "artifacts", "skill-usability");
        Directory.CreateDirectory(path);
        using var output = File.Create(Path.Combine(path, name));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(result));
        encoder.Save(output);
    }
}
