using System.Windows;
using System.Windows.Controls;

namespace Skilly;

public partial class MainWindow : Window
{
    private readonly Infrastructure.RollingLog _log;

    public MainWindow(Infrastructure.RollingLog log, ViewModels.MainViewModel viewModel)
    {
        InitializeComponent();
        _log = log;
        DataContext = viewModel;
        Loaded += (_, _) => _log.Info("Workbench window loaded.");
        Closed += (_, _) => _log.Info("Workbench window closed; shutdown proceeding.");
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
