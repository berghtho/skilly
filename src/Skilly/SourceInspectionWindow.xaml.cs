using System.Windows;
using System.ComponentModel;
using Skilly.Providers.GitHub;
using Skilly.ViewModels;

namespace Skilly;

public partial class SourceInspectionWindow : Window
{
    private readonly GitHubProvider _provider;
    private readonly SourceInspectionViewModel _viewModel;
    private readonly CancellationTokenSource _cancellation = new();
    private TaskCompletionSource? _operationCompletion;

    public SourceInspectionWindow(SourceInspection inspection, GitHubProvider provider, bool mutationsAllowed = true)
    {
        InitializeComponent();
        _provider = provider;
        _viewModel = new SourceInspectionViewModel(inspection, mutationsAllowed);
        DataContext = _viewModel;
    }

    public int InstalledCount { get; private set; }

    public Task OperationCompletion => _operationCompletion?.Task ?? Task.CompletedTask;

    private void OnSelectAll(object sender, RoutedEventArgs e) => _viewModel.SelectAll(true);

    private void OnSelectNone(object sender, RoutedEventArgs e) => _viewModel.SelectAll(false);

    private void OnSelectExact(object sender, RoutedEventArgs e) => _viewModel.SelectExact();

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
        _operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _viewModel.Status = $"Installing {selected.Count} selected Source Skill(s)...";
        try
        {
            var result = await Task.Run(() => _provider.Install(_viewModel.Inspection, selected, _cancellation.Token));
            if (!result.Succeeded)
            {
                _viewModel.Status = $"Installation failed. {result.Diagnostics}";
                return;
            }

            InstalledCount = result.Value!.SucceededCount;
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
            _operationCompletion.TrySetResult();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel.IsBusy)
        {
            _cancellation.Cancel();
            _viewModel.Status = "Cancellation requested. Pending recovery data will be retained for restart reconciliation.";
        }

        base.OnClosing(e);
    }
}
