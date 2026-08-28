using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
using System.Windows.Automation;
using System.Windows.Automation.Peers;
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

    public MainWindow(
        Infrastructure.RollingLog log,
        ViewModels.MainViewModel viewModel,
        GitHubProvider githubProvider,
        SkillsCliProvider skillsProvider,
        ApmProvider apmProvider,
        ProviderCheckRunner checkRunner,
        Func<IReadOnlyList<AdoptionEvidence>?, InventorySnapshot> refreshInventory)
    {
        InitializeComponent();
        _log = log;
        _githubProvider = githubProvider;
        _skillsProvider = skillsProvider;
        _apmProvider = apmProvider;
        _managedReinstall = new ManagedReinstallDispatcher(githubProvider, skillsProvider, apmProvider);
        _checkRunner = checkRunner;
        _refreshInventory = refreshInventory;
        DataContext = viewModel;
        viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Loaded += OnLoaded;
        StateChanged += OnStateChanged;
        Closed += (_, _) =>
        {
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
            var dialog = new SourceInspectionWindow(inspection, _githubProvider, viewModel.MutationsAllowed) { Owner = this };
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
            var dialog = new SourceInspectionWindow(inspection, _apmProvider, viewModel.MutationsAllowed) { Owner = this };
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

    private void OnSkillListSelectionChanged(object sender, SelectionChangedEventArgs e)
        => ((ViewModels.MainViewModel)DataContext).SelectedRows =
            SkillList.SelectedItems.Cast<ViewModels.InventoryRow>().ToList();

    private void OnSortHeaderClick(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string tag } && Enum.TryParse<ViewModels.InventorySortColumn>(tag, out var column))
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
        var viewModel = (ViewModels.MainViewModel)DataContext;
        var record = viewModel.SelectedRow?.Entry.ManagementRecord;
        if (record is null || viewModel.SelectedRow?.CanUpdate != true || !viewModel.MutationsAllowed)
        {
            viewModel.Announce("Direct update is unavailable for the selected Skill. Nothing changed.");
            return;
        }
        if (!await _maintenanceGate.WaitAsync(0))
        {
            viewModel.Announce("Another maintenance operation is already running. Nothing changed.");
            return;
        }

        BeginMutation();
        viewModel.Announce("Updating the selected Skill through its owning provider from verified source content.");
        try
        {
            var result = await RunProviderUpdate(record);
            if (!result.Succeeded)
            {
                viewModel.LoadInventory(RefreshInventory());
                ApplyRecoveryMode(viewModel);
                viewModel.Announce($"Provider update failed. {result.Diagnostics}");
                return;
            }

            viewModel.LoadInventory(RefreshInventory());
            var revision = result.Value!.InstalledRevision;
            viewModel.Announce($"Updated the selected Skill to {revision[..Math.Min(12, revision.Length)]} and verified provider evidence, content, state, and Claude exposure.");
        }
        catch (Exception exception)
        {
            _log.Error("Provider update failed.", exception);
            viewModel.LoadInventory(RefreshInventory());
            viewModel.Announce($"Provider update failed. {exception.Message}");
        }
        finally
        {
            EndMutation();
            _maintenanceGate.Release();
        }
    }

    private Task<ProviderResult<UpdateResult>> RunProviderUpdate(State.ManagementRecord record)
    {
        var skillsOwned = string.Equals(record.Provenance.SourceProvider, "skills", StringComparison.Ordinal);
        var apmOwned = string.Equals(record.Provenance.SourceProvider, ApmClient.ProviderId, StringComparison.Ordinal);
        return skillsOwned
            ? Task.Run(() =>
            {
                var providerResult = _skillsProvider.Update(record, _mutationCancellation!.Token);
                return providerResult.Succeeded
                    ? ProviderResult<UpdateResult>.Success(
                        new UpdateResult(providerResult.Value!.InstallationId, providerResult.Value.InstalledRevision),
                        providerResult.Diagnostics)
                    : ProviderResult<UpdateResult>.Failure(providerResult.Diagnostics);
            })
            : apmOwned
                ? Task.Run(() =>
                {
                    var providerResult = _apmProvider.Update(record, _mutationCancellation!.Token);
                    return providerResult.Succeeded
                        ? ProviderResult<UpdateResult>.Success(new UpdateResult(providerResult.Value!.InstallationId, providerResult.Value.InstalledRevision), providerResult.Diagnostics)
                        : ProviderResult<UpdateResult>.Failure(providerResult.Diagnostics);
                })
                : Task.Run(() => _githubProvider.Update(record, _mutationCancellation!.Token));
    }

    private async void OnUpdateAll(object sender, RoutedEventArgs e)
    {
        var viewModel = (ViewModels.MainViewModel)DataContext;
        var targets = viewModel.UpdatableRows.Select(static row => row.Entry.ManagementRecord!).ToList();
        if (targets.Count == 0 || !viewModel.MutationsAllowed)
        {
            viewModel.Announce("No Skill has a verified direct update available. Nothing changed.");
            return;
        }
        if (!await _maintenanceGate.WaitAsync(0))
        {
            viewModel.Announce("Another maintenance operation is already running. Nothing changed.");
            return;
        }

        BeginMutation();
        viewModel.Announce($"Updating {targets.Count} Skill(s) through their owning providers from verified source content.");
        var updated = 0;
        try
        {
            foreach (var record in targets)
            {
                var result = await RunProviderUpdate(record);
                if (!result.Succeeded)
                {
                    viewModel.LoadInventory(RefreshInventory());
                    ApplyRecoveryMode(viewModel);
                    viewModel.Announce(
                        $"Update all stopped at '{record.CanonicalPath}' after {updated} Skill(s) were updated. "
                        + $"{result.Diagnostics} The remaining Skill(s) were not touched.");
                    return;
                }

                updated++;
            }

            viewModel.LoadInventory(RefreshInventory());
            viewModel.Announce($"Updated {updated} Skill(s) through their owning providers and verified provider evidence, content, state, and Harness Exposures.");
        }
        catch (Exception exception)
        {
            _log.Error("Update all failed.", exception);
            viewModel.LoadInventory(RefreshInventory());
            ApplyRecoveryMode(viewModel);
            viewModel.Announce($"Update all failed after {updated} Skill(s) were updated. {exception.Message} The remaining Skill(s) were not touched.");
        }
        finally
        {
            EndMutation();
            _maintenanceGate.Release();
        }
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
