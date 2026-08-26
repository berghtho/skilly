namespace Skilly.Providers.GitHub;

public sealed class GitHubProvider(
    GhClient client,
    SourceInspector inspector,
    GitHubInstaller installer)
{
    public bool IsAvailable() => client.IsAvailable();

    public SourceInspection Inspect(GitHubSourceReference reference) => inspector.Inspect(reference);

    public InstallResult Install(SourceInspection inspection, IReadOnlyList<SourceSkill> selected)
        => installer.Install(inspection, selected);
}
