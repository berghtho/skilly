using System.Windows;
using System.Windows.Controls;
using System.ComponentModel;
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
            InspectSourceButton.IsEnabled = true;
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
        InspectSourceButton.IsEnabled = false;
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
            InspectSourceButton.IsEnabled = true;
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
        InspectSourceButton.IsEnabled = false;
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
            var skillsOwned = string.Equals(record.Provenance.SourceProvider, "skills", StringComparison.Ordinal);
            var apmOwned = string.Equals(record.Provenance.SourceProvider, ApmClient.ProviderId, StringComparison.Ordinal);
            var result = skillsOwned
                ? await Task.Run(() =>
                {
                    var providerResult = _skillsProvider.Update(record, _mutationCancellation!.Token);
                    return providerResult.Succeeded
                        ? Providers.ProviderResult<UpdateResult>.Success(
                            new UpdateResult(providerResult.Value!.InstallationId, providerResult.Value.InstalledRevision),
                            providerResult.Diagnostics)
                        : Providers.ProviderResult<UpdateResult>.Failure(providerResult.Diagnostics);
                })
                : apmOwned
                    ? await Task.Run(() =>
                    {
                        var providerResult = _apmProvider.Update(record, _mutationCancellation!.Token);
                        return providerResult.Succeeded
                            ? ProviderResult<UpdateResult>.Success(new UpdateResult(providerResult.Value!.InstallationId, providerResult.Value.InstalledRevision), providerResult.Diagnostics)
                            : ProviderResult<UpdateResult>.Failure(providerResult.Diagnostics);
                    })
                    : await Task.Run(() => _githubProvider.Update(record, _mutationCancellation!.Token));
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

    private async void OnAdoptSelected(object sender, RoutedEventArgs e)
    {
        var viewModel = (ViewModels.MainViewModel)DataContext;
        var evidence = viewModel.SelectedRow?.Entry.AdoptionEvidence;
        if (evidence is null || viewModel.SelectedRow?.CanAdopt != true || !viewModel.MutationsAllowed)
        {
            viewModel.Announce("Adoption is unavailable for the selected Skill. Nothing changed.");
            return;
        }
        if (!await _maintenanceGate.WaitAsync(0))
        {
            viewModel.Announce("Another maintenance operation is already running. Nothing changed.");
            return;
        }

        BeginMutation();
        viewModel.Announce("Adopting the selected exact verified Skill. Existing Skill content will be preserved.");
        try
        {
            var result = await Task.Run(() => _githubProvider.Adopt(evidence, _mutationCancellation!.Token));
            _adoptionEvidence = _adoptionEvidence.Where(candidate =>
                !string.Equals(
                    candidate.ProposedRecord.CanonicalPath,
                    evidence.ProposedRecord.CanonicalPath,
                    StringComparison.OrdinalIgnoreCase)).ToList();
            viewModel.LoadInventory(RefreshInventory());
            if (!result.Succeeded)
            {
                ApplyRecoveryMode(viewModel);
                viewModel.Announce($"Adoption failed. {result.Diagnostics} The installation remains Unmanaged; Skill content was not rewritten.");
                return;
            }

            viewModel.Announce($"Adopted the selected Skill at {result.Value!.ExactPath}; verified Provenance was recorded and Skill content was preserved.");
        }
        catch (Exception exception)
        {
            _log.Error("GitHub Adoption failed.", exception);
            _adoptionEvidence = [];
            viewModel.LoadInventory(RefreshInventory());
            ApplyRecoveryMode(viewModel);
            viewModel.Announce($"Adoption failed. {exception.Message} Skill content was not rewritten.");
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
            var planned = await Task.Run(() => _githubProvider.PlanManagedReinstall(record));
            if (!planned.Succeeded)
            {
                viewModel.Announce($"Managed Reinstall preparation failed. {planned.Diagnostics} Nothing changed.");
                return;
            }

            var plan = planned.Value!;
            var decision = MessageBox.Show(
                this,
                $"Managed Reinstall will replace this exact path:\n\n{plan.ExactPath}\n\nVerified replacement revision:\n{plan.Revision}\n\nCurrent content will be snapshotted and replaced cleanly. Files will not be merged.",
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
            var result = await Task.Run(() => _githubProvider.ManagedReinstall(plan, _mutationCancellation!.Token));
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
