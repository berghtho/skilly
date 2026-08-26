using System.Windows;
using System.Windows.Controls;
using Skilly.Providers.GitHub;
using Skilly.Skills;

namespace Skilly;

public partial class MainWindow : Window
{
    private readonly Infrastructure.RollingLog _log;
    private readonly GitHubProvider _githubProvider;
    private readonly GitHubCheckRunner _checkRunner;
    private readonly Func<InventorySnapshot> _refreshInventory;
    private readonly SemaphoreSlim _maintenanceGate = new(1, 1);

    public MainWindow(
        Infrastructure.RollingLog log,
        ViewModels.MainViewModel viewModel,
        GitHubProvider githubProvider,
        GitHubCheckRunner checkRunner,
        Func<InventorySnapshot> refreshInventory)
    {
        InitializeComponent();
        _log = log;
        _githubProvider = githubProvider;
        _checkRunner = checkRunner;
        _refreshInventory = refreshInventory;
        DataContext = viewModel;
        Loaded += OnLoaded;
        Closed += (_, _) => _log.Info("Workbench window closed; shutdown proceeding.");
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _log.Info("Workbench window loaded.");
        await RefreshChecks(background: true);
    }

    private async void OnInspectSource(object sender, RoutedEventArgs e)
    {
        var viewModel = (ViewModels.MainViewModel)DataContext;
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

        InspectSourceButton.IsEnabled = false;
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
            var dialog = new SourceInspectionWindow(inspection, _githubProvider) { Owner = this };
            if (dialog.ShowDialog() == true)
            {
                viewModel.LoadInventory(_refreshInventory());
                viewModel.Announce($"Installed {dialog.InstalledCount} Skill(s) from GitHub and verified all Harness Exposures.");
            }
            else
            {
                viewModel.Announce($"Read-only inspection found {inspection.Skills.Count} Source Skill(s). Nothing changed.");
            }
        }
        catch (Exception exception)
        {
            _log.Error("GitHub source inspection failed.", exception);
            viewModel.Announce($"GitHub source inspection failed. {exception.Message} Nothing changed.");
        }
        finally
        {
            InspectSourceButton.IsEnabled = true;
            _maintenanceGate.Release();
        }
    }

    private void OnSkillListHeaderClick(object sender, RoutedEventArgs e)
    {
        if (e.OriginalSource is GridViewColumnHeader header && header.Column?.Header is string label
            && ColumnMap.TryGetValue(label, out var column))
        {
            ((ViewModels.MainViewModel)DataContext).SortBy(column);
        }
    }

    private async void OnRefreshChecks(object sender, RoutedEventArgs e)
        => await RefreshChecks(background: false);

    private async Task RefreshChecks(bool background)
    {
        var viewModel = (ViewModels.MainViewModel)DataContext;
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
            viewModel.LoadInventory(_refreshInventory());
            viewModel.Announce(result.FailureCount == 0
                ? $"Checked {result.CheckedCount} managed GitHub Skill(s). Installed content was not changed."
                : $"Checked {result.CheckedCount} managed GitHub Skill(s); {result.FailureCount} check(s) failed and prior results are stale. Installed content was not changed.");
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
        var viewModel = (ViewModels.MainViewModel)DataContext;
        var record = viewModel.SelectedRow?.Entry.ManagementRecord;
        if (record is null || viewModel.SelectedRow?.CanUpdate != true)
        {
            viewModel.Announce("Direct update is unavailable for the selected Skill. Nothing changed.");
            return;
        }
        if (!await _maintenanceGate.WaitAsync(0))
        {
            viewModel.Announce("Another maintenance operation is already running. Nothing changed.");
            return;
        }

        viewModel.Announce("Updating the selected GitHub Skill from verified source content.");
        try
        {
            var result = await Task.Run(() => _githubProvider.Update(record));
            if (!result.Succeeded)
            {
                viewModel.LoadInventory(_refreshInventory());
                viewModel.Announce($"GitHub update failed. {result.Diagnostics}");
                return;
            }

            viewModel.LoadInventory(_refreshInventory());
            viewModel.Announce($"Updated the selected GitHub Skill to {result.Value!.InstalledRevision[..12]} and verified content, state, and Claude exposure.");
        }
        catch (Exception exception)
        {
            _log.Error("GitHub update failed.", exception);
            viewModel.LoadInventory(_refreshInventory());
            viewModel.Announce($"GitHub update failed. {exception.Message}");
        }
        finally
        {
            _maintenanceGate.Release();
        }
    }

    private static readonly Dictionary<string, ViewModels.InventorySortColumn> ColumnMap = new()
    {
        ["Skill"] = ViewModels.InventorySortColumn.Name,
        ["Root"] = ViewModels.InventorySortColumn.Root,
        ["Management"] = ViewModels.InventorySortColumn.Management,
        ["Health"] = ViewModels.InventorySortColumn.Health,
        ["Update"] = ViewModels.InventorySortColumn.UpdateStatus,
        ["Exposures"] = ViewModels.InventorySortColumn.Exposures,
    };
}
