using Skilly.Providers;

namespace Skilly.Providers.GitHub;

public sealed class GitHubProvider(
    GhClient client,
    SourceInspector inspector,
    GitHubInstaller installer,
    GitHubChecker checker,
    GitHubUpdater updater)
{
    public ProviderResult<SourceInspection> Inspect(GitHubSourceReference reference)
    {
        try
        {
            var version = client.GetVersion();
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

    public ProviderResult<InstallResult> Install(SourceInspection inspection, IReadOnlyList<SourceSkill> selected)
    {
        try
        {
            var result = installer.Install(inspection, selected);
            return ProviderResult<InstallResult>.Success(
                result,
                $"Installed and verified {result.SucceededCount} Skill(s). No partial success was accepted.");
        }
        catch (Exception exception)
        {
            return ProviderResult<InstallResult>.Failure(exception.Message);
        }
    }

    public ProviderResult<CheckResult> Check(State.ManagementRecord record)
    {
        try
        {
            return ProviderResult<CheckResult>.Success(
                checker.Check(record),
                "Read-only selected-content Check completed; nothing changed.");
        }
        catch (Exception exception)
        {
            return ProviderResult<CheckResult>.Failure(exception.Message);
        }
    }

    public ProviderResult<UpdateResult> Update(State.ManagementRecord record)
    {
        try
        {
            var result = updater.Update(record);
            return ProviderResult<UpdateResult>.Success(
                result,
                $"Updated and verified GitHub Skill at {result.InstalledRevision}.");
        }
        catch (Exception exception)
        {
            return ProviderResult<UpdateResult>.Failure(exception.Message);
        }
    }
}
