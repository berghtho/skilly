using System.Windows;

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
}
