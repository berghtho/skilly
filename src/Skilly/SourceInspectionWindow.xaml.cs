using System.Windows;
using System.ComponentModel;
using Skilly.Providers.GitHub;
using Skilly.Providers.SkillsCli;
using Skilly.ViewModels;
using Skilly.Providers.Apm;

namespace Skilly;

public partial class SourceInspectionWindow : Window
{
    private readonly GitHubProvider? _githubProvider;
    private readonly SkillsCliProvider? _skillsProvider;
    private readonly ApmProvider? _apmProvider;
    private readonly SourceInspectionViewModel? _viewModel;
    private readonly SkillsCliSourceInspectionViewModel? _skillsViewModel;
    private readonly ApmSourceInspectionViewModel? _apmViewModel;
    private readonly CancellationTokenSource _cancellation = new();
    private TaskCompletionSource? _operationCompletion;

    public SourceInspectionWindow(SourceInspection inspection, GitHubProvider provider, bool mutationsAllowed = true)
    {
        InitializeComponent();
        _githubProvider = provider;
        _viewModel = new SourceInspectionViewModel(inspection, mutationsAllowed);
        DataContext = _viewModel;
    }

    public SourceInspectionWindow(ApmInspection inspection, ApmProvider provider, bool mutationsAllowed = true)
    {
        InitializeComponent();
        _apmProvider = provider;
        _apmViewModel = new ApmSourceInspectionViewModel(inspection, mutationsAllowed);
        DataContext = _apmViewModel;
    }

    public SourceInspectionWindow(SkillsCliInspection inspection, SkillsCliProvider provider, bool mutationsAllowed = true)
    {
        InitializeComponent();
        _skillsProvider = provider;
        _skillsViewModel = new SkillsCliSourceInspectionViewModel(inspection, mutationsAllowed);
        DataContext = _skillsViewModel;
    }

    public int InstalledCount { get; private set; }

    public Task OperationCompletion => _operationCompletion?.Task ?? Task.CompletedTask;

    private void OnSelectAll(object sender, RoutedEventArgs e)
    {
        _viewModel?.SelectAll(true);
        _skillsViewModel?.SelectAll(true);
        _apmViewModel?.SelectAll(true);
    }

    private void OnSelectNone(object sender, RoutedEventArgs e)
    {
        _viewModel?.SelectAll(false);
        _skillsViewModel?.SelectAll(false);
        _apmViewModel?.SelectAll(false);
    }

    private void OnSelectExact(object sender, RoutedEventArgs e)
    {
        _viewModel?.SelectExact();
        _skillsViewModel?.SelectExact();
        _apmViewModel?.SelectExact();
    }

    private async void OnInstallSelected(object sender, RoutedEventArgs e)
    {
        if (_apmViewModel is not null)
        {
            await InstallApmSelected();
            return;
        }
        if (_skillsViewModel is not null)
        {
            await InstallSkillsCliSelected();
            return;
        }
        var viewModel = _viewModel!;
        var selected = viewModel.Skills
            .Where(static item => item.IsSelected)
            .Select(static item => item.Skill)
            .ToList();
        if (selected.Count == 0)
        {
            return;
        }

        viewModel.IsBusy = true;
        _operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.Status = $"Installing {selected.Count} selected Source Skill(s)...";
        try
        {
            var result = await Task.Run(() => _githubProvider!.Install(viewModel.Inspection, selected, _cancellation.Token));
            if (!result.Succeeded)
            {
                viewModel.Status = $"Installation failed. {result.Diagnostics}";
                return;
            }

            InstalledCount = result.Value!.SucceededCount;
            viewModel.IsBusy = false;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            viewModel.Status = $"Installation failed. {exception.Message}";
        }
        finally
        {
            viewModel.IsBusy = false;
            _operationCompletion.TrySetResult();
        }
    }

    private async Task InstallApmSelected()
    {
        var viewModel = _apmViewModel!;
        var selected = viewModel.Skills.Where(item => item.IsSelected).Select(item => item.Skill).ToList();
        if (selected.Count == 0) return;
        viewModel.IsBusy = true;
        _operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.Status = $"Installing {selected.Count} selected Source Skill(s) through Microsoft APM...";
        try
        {
            var result = await Task.Run(() => _apmProvider!.Install(viewModel.Inspection, selected, _cancellation.Token));
            if (!result.Succeeded) { viewModel.Status = $"Installation failed. {result.Diagnostics}"; return; }
            InstalledCount = result.Value!.SucceededCount;
            viewModel.IsBusy = false;
            DialogResult = true;
        }
        catch (Exception exception) { viewModel.Status = $"Installation failed. {exception.Message}"; }
        finally { viewModel.IsBusy = false; _operationCompletion.TrySetResult(); }
    }

    private async Task InstallSkillsCliSelected()
    {
        var viewModel = _skillsViewModel!;
        var selected = viewModel.Skills.Where(static item => item.IsSelected).Select(static item => item.Skill).ToList();
        if (selected.Count == 0) return;
        viewModel.IsBusy = true;
        _operationCompletion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        viewModel.Status = $"Installing {selected.Count} selected Source Skill(s) through {SkillsCliClient.Package}...";
        try
        {
            var result = await Task.Run(() => _skillsProvider!.Install(viewModel.Inspection, selected, _cancellation.Token));
            if (!result.Succeeded)
            {
                viewModel.Status = $"Installation failed. {result.Diagnostics}";
                return;
            }
            InstalledCount = result.Value!.SucceededCount;
            viewModel.IsBusy = false;
            DialogResult = true;
        }
        catch (Exception exception)
        {
            viewModel.Status = $"Installation failed. {exception.Message}";
        }
        finally
        {
            viewModel.IsBusy = false;
            _operationCompletion.TrySetResult();
        }
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_viewModel?.IsBusy == true || _skillsViewModel?.IsBusy == true || _apmViewModel?.IsBusy == true)
        {
            _cancellation.Cancel();
            if (_viewModel is not null) _viewModel.Status = "Cancellation requested. Pending recovery data will be retained for restart reconciliation.";
            if (_skillsViewModel is not null) _skillsViewModel.Status = "Cancellation requested. Pending recovery data will be retained for restart reconciliation.";
            if (_apmViewModel is not null) _apmViewModel.Status = "Cancellation requested. Pending recovery data will be retained for restart reconciliation.";
        }

        base.OnClosing(e);
    }
}
