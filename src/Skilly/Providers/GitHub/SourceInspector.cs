using System.IO;
using Skilly.Infrastructure;
using Skilly.Skills;

namespace Skilly.Providers.GitHub;

public sealed record SourceSkill(
    string SkillPath,
    string RepositoryPath,
    string FolderName,
    string? DeclaredName,
    string? Description,
    bool MetadataValid,
    string? MetadataError,
    IReadOnlyList<string> FilePaths,
    string ContentIdentity = "",
    IReadOnlyDictionary<string, string>? BlobIdentities = null)
{
    public bool MatchesAlias(string candidate)
        => string.Equals(candidate, SkillPath, StringComparison.Ordinal)
           || (DeclaredName is not null && string.Equals(candidate, DeclaredName, StringComparison.Ordinal));
}

public sealed record SourceInspection(
    GitHubSourceReference Reference,
    RepositoryFacts Repository,
    string RequestedTrackingRule,
    State.TrackingRuleKind TrackingRuleKind,
    ResolvedCommit Commit,
    string ProviderVersion,
    IReadOnlyList<SourceSkill> Skills);

public sealed class SourceInspector(GhClient client, RollingLog log)
{
    public SourceInspection Inspect(GitHubSourceReference reference, string providerVersion)
    {
        var repository = client.GetRepository(reference.Owner, reference.Repository);
        var (resolvedReference, requestedRef, trackingRuleKind, commit) = ResolveReference(reference, repository);
        log.Info($"Inspecting GitHub source '{resolvedReference.Normalized}' pinned to commit {commit.Sha}.");
        var tree = client.GetTreeBelowPath(
            resolvedReference.Owner,
            resolvedReference.Repository,
            commit.Sha,
            resolvedReference.RequestedPath);

        var requestedRoot = resolvedReference.RequestedPath?.TrimEnd('/') ?? string.Empty;
        var blobs = tree.Entries
            .Where(entry => entry.Type == "blob")
            .Select(entry => entry.Path.Replace('\\', '/'))
            .ToList();

        var skillFolders = blobs
            .Where(path => path == "SKILL.md" || path.EndsWith("/SKILL.md", StringComparison.Ordinal))
            .Select(path => path == "SKILL.md" ? string.Empty : path[..^"/SKILL.md".Length])
            .Distinct(StringComparer.Ordinal)
            .ToList();

        if (skillFolders.Count == 0)
        {
            throw new GhApiException(
                $"No SKILL.md files were found under '{(requestedRoot.Length == 0 ? "the repository root" : requestedRoot)}' at commit {commit.Sha[..12]}.");
        }

        var skills = new List<SourceSkill>();
        foreach (var folder in skillFolders.OrderBy(static folder => folder, StringComparer.Ordinal))
        {
            var skillMdPath = folder.Length == 0 ? "SKILL.md" : folder + "/SKILL.md";
            byte[] skillMdBytes;
            try
            {
                var repositorySkillMdPath = JoinRepositoryPath(requestedRoot, skillMdPath);
                skillMdBytes = client.GetFileContent(resolvedReference.Owner, resolvedReference.Repository, repositorySkillMdPath, commit.Sha);
            }
            catch (GhApiException exception)
            {
                log.Error($"Could not read {skillMdPath} during inspection.", exception);
                throw;
            }

            var metadata = SkillMdReader.Parse(System.Text.Encoding.UTF8.GetString(skillMdBytes));
            var relativeFiles = folder.Length == 0
                ? blobs
                : blobs.Where(path => path.StartsWith(folder + "/", StringComparison.Ordinal)).ToList();
            var files = relativeFiles.Select(path => JoinRepositoryPath(requestedRoot, path)).ToList();
            var blobIdentities = tree.Entries
                .Where(entry => entry.Type == "blob" && relativeFiles.Contains(entry.Path, StringComparer.Ordinal))
                .ToDictionary(
                    entry => JoinRepositoryPath(requestedRoot, entry.Path),
                    static entry => entry.Sha,
                    StringComparer.Ordinal);
            var folderName = folder.Length == 0
                ? requestedRoot.Length == 0
                    ? resolvedReference.Repository
                    : requestedRoot[(requestedRoot.LastIndexOf('/') + 1)..]
                : folder[(folder.LastIndexOf('/') + 1)..];
            var skillPath = folder.Length == 0 ? "." : folder;
            var validIdentity = SkillMdReader.IsValidSkillFolderName(folderName);
            var invalidEntry = tree.Entries.FirstOrDefault(entry => IsBelow(entry.Path, folder)
                && (entry.Type == "commit"
                    || (entry.Type == "blob" && entry.Mode is not ("100644" or "100755"))
                    || !IsSafeRelativePath(entry.Path)));
            var caseCollision = relativeFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() != relativeFiles.Count;
            var metadataValid = metadata.Status == MetadataReadStatus.Valid && validIdentity && invalidEntry is null;
            var metadataError = !validIdentity
                ? $"Folder name '{folderName}' is not a valid canonical Skill identity."
                : caseCollision
                    ? "Selected folder contains paths that collide on Windows."
                    : invalidEntry is not null
                        ? $"Selected folder contains unsupported Git entry '{invalidEntry.Path}' ({invalidEntry.Type}, mode {invalidEntry.Mode})."
                        : metadata.Error;
            metadataValid &= !caseCollision;
            var contentIdentity = folder.Length == 0
                ? tree.Sha
                : tree.Entries.Single(entry => entry.Type == "tree" && string.Equals(entry.Path, folder, StringComparison.Ordinal)).Sha;
            skills.Add(new SourceSkill(
                skillPath,
                JoinRepositoryPath(requestedRoot, folder),
                folderName,
                metadata.DeclaredName,
                metadata.Description,
                metadataValid,
                metadataError,
                files,
                contentIdentity,
                blobIdentities));
        }

        log.Info($"Inspection found {skills.Count} Source Skill(s) below '{(resolvedReference.RequestedPath ?? "(root)")}' at commit {commit.Sha}.");
        return new SourceInspection(resolvedReference, repository, requestedRef, trackingRuleKind, commit, providerVersion, skills);
    }

