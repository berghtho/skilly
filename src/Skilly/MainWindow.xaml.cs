using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
using System.Windows.Input;
using Skilly.Providers.GitHub;
using Skilly.Providers.SkillsCli;
using Skilly.Providers;
using Skilly.Skills;
using Skilly.Providers.Apm;

namespace Skilly;

public partial class MainWindow : Window
{
    private readonly Infrastructure.RollingLog _log;
    private readonly GitHubProvider _githubProvider;
    private readonly SkillsCliProvider _skillsProvider;
    private readonly ApmProvider _apmProvider;
    private readonly ManagedReinstallDispatcher _managedReinstall;
    private readonly ProviderCheckRunner _checkRunner;
    private readonly Func<IReadOnlyList<AdoptionEvidence>?, InventorySnapshot> _refreshInventory;
    private IReadOnlyList<AdoptionEvidence> _adoptionEvidence = [];
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);
    private CancellationTokenSource? _mutationCancellation;
    private volatile bool _mutationInProgress;
    private readonly Infrastructure.OperationHistoryStore _historyStore;
    private bool _stopAfterCurrent;
    private bool _closing;
    private readonly SkillSetArchive? _skillSets;

    public MainWindow(
        Infrastructure.RollingLog log,
        ViewModels.MainViewModel viewModel,
        GitHubProvider githubProvider,
        SkillsCliProvider skillsProvider,
        ApmProvider apmProvider,
        ProviderCheckRunner checkRunner,
        Func<IReadOnlyList<AdoptionEvidence>?, InventorySnapshot> refreshInventory,
        Infrastructure.OperationHistoryStore? historyStore = null,
        SkillSetArchive? skillSets = null)
    {
        InitializeComponent();
        AccessKeyManager.Register("i", InspectSourceButton);
        AccessKeyManager.Register("r", RefreshChecksButton);
        AccessKeyManager.Register("a", UpdateAllButton);
        _log = log;
        _githubProvider = githubProvider;
        _skillsProvider = skillsProvider;
        _apmProvider = apmProvider;
        _managedReinstall = new ManagedReinstallDispatcher(githubProvider, skillsProvider, apmProvider);
        _checkRunner = checkRunner;
        _refreshInventory = refreshInventory;
        _skillSets = skillSets;
        DataContext = viewModel;
        _historyStore = historyStore ?? new Infrastructure.OperationHistoryStore(System.IO.Path.Combine(Infrastructure.SkillyPaths.ApplicationRoot, "operation-history.json"));
        foreach (var entry in _historyStore.Load()) viewModel.OperationHistory.Add(entry);
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        if (Width < 1180) viewModel.ShowDetails = false;
        SizeChanged += (_, args) =>
        {
            if (args.WidthChanged && args.NewSize.Width < 1180 && args.PreviousSize.Width >= 1180) viewModel.ShowDetails = false;
        };
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closed += (_, _) =>
        {
            AccessKeyManager.Unregister("i", InspectSourceButton);
            AccessKeyManager.Unregister("r", RefreshChecksButton);
            AccessKeyManager.Unregister("a", UpdateAllButton);
            viewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _log.Info("Workbench window closed; shutdown proceeding.");
        };
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName != nameof(ViewModels.MainViewModel.Status)) return;
        Dispatcher.BeginInvoke(() =>
        {
            var peer = UIElementAutomationPeer.FromElement(StatusMessage) ?? new TextBlockAutomationPeer(StatusMessage);
            peer.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
        });
    }

    private void OnCloseWindow(object sender, RoutedEventArgs e) => Close();

    private void OnShellSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var stacked = e.NewSize.Width < 1120;
        Grid.SetRow(HeaderToolbar, stacked ? 1 : 0);
        Grid.SetColumn(HeaderToolbar, stacked ? 0 : 1);
        Grid.SetColumnSpan(HeaderToolbar, stacked ? 3 : 1);
        HeaderToolbar.Margin = stacked ? new Thickness(0, 7, 0, -5) : new Thickness(0, -5, 0, -5);
    }

    private void OnResizeColumn(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
    {
        var thumb = (System.Windows.Controls.Primitives.Thumb)sender;
        var grid = (Grid)thumb.Parent;
        var index = Grid.GetColumn(thumb);
        ((ViewModels.MainViewModel)DataContext).ResizeColumn(index, grid.ColumnDefinitions[index].ActualWidth, e.HorizontalChange);
    }

    private void OnResizeDetails(object sender, System.Windows.Controls.Primitives.DragDeltaEventArgs e)
        => ((ViewModels.MainViewModel)DataContext).ResizeDetails(e.HorizontalChange);

    private void OnOpenSkillFolder(object sender, RoutedEventArgs e)
        => NavigateSelected(row =>
        {
            if (!System.IO.Directory.Exists(row.Entry.LocalPath)) throw new System.IO.DirectoryNotFoundException("The Skill folder is missing. Refresh checks.");
            var start = new System.Diagnostics.ProcessStartInfo("explorer.exe") { UseShellExecute = true };
            start.ArgumentList.Add(System.IO.Path.GetFullPath(row.Entry.LocalPath));
            System.Diagnostics.Process.Start(start);
        });

    private void OnOpenSkillSource(object sender, RoutedEventArgs e)
        => NavigateSelected(row =>
        {
            var url = row.SourceUrl ?? throw new InvalidOperationException("This source has no supported HTTPS address.");
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        });

    private void OnCopySkillPath(object sender, RoutedEventArgs e)
        => NavigateSelected(row =>
        {
            Clipboard.SetText(row.Entry.LocalPath);
            ((ViewModels.MainViewModel)DataContext).Announce("Skill path copied.");
        });

    private void OnReadSkillMarkdown(object sender, RoutedEventArgs e)
        => NavigateSelected(row =>
        {
            var path = System.IO.Path.Combine(row.Entry.LocalPath, "SKILL.md");
            var text = SkillMarkdownPreview.ReadFile(path);
            new SkillDocumentWindow(path, text) { Owner = this }.ShowDialog();
        });

    private void NavigateSelected(Action<ViewModels.InventoryRow> action)
    {
        var viewModel = (ViewModels.MainViewModel)DataContext;
        if (viewModel.SelectedRow is not { } row) return;
        try { action(row); }
        catch (Exception exception)
        {
            _log.Error("Skill navigation failed.", exception);
            viewModel.Announce($"Could not open or copy the selected Skill: {exception.Message}");
        }
    }

    // A chromeless maximized window overhangs the screen by the resize border.
    private void OnStateChanged(object? sender, EventArgs e)
        => RootShell.Margin = WindowState == WindowState.Maximized ? new Thickness(7) : new Thickness(0);

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _log.Info("Workbench window loaded.");
        await RefreshChecks(background: true);
    }

    private async void OnInspectSource(object sender, RoutedEventArgs e)
    {
        var viewModel = (ViewModels.MainViewModel)DataContext;
        if (string.Equals(viewModel.SelectedSourceProvider, SkillsCliClient.Package, StringComparison.Ordinal))
        {
            await InspectSkillsSource(viewModel);
            return;
        }
        if (string.Equals(viewModel.SelectedSourceProvider, ApmClient.Provider, StringComparison.Ordinal))
        {
            await InspectApmSource(viewModel);
            return;
        }
        if (!GitHubSourceReference.TryParse(viewModel.SourceText, out var reference, out var parseError))
        {
            viewModel.Announce($"Source inspection failed. {parseError} Nothing changed.");
            return;
        }

        if (!await _maintenanceGate.WaitAsync(0))
        {
            viewModel.Announce("Another maintenance operation is already running. Nothing changed.");
            return;
        }

        viewModel.InspectionInProgress = true;
        viewModel.Announce("Inspecting GitHub source read-only. Nothing has changed.");
        try
        {
            var inspectionResult = await Task.Run(() => _githubProvider.Inspect(reference));
            if (!inspectionResult.Succeeded)
            {
                viewModel.Announce($"GitHub source inspection failed. {inspectionResult.Diagnostics} Nothing changed.");
                return;
            }

            var inspection = inspectionResult.Value!;
            var discoveryResult = await Task.Run(() => _githubProvider.DiscoverAdoptions(inspection, _refreshInventory(null)));
            var discovery = discoveryResult.Succeeded ? discoveryResult.Value! : new AdoptionDiscovery([], [discoveryResult.Diagnostics]);
            var dialog = new SourceInspectionWindow(inspection, _githubProvider, viewModel.MutationsAllowed, OccupiedSkillFolders()) { Owner = this };
            var installed = dialog.ShowDialog() == true;
            await dialog.OperationCompletion;
            if (installed)
            {
                _adoptionEvidence = [];
                viewModel.LoadInventory(RefreshInventory());
                viewModel.Announce($"Installed {dialog.InstalledCount} Skill(s) from GitHub and verified all Harness Exposures.");
            }
            else
            {
                _adoptionEvidence = discovery.Evidence;
                viewModel.LoadInventory(RefreshInventory());
                viewModel.Announce(
                    $"Read-only inspection found {inspection.Skills.Count} Source Skill(s) and verified {discovery.Evidence.Count} Adoption candidate(s). Nothing changed."
                    + (discovery.Diagnostics.Count == 0 ? string.Empty : $" {discovery.Diagnostics[0]}"));
            }
            ApplyRecoveryMode(viewModel);
        }
        catch (Exception exception)
        {
            _log.Error("GitHub source inspection failed.", exception);
            viewModel.Announce($"GitHub source inspection failed. {exception.Message} Nothing changed.");
        }
        finally
        {
            viewModel.InspectionInProgress = false;
            _maintenanceGate.Release();
        }
    }

    private async Task InspectApmSource(ViewModels.MainViewModel viewModel)
    {
        if (!await _maintenanceGate.WaitAsync(0))
        {
            viewModel.Announce("Another maintenance operation is already running. Nothing changed.");
            return;
        }
        viewModel.InspectionInProgress = true;
        viewModel.Announce("Inspecting source through Microsoft APM in an isolated home. User state has not changed.");
        try
        {
            var result = await Task.Run(() => _apmProvider.Inspect(viewModel.SourceText));
            if (!result.Succeeded)
            {
                viewModel.SetApmReadiness(new ProviderReadiness(false, ApmClient.Provider, $"Microsoft APM source readiness failed: {result.Diagnostics}"));
                viewModel.Announce($"Microsoft APM source inspection failed. {result.Diagnostics} Nothing changed.");
                return;
            }
            viewModel.SetApmReadiness(_apmProvider.GetReadiness());
            var inspection = result.Value!;
            var dialog = new SourceInspectionWindow(inspection, _apmProvider, viewModel.MutationsAllowed, OccupiedSkillFolders()) { Owner = this };
            var installed = dialog.ShowDialog() == true;
            await dialog.OperationCompletion;
            _adoptionEvidence = [];
            viewModel.LoadInventory(RefreshInventory());
            viewModel.Announce(installed
                ? $"Installed {dialog.InstalledCount} Skill(s) through Microsoft APM; manifest, lock, canonical content, state, and Harness Exposures were verified."
                : $"Read-only Microsoft APM inspection found {inspection.Skills.Count} Source Skill(s). User state was not changed.");
            ApplyRecoveryMode(viewModel);
        }
        catch (Exception exception)
        {
            _log.Error("Microsoft APM source inspection failed.", exception);
            viewModel.Announce($"Microsoft APM source inspection failed. {exception.Message} Nothing changed.");
        }
        finally
        {
            viewModel.InspectionInProgress = false;
            _maintenanceGate.Release();
        }
    }

    private async Task InspectSkillsSource(ViewModels.MainViewModel viewModel)
    {
        if (!await _maintenanceGate.WaitAsync(0))
        {
            viewModel.Announce("Another maintenance operation is already running. Nothing changed.");
            return;
        }
        viewModel.InspectionInProgress = true;
        viewModel.Announce($"Inspecting source read-only through {SkillsCliClient.Package}. Nothing has changed.");
        try
        {
            var result = await Task.Run(() => _skillsProvider.Inspect(viewModel.SourceText));
            if (!result.Succeeded)
            {
                viewModel.SetSkillsReadiness(new Providers.ProviderReadiness(
                    false,
                    SkillsCliClient.Package,
                    $"{SkillsCliClient.Package} source readiness failed: {result.Diagnostics}"));
                viewModel.Announce($"{SkillsCliClient.Package} source inspection failed. {result.Diagnostics} Nothing changed.");
                return;
            }
            viewModel.SetSkillsReadiness(_skillsProvider.GetReadiness());
            var inspection = result.Value!;
            var dialog = new SourceInspectionWindow(inspection, _skillsProvider, viewModel.MutationsAllowed) { Owner = this };
            var installed = dialog.ShowDialog() == true;
            await dialog.OperationCompletion;
            _adoptionEvidence = [];
            viewModel.LoadInventory(RefreshInventory());
            viewModel.Announce(installed
                ? $"Installed {dialog.InstalledCount} Skill(s) through {SkillsCliClient.Package}; canonical content, provider lock, authority, and Harness Exposures were verified."
                : $"Read-only {SkillsCliClient.Package} inspection found {inspection.Skills.Count} Source Skill(s). Nothing changed.");
            ApplyRecoveryMode(viewModel);
        }
        catch (Exception exception)
        {
            _log.Error($"{SkillsCliClient.Package} source inspection failed.", exception);
            viewModel.Announce($"{SkillsCliClient.Package} source inspection failed. {exception.Message} Nothing changed.");
        }
        finally
        {
            viewModel.InspectionInProgress = false;
            _maintenanceGate.Release();
        }
    }

    private ISet<string> OccupiedSkillFolders()
        => _refreshInventory(null).Entries
            .Where(entry => entry.RootKind is RootKind.CanonicalAgents or RootKind.ClaudeSkills
                && entry.Health != InstallationHealth.Missing)
            .Select(entry => entry.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);

    private void OnSkillListSelectionChanged(object sender, SelectionChangedEventArgs e)
        => ((ViewModels.MainViewModel)DataContext).SelectedRows =
            SkillList.SelectedItems.OfType<ViewModels.InventoryRow>().ToList();

    private void OnSkillListMouseDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (System.Windows.Input.Keyboard.Modifiers != System.Windows.Input.ModifierKeys.None) return;
        var source = e.OriginalSource as DependencyObject;
        while (source is not null && source != SkillList)
        {
            // Child actions keep their own click; Ctrl/Shift keep batch selection.
            if (source is System.Windows.Controls.Primitives.ButtonBase) return;
            if (source is ListBoxItem item)
            {
                if (item.DataContext is ViewModels.InventoryRow && item.IsSelected && SkillList.SelectedItems.Count == 1)
                {
                    SkillList.UnselectAll();
                    e.Handled = true;
                }
                return;
            }
            source = source is System.Windows.Media.Visual
                ? System.Windows.Media.VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
    }

    private bool SelectActionRow(object sender)
    {
        if (((ViewModels.MainViewModel)DataContext).MutationsAllowed
            && sender is FrameworkElement { DataContext: ViewModels.InventoryRow row })
        {
            SkillList.UnselectAll();
            SkillList.SelectedItem = row;
            return true;
        }
        return false;
    }

    private void OnRowUpdate(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (SelectActionRow(sender)) OnUpdateSelected(sender, e);
    }

    private void OnRowAdopt(object sender, RoutedEventArgs e)
    {
        e.Handled = true;
        if (SelectActionRow(sender)) OnAdoptSelected(sender, e);
    }

    private void OnSortHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string tag } && Enum.TryParse<ViewModels.InventorySortColumn>(tag, out var column))
        {
            ((ViewModels.MainViewModel)DataContext).SortBy(column);
        }
    }

    private async void OnRefreshChecks(object sender, RoutedEventArgs e)
        => await RefreshChecks(background: false);

    private InventorySnapshot RefreshInventory() => _refreshInventory(_adoptionEvidence);

    private async Task RefreshChecks(bool background)
    {
        var viewModel = (ViewModels.MainViewModel)DataContext;
        if (viewModel.RecoveryRequired)
        {
            if (!background)
            {
                viewModel.Announce($"Recovery Required; Skilly is read-only. {viewModel.RecoveryDiagnostic}");
            }
            return;
        }
        if (!await _maintenanceGate.WaitAsync(0))
        {
            if (!background)
            {
                viewModel.Announce("Another maintenance operation is already running. Nothing changed.");
            }
            return;
        }

        RefreshChecksButton.IsEnabled = false;
        viewModel.Announce(background
            ? "Running the launch update Check in the background. Nothing has changed."
            : "Refreshing update checks read-only. Nothing has changed.");
        try
        {
            var result = await Task.Run(_checkRunner.Refresh);
            viewModel.LoadInventory(RefreshInventory());
            viewModel.Announce(result.FailureCount == 0
                ? $"Checked {result.CheckedCount} managed Skill(s) across available providers. Installed content was not changed."
                : $"Checked {result.CheckedCount} managed Skill(s); {result.FailureCount} check(s) failed and prior results are stale. Installed content was not changed.");
        }
        catch (Exception exception)
        {
            _log.Error("GitHub check refresh failed.", exception);
            viewModel.Announce($"Check refresh failed. {exception.Message} Installed content was not changed.");
        }
        finally
        {
            RefreshChecksButton.IsEnabled = true;
            _maintenanceGate.Release();
        }
    }

    private async void OnUpdateSelected(object sender, RoutedEventArgs e)
    {
        var vm = (ViewModels.MainViewModel)DataContext;
        if (vm.SelectedRow is { CanUpdate: true, Entry.ManagementRecord: { } record })
            await RunUpdateBatch([record], "Update Skill");
    }

    private async void OnUpdateAll(object sender, RoutedEventArgs e)
    {
        var vm = (ViewModels.MainViewModel)DataContext;
        await RunUpdateBatch(vm.UpdatableRows.Select(row => row.Entry.ManagementRecord!).ToList(), "Update all");
    }

    private async void OnUpdateLibrary(object sender, RoutedEventArgs e)
    {
        var vm = (ViewModels.MainViewModel)DataContext;
        if ((sender as FrameworkElement)?.DataContext is not ViewModels.LibraryGroupRow { Key: { } key } group) return;
        await RunUpdateBatch(vm.LibraryMembers(key).Where(row => row.CanUpdate)
            .Select(row => row.Entry.ManagementRecord!).ToList(), "Update Library " + group.Label);
    }

    private async Task RunUpdateBatch(IReadOnlyList<State.ManagementRecord> requested, string operation)
    {
        var vm = (ViewModels.MainViewModel)DataContext;
        if (!vm.MutationsAllowed || requested.Count == 0) { vm.Announce("No eligible update is available."); return; }
        if (!await _maintenanceGate.WaitAsync(0)) { vm.Announce("Another maintenance operation is running."); return; }
        var targets = requested.GroupBy(record => record.Provenance.SourceProvider == ApmClient.ProviderId
            ? "apm:" + record.Provenance.Repository : record.Provenance.SourceProvider + ":" + record.InstallationId,
            StringComparer.OrdinalIgnoreCase).Select(group => group.First()).ToList();
        var prepared = new List<(State.ManagementRecord Record, UpdatePreview Preview)>();
        var runId = Guid.NewGuid().ToString("N");
        var started = DateTimeOffset.Now;
        var completed = 0;
        _stopAfterCurrent = false;
        vm.MaintenanceBusy = true;
        RefreshChecksButton.IsEnabled = false;
        vm.ProgressValue = 0;
        vm.ProgressMaximum = targets.Count;
        try
        {
            var apmPackages = requested.Where(record => record.Provenance.SourceProvider == ApmClient.ProviderId)
                .Select(record => record.Provenance.Repository).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var known = requested.Concat(RefreshInventory().Entries.Select(entry => entry.ManagementRecord)
                .OfType<State.ManagementRecord>().Where(record => record.Provenance.SourceProvider == ApmClient.ProviderId
                    && apmPackages.Contains(record.Provenance.Repository))).DistinctBy(record => record.InstallationId).ToList();
            foreach (var record in known.AsEnumerable().Reverse())
                QueueHistory(new Infrastructure.OperationEntry(runId, started, operation,
                    System.IO.Path.GetFileName(record.CanonicalPath), record.CanonicalPath, "Pending", "Preparing update preview."));
            SaveHistory();
            foreach (var record in targets)
            {
                vm.OperationProgress = $"Preparing preview {prepared.Count + 1}/{targets.Count}: {System.IO.Path.GetFileName(record.CanonicalPath)}";
                vm.Announce(vm.OperationProgress);
                var result = await Task.Run(() => record.Provenance.SourceProvider switch
                {
                    "github" => _githubProvider.PreviewUpdate(record),
                    "skills" => _skillsProvider.PreviewUpdate(record),
                    ApmClient.ProviderId => _apmProvider.PreviewUpdate(record),
                    _ => ProviderResult<UpdatePreview>.Failure("Unsupported update provider."),
                });
                if (_closing || _stopAfterCurrent) { vm.Announce("Update preparation stopped. Installed content was not changed."); return; }
                if (!result.Succeeded)
                {
                    foreach (var affected in known.Where(candidate => candidate.InstallationId == record.InstallationId
                        || (record.Provenance.SourceProvider == ApmClient.ProviderId && candidate.Provenance.SourceProvider == ApmClient.ProviderId
                            && candidate.Provenance.Repository.Equals(record.Provenance.Repository, StringComparison.OrdinalIgnoreCase))))
                        QueueHistory(new Infrastructure.OperationEntry(runId, started, operation,
                            System.IO.Path.GetFileName(affected.CanonicalPath), affected.CanonicalPath, "Preview failed", result.Diagnostics, DateTimeOffset.Now));
                    SaveHistory();
                    vm.Announce("Update preview failed. " + result.Diagnostics);
                    return;
                }
                prepared.Add((record, result.Value!));
                foreach (var skill in result.Value!.Skills)
                    QueueHistory(new Infrastructure.OperationEntry(runId, started, operation,
                        skill.Name, skill.LocalPath, "Pending", $"{skill.InstalledRevision} → {skill.TargetRevision}"));
                SaveHistory();
                vm.ProgressValue = prepared.Count;
            }
            var previews = prepared.Select(item => item.Preview).ToList();
            if (vm.ReviewUpdatesFirst || previews.Any(preview => !preview.CanApply))
            {
                vm.OperationProgress = "Review the changes before applying.";
                if (new UpdatePreviewWindow(previews) { Owner = this }.ShowDialog() != true)
                { vm.Announce("Update cancelled. Installed content was not changed."); return; }
            }
            if (_closing) return;
            vm.ProgressValue = 0;
            vm.ProgressMaximum = previews.Sum(preview => preview.Skills.Count);
            SaveHistory();
            BeginMutation();
            foreach (var item in prepared)
            {
                if (_stopAfterCurrent || _closing) break;
                var names = string.Join(", ", item.Preview.Skills.Select(skill => skill.Name));
                var range = item.Preview.Skills.Count == 1 ? $"{completed + 1}" : $"{completed + 1}-{completed + item.Preview.Skills.Count}";
                vm.OperationProgress = $"Updating Skills {range}/{vm.ProgressMaximum}: {names}";
                vm.Announce(vm.OperationProgress);
                SetHistoryResult(runId, item.Preview.Skills, "Running", vm.OperationProgress);
                var result = await RunProviderUpdate(item.Record, item.Preview);
                if (!result.Succeeded)
                {
                    SetHistoryResult(runId, item.Preview.Skills, "Failed", result.Diagnostics);
                    vm.Announce($"Stopped after {completed}/{vm.ProgressMaximum} Skills. {result.Diagnostics} See History for each result.");
                    return;
                }
                completed += item.Preview.Skills.Count;
                vm.ProgressValue = completed;
                SetHistoryResult(runId, item.Preview.Skills, "Updated", "Reviewed content and provider postconditions verified.");
            }
            vm.Announce($"Updated {completed}/{vm.ProgressMaximum} Skills. " + (completed < vm.ProgressMaximum ? "Remaining updates were not run. " : string.Empty) + "See History for each result.");
        }
        catch (Exception exception)
        {
            _log.Error("Update workflow failed.", exception);
            foreach (var item in prepared) SetHistoryResult(runId, item.Preview.Skills, "Failed", exception.Message, onlyRunning: true);
            vm.Announce($"Update stopped after {completed} Skills. {exception.Message} See History for details.");
        }
        finally
        {
            for (var index = 0; index < vm.OperationHistory.Count; index++)
            {
                var row = vm.OperationHistory[index];
                if (row.RunId == runId && row.Status == "Pending") vm.OperationHistory[index] = row with
                { Status = "Not run", Detail = "Batch stopped before this Skill was changed.", FinishedAt = DateTimeOffset.Now };
            }
            SaveHistory();
            EndMutation();
            var finalMessage = vm.Status.Message;
            try { vm.LoadInventory(RefreshInventory()); ApplyRecoveryMode(vm); if (!vm.RecoveryRequired) vm.Announce(finalMessage); }
            catch (Exception exception) { _log.Error("Inventory refresh after update failed.", exception); vm.Announce("Refresh inventory failed: " + exception.Message); }
            vm.MaintenanceBusy = false;
            RefreshChecksButton.IsEnabled = true;
            _maintenanceGate.Release();
            if (_historyStore.Notice is { } notice) vm.Announce(vm.Status.Message + " " + notice);
        }
    }

    private async Task<ProviderResult<UpdateResult>> RunProviderUpdate(State.ManagementRecord record, UpdatePreview preview)
    {
        if (record.Provenance.SourceProvider == "github")
            return await Task.Run(() => _githubProvider.Update(record, _mutationCancellation!.Token, preview));
        if (record.Provenance.SourceProvider == "skills")
        {
            var result = await Task.Run(() => _skillsProvider.Update(record, _mutationCancellation!.Token, preview));
            return result.Succeeded ? ProviderResult<UpdateResult>.Success(new UpdateResult(result.Value!.InstallationId, result.Value.InstalledRevision), result.Diagnostics)
                : ProviderResult<UpdateResult>.Failure(result.Diagnostics);
        }
        var apmResult = await Task.Run(() => _apmProvider.Update(record, _mutationCancellation!.Token, preview));
        return apmResult.Succeeded ? ProviderResult<UpdateResult>.Success(new UpdateResult(apmResult.Value!.InstallationId, apmResult.Value.InstalledRevision), apmResult.Diagnostics)
            : ProviderResult<UpdateResult>.Failure(apmResult.Diagnostics);
    }

    private void SetHistoryResult(string runId, IReadOnlyList<SkillUpdatePreview> skills, string status, string detail, bool onlyRunning = false)
    {
        var vm = (ViewModels.MainViewModel)DataContext;
        var paths = skills.Select(skill => skill.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < vm.OperationHistory.Count; index++)
        {
            var row = vm.OperationHistory[index];
            if (row.RunId != runId || !paths.Contains(row.Path) || (onlyRunning && row.Status != "Running")) continue;
            var skill = skills.Single(skill => string.Equals(skill.LocalPath, row.Path, StringComparison.OrdinalIgnoreCase));
            vm.OperationHistory[index] = row with { Status = status, Detail = $"{skill.InstalledRevision} → {skill.TargetRevision}\n{detail}", FinishedAt = status == "Running" ? null : DateTimeOffset.Now };
        }
        SaveHistory();
    }

    private void SaveHistory()
    {
        _historyStore.Save(((ViewModels.MainViewModel)DataContext).OperationHistory);
        if (_historyStore.Notice is { } notice) _log.Error(notice);
    }

    private void QueueHistory(Infrastructure.OperationEntry entry)
    {
        var rows = ((ViewModels.MainViewModel)DataContext).OperationHistory;
        for (var index = 0; index < rows.Count; index++)
            if (rows[index].RunId == entry.RunId && string.Equals(rows[index].Path, entry.Path, StringComparison.OrdinalIgnoreCase))
            { rows[index] = entry; return; }
        rows.Insert(0, entry);
    }

    private void OnShowHistory(object sender, RoutedEventArgs e)
        => new OperationHistoryWindow(((ViewModels.MainViewModel)DataContext).OperationHistory, _historyStore.Notice) { Owner = this }.Show();

    private void OnStopUpdates(object sender, RoutedEventArgs e)
    {
        _stopAfterCurrent = true;
        ((ViewModels.MainViewModel)DataContext).Announce("Stopping after the current provider operation. Remaining updates will not run.");
    }

    private async void OnAdoptSelected(object sender, RoutedEventArgs e)
    {
        var viewModel = (ViewModels.MainViewModel)DataContext;
        var targets = viewModel.SelectedRows
            .Where(static row => row.CanAdopt && row.Entry.AdoptionEvidence is not null)
            .Select(static row => row.Entry.AdoptionEvidence!)
            .ToList();
        if (targets.Count == 0 || !viewModel.MutationsAllowed)
        {
            viewModel.Announce("Adoption is unavailable for the selected Skill(s). Nothing changed.");
            return;
        }
        if (!await _maintenanceGate.WaitAsync(0))
        {
            viewModel.Announce("Another maintenance operation is already running. Nothing changed.");
            return;
        }

        BeginMutation();
        viewModel.Announce(targets.Count == 1
            ? "Adopting the selected exact verified Skill. Existing Skill content will be preserved."
            : $"Adopting {targets.Count} exact verified Skill(s). Existing Skill content will be preserved.");
        var adopted = new List<string>();
        try
        {
            foreach (var evidence in targets)
            {
                var provider = evidence.ProposedRecord.Provenance.SourceProvider;
                var result = string.Equals(provider, "github", StringComparison.Ordinal)
                    ? await Task.Run(() => _githubProvider.Adopt(evidence, _mutationCancellation!.Token))
                    : await Task.Run(() => _githubProvider.AdoptVerifiedProviderEvidence(
                        evidence,
                        () => _refreshInventory(null).Entries.SingleOrDefault(entry =>
                            string.Equals(entry.LocalPath, evidence.ProposedRecord.CanonicalPath, StringComparison.OrdinalIgnoreCase))?.AdoptionEvidence,
                        _mutationCancellation!.Token));
                _adoptionEvidence = _adoptionEvidence.Where(candidate =>
                    !string.Equals(
                        candidate.ProposedRecord.CanonicalPath,
                        evidence.ProposedRecord.CanonicalPath,
                        StringComparison.OrdinalIgnoreCase)).ToList();
                if (!result.Succeeded)
                {
                    viewModel.LoadInventory(RefreshInventory());
                    ApplyRecoveryMode(viewModel);
                    viewModel.Announce(
                        $"Adoption failed at '{evidence.ProposedRecord.CanonicalPath}' after {adopted.Count} Skill(s) were adopted. "
                        + $"{result.Diagnostics} The failed installation remains Unmanaged; Skill content was not rewritten.");
                    return;
                }

                adopted.Add(result.Value!.ExactPath);
            }

            viewModel.LoadInventory(RefreshInventory());
            viewModel.Announce(adopted.Count == 1
                ? $"Adopted the selected Skill at {adopted[0]}; verified Provenance was recorded and Skill content was preserved."
                : $"Adopted {adopted.Count} Skill(s); verified Provenance was recorded and Skill content was preserved.");
        }
        catch (Exception exception)
        {
            _log.Error("Adoption failed.", exception);
            _adoptionEvidence = [];
            viewModel.LoadInventory(RefreshInventory());
            ApplyRecoveryMode(viewModel);
            viewModel.Announce($"Adoption failed after {adopted.Count} Skill(s) were adopted. {exception.Message} Skill content was not rewritten.");
        }
        finally
        {
            EndMutation();
            _maintenanceGate.Release();
        }
    }

    private async void OnManagedReinstallSelected(object sender, RoutedEventArgs e)
    {
        var viewModel = (ViewModels.MainViewModel)DataContext;
        var row = viewModel.SelectedRow;
        var record = row?.Entry.ManagementRecord;
        if (record is null || row?.CanManagedReinstall != true || !viewModel.MutationsAllowed)
        {
            viewModel.Announce("Managed Reinstall is unavailable for the selected Skill. Nothing changed.");
            return;
        }
        if (!await _maintenanceGate.WaitAsync(0))
        {
            viewModel.Announce("Another maintenance operation is already running. Nothing changed.");
            return;
        }

        try
        {
            viewModel.Announce("Preparing a verified Managed Reinstall decision. Nothing has changed.");
            var planned = await Task.Run(() => _managedReinstall.Plan(record));
            if (!planned.Succeeded)
            {
                viewModel.Announce($"Managed Reinstall preparation failed. {planned.Diagnostics} Nothing changed.");
                return;
            }

            var plan = planned.Value!;
            var affectedPaths = string.Join(Environment.NewLine, plan.AffectedPaths);
            var decision = MessageBox.Show(
                this,
                $"Managed Reinstall will replace these exact provider-owned paths:\n\n{affectedPaths}\n\nVerified replacement revision:\n{plan.Revision}\n\nCurrent content and provider state will be snapshotted and replaced cleanly through the owning provider. Files will not be merged.",
                "Confirm Managed Reinstall",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel);
            if (decision != MessageBoxResult.OK)
            {
                viewModel.Announce("Managed Reinstall cancelled. Nothing changed.");
                return;
            }

            BeginMutation();
            var result = await Task.Run(() => _managedReinstall.Execute(plan, _mutationCancellation!.Token));
            viewModel.LoadInventory(RefreshInventory());
            if (!result.Succeeded)
            {
                ApplyRecoveryMode(viewModel);
            }
            viewModel.Announce(result.Succeeded
                ? $"Managed Reinstall completed at {plan.ExactPath} from verified revision {plan.Revision[..Math.Min(12, plan.Revision.Length)]}; no files were merged."
                : $"Managed Reinstall failed. {result.Diagnostics}");
        }
        catch (Exception exception)
        {
            _log.Error("Managed Reinstall failed.", exception);
            viewModel.LoadInventory(RefreshInventory());
            viewModel.Announce($"Managed Reinstall failed. {exception.Message}");
        }
        finally
        {
            EndMutation();
            _maintenanceGate.Release();
        }
    }

    private async void OnUninstallSelected(object sender, RoutedEventArgs e)
    {
        var viewModel = (ViewModels.MainViewModel)DataContext;
        var row = viewModel.SelectedRow;
        var record = row?.Entry.ManagementRecord;
        if (record is null || row?.CanUninstall != true || !viewModel.MutationsAllowed)
        {
            viewModel.Announce("Healthy Managed uninstall is unavailable for the selected Skill. Nothing changed.");
            return;
        }
        if (!await _maintenanceGate.WaitAsync(0))
        {
            viewModel.Announce("Another maintenance operation is already running. Nothing changed.");
            return;
        }

        BeginMutation();
        try
        {
            viewModel.Announce($"Uninstalling Healthy Managed Skill at {record.CanonicalPath}.");
            var result = string.Equals(record.Provenance.SourceProvider, "skills", StringComparison.Ordinal)
                ? await Task.Run(() => _skillsProvider.Uninstall(record, _mutationCancellation!.Token))
                : string.Equals(record.Provenance.SourceProvider, ApmClient.ProviderId, StringComparison.Ordinal)
                    ? await Task.Run(() => _apmProvider.Uninstall(record, _mutationCancellation!.Token))
                    : await Task.Run(() => _githubProvider.Uninstall(record, _mutationCancellation!.Token));
            viewModel.LoadInventory(RefreshInventory());
            if (!result.Succeeded)
            {
                ApplyRecoveryMode(viewModel);
            }
            viewModel.Announce(result.Succeeded
                ? $"Uninstalled the Healthy Managed Skill at {record.CanonicalPath}; content and Harness Exposure absence were verified before authority removal."
                : $"Uninstall failed. {result.Diagnostics}");
        }
        finally
        {
            EndMutation();
            _maintenanceGate.Release();
        }
    }

    private async void OnRemoveLocalFolderSelected(object sender, RoutedEventArgs e)
    {
        var viewModel = (ViewModels.MainViewModel)DataContext;
        var row = viewModel.SelectedRow;
        if (row?.CanRemoveLocalFolder != true || !viewModel.MutationsAllowed)
        {
            viewModel.Announce("Remove Local Folder is unavailable for the selected installation. Nothing changed.");
            return;
        }

        var exactPath = row.Entry.LocalPath;
        if (MessageBox.Show(
                this,
                $"Remove Local Folder will delete this exact Unmanaged Installation path after creating a temporary recovery snapshot:\n\n{exactPath}",
                "Confirm Remove Local Folder",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning,
                MessageBoxResult.Cancel) != MessageBoxResult.OK)
        {
            viewModel.Announce("Remove Local Folder cancelled. Nothing changed.");
            return;
        }
        if (!await _maintenanceGate.WaitAsync(0))
        {
            viewModel.Announce("Another maintenance operation is already running. Nothing changed.");
            return;
        }

        BeginMutation();
        try
        {
            var result = await Task.Run(() => _githubProvider.RemoveLocalFolder(exactPath, _mutationCancellation!.Token));
            viewModel.LoadInventory(RefreshInventory());
            if (!result.Succeeded)
            {
                ApplyRecoveryMode(viewModel);
            }
            viewModel.Announce(result.Succeeded
                ? $"Remove Local Folder completed for exact Unmanaged Installation path {exactPath}."
                : $"Remove Local Folder failed. {result.Diagnostics}");
        }
        finally
        {
            EndMutation();
            _maintenanceGate.Release();
        }
    }

    private void BeginMutation()
    {
        _mutationCancellation = new CancellationTokenSource();
        _mutationInProgress = true;
    }

    private void EndMutation()
    {
        _mutationInProgress = false;
        _mutationCancellation?.Dispose();
        _mutationCancellation = null;
    }

    private void ApplyRecoveryMode(ViewModels.MainViewModel viewModel)
    {
        if (_githubProvider.RecoveryRequired)
        {
            viewModel.EnterRecoveryRequired($"Recovery Required: {_githubProvider.RecoveryDiagnostic}");
        }
        else if (_skillsProvider.RecoveryRequired)
        {
            viewModel.EnterRecoveryRequired($"Recovery Required: {_skillsProvider.RecoveryDiagnostic}");
        }
        else if (_apmProvider.RecoveryRequired)
        {
            viewModel.EnterRecoveryRequired($"Recovery Required: {_apmProvider.RecoveryDiagnostic}");
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        _closing = true;
        if (_mutationInProgress)
        {
            _mutationCancellation?.Cancel();
            try
            {
                _githubProvider.RequestMutationCancellation();
                _skillsProvider.RequestMutationCancellation();
                _apmProvider.RequestMutationCancellation();
            }
            catch (Exception exception)
            {
                _log.Error("Cancellation was requested while closing; the existing pending journal remains authoritative.", exception);
            }
        }

        base.OnClosing(e);
    }

}
