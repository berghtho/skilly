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
                var githubInspection = github.Provider.Inspect(github.Reference).ValueOrThrow();
                github.Provider.Install(githubInspection, githubInspection.Skills).ValueOrThrow();
                var records = github.StateStore.Load().Records;
                SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(Dispatcher.CurrentDispatcher));
                var workflow = typeof(MainWindow).GetMethod("RunUpdateBatch", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
                RunWorkflow((Task)workflow.Invoke(main, [records, "Update all"])!);
                Assert.Equal(2, mainModel.OperationHistory.Count);
                Assert.Single(mainModel.OperationHistory, row => row.Status == "Preview failed");
                Assert.Single(mainModel.OperationHistory, row => row.Status == "Not run");
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
                main.Close();
                var historyWindow = new OperationHistoryWindow(mainModel.OperationHistory, null) { Width = 920, Height = 600 };
                Render(historyWindow, "update-history.png");
                var historyRows = Find<DataGrid>(historyWindow, "Skilly.OperationHistory");
                historyRows.SelectedIndex = 0;
                Assert.Equal(4, historyRows.Items.Count);
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
                app.Shutdown();
            }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(thread.Join(TimeSpan.FromSeconds(90)), "WPF rendering and workflows did not finish.");
        if (failure is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static void Pump() => Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.ApplicationIdle);

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
