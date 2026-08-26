using System.Windows;
using System.ComponentModel;
using Skilly.Providers.GitHub;
using Skilly.ViewModels;

namespace Skilly;

public partial class SourceInspectionWindow : Window
{
    private readonly GitHubProvider _provider;
    private readonly SourceInspectionViewModel _viewModel;

    public SourceInspectionWindow(SourceInspection inspection, GitHubProvider provider)
    {
        InitializeComponent();
        _provider = provider;
        _viewModel = new SourceInspectionViewModel(inspection);
        DataContext = _viewModel;
    }

    public int InstalledCount { get; private set; }

    private void OnSelectAll(object sender, RoutedEventArgs e) => _viewModel.SelectAll(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => _viewModel.SelectAll(false);

    private async void OnInstallSelected(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.Skills
            .Where(static item => item.IsSelected)
            .Select(static item => item.Skill)
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        _viewModel.IsBusy = true;
        _viewModel.Status = $"Installing {selected.Count} selected Source Skill(s)...";
        try
        {
            var result = await Task.Run(() => _provider.Install(_viewModel.Inspection, selected));
            InstalledCount = result.SucceededCount;
            _viewModel.IsBusy = false;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            _viewModel.Status = $"Installation failed. {exception.Message}";
        }
        finally
        {
            _viewModel.IsBusy = false;
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            e.Cancel = true;
            _viewModel.Status = "Installation is still running. Wait for it to complete before closing.";
        }

        base.OnClosing(e);
    }
}
