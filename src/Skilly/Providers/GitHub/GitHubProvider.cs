using Skilly.Providers;

namespace Skilly.Providers.GitHub;

public sealed record ProviderReadiness(bool IsReady, string Provider, string Diagnostic, string? Version = null);

public sealed class GitHubProvider(
    GhClient client,
    SourceInspector inspector,
    GitHubInstaller installer,
    GitHubChecker checker,
    GitHubUpdater updater,
    GitHubLifecycle lifecycle,
    GitHubAdoptionVerifier adoptionVerifier)
{
    public bool RecoveryRequired => lifecycle.RecoveryRequired;

    public string RecoveryDiagnostic => lifecycle.RecoveryDiagnostic;

    public ProviderReadiness GetReadiness()
    {
        try
        {
            var version = client.GetVersion();
            client.EnsureAuthenticated();
            var gitVersion = client.GetGitVersion();
            return new ProviderReadiness(
                true,
                "GitHub",
                $"GitHub is ready through the active authenticated gh identity ({version}; {gitVersion}).",
                version);
        }
        catch (Exception exception)
        {
            return new ProviderReadiness(false, "GitHub", $"GitHub provider unavailable: {exception.Message}");
        }
    }

    public ProviderResult<SourceInspection> Inspect(GitHubSourceReference reference)
    {
        try
        {
            var version = client.GetVersion();
            client.EnsureAuthenticated(reference.Host);
            client.GetGitVersion();
            var inspection = inspector.Inspect(reference, version);
            return ProviderResult<SourceInspection>.Success(
                inspection,
                $"Read-only GitHub inspection completed with {version}; nothing changed.");
        }
        catch (Exception exception)
        {
            return ProviderResult<SourceInspection>.Failure(exception.Message);
        }
    }

    public ProviderResult<InstallResult> Install(
        SourceInspection inspection,
        IReadOnlyList<SourceSkill> selected,
        CancellationToken cancellationToken = default)
    {
        try
        {
            client.EnsureAuthenticated(inspection.Reference.Host);
            var result = installer.Install(inspection, selected, cancellationToken);
            return ProviderResult<InstallResult>.Success(
                result,
                $"Installed and verified {result.SucceededCount} Skill(s). No partial success was accepted.");
        }
        catch (Exception exception)
        {
            return ProviderResult<InstallResult>.Failure(exception.Message);
        }
    }

    public ProviderResult<AdoptionDiscovery> DiscoverAdoptions(SourceInspection inspection, Skills.InventorySnapshot inventory)
    {
        try
        {
            client.EnsureAuthenticated(inspection.Reference.Host);
            var discovery = adoptionVerifier.Discover(inspection, inventory);
            return ProviderResult<AdoptionDiscovery>.Success(
                discovery,
                $"Verified {discovery.Evidence.Count} exact Adoption candidate(s); nothing changed.");
        }
        catch (Exception exception)
        {
            return ProviderResult<AdoptionDiscovery>.Failure(exception.Message);
        }
    }

    public ProviderResult<LifecycleResult> Adopt(Skills.AdoptionEvidence evidence, CancellationToken cancellationToken = default)
    {
        try
        {
            client.EnsureAuthenticated(evidence.ProposedRecord.Provenance.Host);
            return ProviderResult<LifecycleResult>.Success(
                lifecycle.Adopt(evidence, cancellationToken),
                "Adoption recorded verified Provenance without rewriting Skill content.");
        }
        catch (Exception exception)
        {
            return ProviderResult<LifecycleResult>.Failure(exception.Message);
        }
    }

    public ProviderResult<CheckResult> Check(State.ManagementRecord record)
    {
        try
        {
            client.EnsureAuthenticated(record.Provenance.Host);
            return ProviderResult<CheckResult>.Success(
                checker.Check(record),
                "Read-only selected-content Check completed; nothing changed.");
        }
        catch (Exception exception)
        {
            return ProviderResult<CheckResult>.Failure(exception.Message);
        }
    }

    public ProviderResult<UpdateResult> Update(State.ManagementRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            client.EnsureAuthenticated(record.Provenance.Host);
            var result = updater.Update(record, cancellationToken);
            return ProviderResult<UpdateResult>.Success(
                result,
                $"Updated and verified GitHub Skill at {result.InstalledRevision}.");
        }
        catch (Exception exception)
        {
            return ProviderResult<UpdateResult>.Failure(exception.Message);
        }
    }

    public ProviderResult<ManagedReinstallPlan> PlanManagedReinstall(State.ManagementRecord record)
    {
        try
        {
            client.EnsureAuthenticated(record.Provenance.Host);
            return ProviderResult<ManagedReinstallPlan>.Success(
                lifecycle.PlanManagedReinstall(record),
                "Verified Managed Reinstall path and replacement revision. Nothing changed.");
        }
        catch (Exception exception)
        {
            return ProviderResult<ManagedReinstallPlan>.Failure(exception.Message);
        }
    }

    public ProviderResult<LifecycleResult> ManagedReinstall(ManagedReinstallPlan plan, CancellationToken cancellationToken = default)
    {
        try
        {
            return ProviderResult<LifecycleResult>.Success(
                lifecycle.ManagedReinstall(plan, cancellationToken),
                "Managed Reinstall replaced clean source content and verified all postconditions.");
        }
        catch (Exception exception)
        {
            return ProviderResult<LifecycleResult>.Failure(exception.Message);
        }
    }

    public ProviderResult<LifecycleResult> Uninstall(State.ManagementRecord record, CancellationToken cancellationToken = default)
    {
        try
        {
            return ProviderResult<LifecycleResult>.Success(
                lifecycle.Uninstall(record, cancellationToken),
                "Healthy Managed uninstall removed content, Harness Exposures, then authority.");
        }
        catch (Exception exception)
        {
            return ProviderResult<LifecycleResult>.Failure(exception.Message);
        }
    }

    public ProviderResult<LifecycleResult> RemoveLocalFolder(string exactPath, CancellationToken cancellationToken = default)
    {
        try
        {
            return ProviderResult<LifecycleResult>.Success(
                lifecycle.RemoveLocalFolder(exactPath, cancellationToken),
                "Remove Local Folder removed the exact Unmanaged Installation path.");
        }
        catch (Exception exception)
        {
            return ProviderResult<LifecycleResult>.Failure(exception.Message);
        }
    }

    public void RequestMutationCancellation() => lifecycle.RequestCancellation();
}
