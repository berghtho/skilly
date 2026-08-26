using Skilly.Providers;

namespace Skilly.Providers.GitHub;

public sealed class GitHubProvider(
    GhClient client,
    SourceInspector inspector,
    GitHubInstaller installer)
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
}
