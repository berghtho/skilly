using Skilly.Skills;
using Skilly.State;

namespace Skilly.Providers.GitHub;

public sealed record CheckResult(
    UpdateStatus Status,
    string InstalledRevision,
    DateTimeOffset? InstalledRevisionDate,
    string? AvailableRevision,
    DateTimeOffset? AvailableRevisionDate,
    string? AvailablePayloadHash,
    DateTimeOffset CheckedAt,
    string? Warning = null,
    string? AvailableContentIdentity = null);

public sealed record GitHubPayload(
    IReadOnlyList<(string RelativePath, byte[] Content)> Files,
    string Hash,
    string ContentIdentity);

public sealed class GitHubChecker(GhClient client)
{
    public CheckResult Check(ManagementRecord record, CommitResolutionCache? commitCache = null)
    {
        if (!string.Equals(record.Provenance.SourceProvider, "github", StringComparison.Ordinal))
        {
            throw new GhApiException("The Management Record is not owned by the GitHub provider.");
        }

        var provenance = record.Provenance;
        ResolvedCommit commit;
        try
        {
            commit = commitCache is null
                ? client.ResolveCommit(provenance.Owner, provenance.Repository, provenance.TrackingRule)
                : commitCache.Resolve(client, provenance);
        }
        catch (GhSourceUnavailableException exception)
        {
            return new CheckResult(
                UpdateStatus.SourceUnavailable,
                record.InstalledRevision,
                null,
                null,
                null,
                null,
                DateTimeOffset.Now,
                exception.Message);
        }

        GitHubPayload payload;
        try
        {
            payload = FetchPayload(record, commit.Sha);
        }
        catch (GhSourceUnavailableException exception)
        {
            return new CheckResult(
                UpdateStatus.SourceUnavailable,
                record.InstalledRevision,
                null,
                commit.Sha,
                null,
                null,
                DateTimeOffset.Now,
                exception.Message);
        }

        var repositoryPath = RepositoryPath(provenance);
        var installedDate = client.GetSkillRevisionDate(
            provenance.Owner,
            provenance.Repository,
            record.InstalledRevision,
            repositoryPath);
        var availableDate = string.Equals(commit.Sha, record.InstalledRevision, StringComparison.OrdinalIgnoreCase)
            ? installedDate
            : client.GetSkillRevisionDate(
                provenance.Owner,
                provenance.Repository,
                commit.Sha,
                repositoryPath);

        if (provenance.TrackingRuleKind is TrackingRuleKind.Commit or TrackingRuleKind.Tag)
        {
            var moved = provenance.TrackingRuleKind == TrackingRuleKind.Tag
                        && !string.Equals(commit.Sha, record.InstalledRevision, StringComparison.OrdinalIgnoreCase);
            return new CheckResult(
                UpdateStatus.Pinned,
                record.InstalledRevision,
                installedDate,
                commit.Sha,
                availableDate,
                payload.Hash,
                DateTimeOffset.Now,
                moved ? "The pinned tag now resolves to a different commit; Skilly will not update it automatically." : null,
                payload.ContentIdentity);
        }

        var contentIsCurrent = provenance.SelectedContentIdentity.StartsWith("payload-sha256:", StringComparison.Ordinal)
            ? string.Equals(payload.Hash, provenance.SelectedContentIdentity["payload-sha256:".Length..], StringComparison.OrdinalIgnoreCase)
            : string.Equals(payload.ContentIdentity, provenance.SelectedContentIdentity, StringComparison.Ordinal);
        var status = contentIsCurrent
            ? UpdateStatus.Current
            : UpdateStatus.UpdateAvailable;
        return new CheckResult(
            status,
            record.InstalledRevision,
            installedDate,
            commit.Sha,
            availableDate,
            payload.Hash,
            DateTimeOffset.Now,
            AvailableContentIdentity: payload.ContentIdentity);
    }

    public GitHubPayload FetchPayload(ManagementRecord record, string revision)
    {
        var provenance = record.Provenance;
        var repositoryPath = RepositoryPath(provenance);
        TreeSnapshot tree;
        try
        {
            tree = client.GetTreeBelowPath(provenance.Owner, provenance.Repository, revision, repositoryPath);
        }
        catch (GhSourceUnavailableException exception)
        {
            throw new GhSourceUnavailableException(
                $"The selected Source Skill '{provenance.SourceSkillPath}' no longer contains SKILL.md. {exception.Message}");
        }
        var files = tree.Entries
            .Where(static entry => entry.Type == "blob")
            .Select(entry => repositoryPath.Length == 0 ? entry.Path : repositoryPath + "/" + entry.Path)
            .ToList();
        var blobIdentities = tree.Entries
            .Where(static entry => entry.Type == "blob")
            .ToDictionary(
                entry => repositoryPath.Length == 0 ? entry.Path : repositoryPath + "/" + entry.Path,
                static entry => entry.Sha,
                StringComparer.Ordinal);
        if (!files.Contains(repositoryPath.Length == 0 ? "SKILL.md" : repositoryPath + "/SKILL.md", StringComparer.Ordinal))
        {
            throw new GhSourceUnavailableException(
                $"The selected Source Skill '{provenance.SourceSkillPath}' no longer contains SKILL.md.");
        }

        var payload = client.FetchFolder(
            provenance.Owner,
            provenance.Repository,
            revision,
            repositoryPath,
            files,
            expectedBlobIdentities: blobIdentities);
        return new GitHubPayload(payload, PayloadHasher.HashFiles(payload), tree.Sha);
    }

    internal static string RepositoryPath(ProvenanceInfo provenance)
    {
        var requestedPath = provenance.RequestedPath?.Trim('/') ?? string.Empty;
        if (provenance.SourceSkillPath == ".")
        {
            return requestedPath;
        }

        return requestedPath.Length == 0
            ? provenance.SourceSkillPath
            : requestedPath + "/" + provenance.SourceSkillPath;
    }
}
