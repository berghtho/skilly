using Skilly.Infrastructure;
using Skilly.Skills;

namespace Skilly.Providers.GitHub;

public sealed record SourceSkill(
    string SkillPath,
    string FolderName,
    string? DeclaredName,
    string? Description,
    bool MetadataValid,
    string? MetadataError,
    IReadOnlyList<string> FilePaths)
{
    public bool MatchesAlias(string candidate)
        => string.Equals(candidate, SkillPath, StringComparison.Ordinal)
           || string.Equals(candidate, FolderName, StringComparison.Ordinal)
           || (DeclaredName is not null && string.Equals(candidate, DeclaredName, StringComparison.Ordinal));
}

public sealed record SourceInspection(
    GitHubSourceReference Reference,
    RepositoryFacts Repository,
    string RequestedTrackingRule,
    ResolvedCommit Commit,
    string ProviderVersion,
    IReadOnlyList<SourceSkill> Skills);

public sealed class SourceInspector(GhClient client, RollingLog log)
{
    public SourceInspection Inspect(GitHubSourceReference reference, string providerVersion)
    {
        log.Info($"Inspecting GitHub source '{reference.Normalized}'.");
        var repository = client.GetRepository(reference.Owner, reference.Repository);
        var requestedRef = reference.RequestedRef ?? repository.DefaultBranch;
        var commit = client.ResolveCommit(reference.Owner, reference.Repository, requestedRef);
        var tree = client.GetTree(reference.Owner, reference.Repository, commit.Sha);

        var prefix = reference.RequestedPath is null
            ? string.Empty
            : reference.RequestedPath.TrimEnd('/') + "/";
        var blobs = tree.Entries
            .Where(entry => entry.Type == "blob")
            .Where(entry => prefix.Length == 0 || entry.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(entry => entry.Path.Replace('\\', '/'))
            .ToList();

        var skillFolders = blobs
            .Where(path => path.EndsWith("/SKILL.md", StringComparison.Ordinal))
            .Select(path => path[..^"/SKILL.md".Length])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (skillFolders.Count == 0)
        {
            throw new GhApiException(
                $"No SKILL.md files were found under '{(prefix.Length == 0 ? "the repository root" : reference.RequestedPath)}' at commit {commit.Sha[..12]}.");
        }

        var skills = new List<SourceSkill>();
        foreach (var folder in skillFolders.OrderBy(static folder => folder, StringComparer.Ordinal))
        {
            var skillMdPath = folder + "/SKILL.md";
            byte[] skillMdBytes;
            try
            {
                skillMdBytes = client.GetFileContent(reference.Owner, reference.Repository, skillMdPath, commit.Sha);
            }
            catch (GhApiException exception)
            {
                log.Error($"Could not read {skillMdPath} during inspection.", exception);
                throw;
            }

            var metadata = SkillMdReader.Parse(System.Text.Encoding.UTF8.GetString(skillMdBytes));
            var files = blobs.Where(path => path.StartsWith(folder + "/", StringComparison.Ordinal)).ToList();
            var folderName = folder[(folder.LastIndexOf('/') + 1)..];
            var validIdentity = SkillMdReader.IsValidSkillFolderName(folderName);
            skills.Add(new SourceSkill(
                folder,
                folderName,
                metadata.DeclaredName,
                metadata.Description,
                metadata.Status == MetadataReadStatus.Valid && validIdentity,
                validIdentity ? metadata.Error : $"Folder name '{folderName}' is not a valid canonical Skill identity.",
                files));
        }

        log.Info($"Inspection found {skills.Count} Source Skill(s) below '{(reference.RequestedPath ?? "(root)")}' at commit {commit.Sha}.");
        return new SourceInspection(reference, repository, requestedRef, commit, providerVersion, skills);
    }
}