    private (GitHubSourceReference Reference, string RequestedRef, State.TrackingRuleKind Kind, ResolvedCommit Commit) ResolveReference(
        GitHubSourceReference reference,
        RepositoryFacts repository)
    {
        if (reference.TreeSegments is null)
        {
            var commit = client.ResolveCommit(reference.Owner, reference.Repository, repository.DefaultBranch);
            return (reference.ResolveTreeBoundary(repository.DefaultBranch, null), repository.DefaultBranch, State.TrackingRuleKind.Branch, commit);
        }

        for (var count = reference.TreeSegments.Count; count >= 1; count--)
        {
            var candidate = string.Join('/', reference.TreeSegments.Take(count));
            State.TrackingRuleKind? kind = null;
            if (string.Equals(candidate, repository.DefaultBranch, StringComparison.Ordinal)
                || client.ReferenceExists(reference.Owner, reference.Repository, GitHubReferenceKind.Branch, candidate))
            {
                kind = State.TrackingRuleKind.Branch;
            }
            else if (client.ReferenceExists(reference.Owner, reference.Repository, GitHubReferenceKind.Tag, candidate))
            {
                kind = State.TrackingRuleKind.Tag;
            }
            else if (System.Text.RegularExpressions.Regex.IsMatch(
                         candidate,
                         "^[0-9a-fA-F]{40}$",
                         System.Text.RegularExpressions.RegexOptions.CultureInvariant))
            {
                kind = State.TrackingRuleKind.Commit;
            }

            if (kind is null)
            {
                continue;
            }

            try
            {
                var commit = client.ResolveCommit(reference.Owner, reference.Repository, candidate);
                var path = count == reference.TreeSegments.Count
                    ? null
                    : string.Join('/', reference.TreeSegments.Skip(count));
                return (reference.ResolveTreeBoundary(candidate, path), candidate, kind.Value, commit);
            }
            catch (GhSourceUnavailableException)
            {
            }
        }

        throw new GhSourceUnavailableException(
            $"No candidate ref prefix in the GitHub tree URL resolves to an exact branch, tag, or commit.");
    }

    private static string JoinRepositoryPath(string root, string relative)
        => root.Length == 0 ? relative : relative.Length == 0 ? root : root + "/" + relative;

    private static bool IsBelow(string path, string folder)
        => folder.Length == 0 || path.StartsWith(folder + "/", StringComparison.Ordinal);

    private static bool IsSafeRelativePath(string path)
    {
        if (path.StartsWith('/') || path.Contains('\\'))
        {
            return false;
        }
        foreach (var segment in path.Split('/'))
        {
            if (segment.Length == 0 || segment is "." or ".." || segment.Any(char.IsControl)
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            {
                return false;
            }
            var stem = segment.Split('.')[0];
            if (stem.Equals("CON", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("PRN", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("AUX", StringComparison.OrdinalIgnoreCase)
                || stem.Equals("NUL", StringComparison.OrdinalIgnoreCase)
                || System.Text.RegularExpressions.Regex.IsMatch(stem, "^(COM|LPT)[1-9]$", System.Text.RegularExpressions.RegexOptions.IgnoreCase))
            {
                return false;
            }
        }
        return true;
    }
}
