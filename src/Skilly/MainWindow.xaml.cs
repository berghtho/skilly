using System.Windows;
using System.Windows.Controls;
using Skilly.Providers.GitHub;
using Skilly.Skills;

namespace Skilly;

public partial class MainWindow : Window
{
    private readonly Infrastructure.RollingLog _log;
    private readonly GitHubProvider _githubProvider;
    private readonly Func<InventorySnapshot> _refreshInventory;

    public MainWindow(
        Infrastructure.RollingLog log,
        ViewModels.MainViewModel viewModel,
        GitHubProvider githubProvider,
        Func<InventorySnapshot> refreshInventory)
    {
        InitializeComponent();
        _log = log;
        _githubProvider = githubProvider;
        _refreshInventory = refreshInventory;
        DataContext = viewModel;
        Loaded += (_, _) => _log.Info("Workbench window loaded.");
        Closed += (_, _) => _log.Info("Workbench window closed; shutdown proceeding.");
    }

    private async void OnInspectSource(object sender, RoutedEventArgs e)
    {
        var viewModel = (ViewModels.MainViewModel)DataContext;
        if (!GitHubSourceReference.TryParse(viewModel.SourceText, out var reference, out var parseError))
        {
            viewModel.Announce($"Source inspection failed. {parseError} Nothing changed.");
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
