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
    string? Warning = null);

public sealed record GitHubPayload(IReadOnlyList<(string RelativePath, byte[] Content)> Files, string Hash);

public sealed class GitHubChecker(GhClient client)
{
    public CheckResult Check(ManagementRecord record)
    {
        if (!string.Equals(record.Provenance.SourceProvider, "github", StringComparison.Ordinal))
        {
            throw new GhApiException("The Management Record is not owned by the GitHub provider.");
        }

        var provenance = record.Provenance;
        ResolvedCommit commit;
        try
        {
            commit = client.ResolveCommit(provenance.Owner, provenance.Repository, provenance.TrackingRule);
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
                moved ? "The pinned tag now resolves to a different commit; Skilly will not update it automatically." : null);
        }

        var status = string.Equals(payload.Hash, record.InstalledPayloadHash, StringComparison.OrdinalIgnoreCase)
            ? UpdateStatus.Current
            : UpdateStatus.UpdateAvailable;
        return new CheckResult(
            status,
            record.InstalledRevision,
            installedDate,
            commit.Sha,
            availableDate,
            payload.Hash,
            DateTimeOffset.Now);
    }

    public GitHubPayload FetchPayload(ManagementRecord record, string revision)
    {
        var provenance = record.Provenance;
        var tree = client.GetTree(provenance.Owner, provenance.Repository, revision);
        var repositoryPath = RepositoryPath(provenance);
        var prefix = repositoryPath.Length == 0 ? string.Empty : repositoryPath + "/";
        var files = tree.Entries
            .Where(static entry => entry.Type == "blob")
            .Where(entry => prefix.Length == 0 || entry.Path.StartsWith(prefix, StringComparison.Ordinal))
            .Select(entry => entry.Path)
            .ToList();
        if (!files.Contains(repositoryPath.Length == 0 ? "SKILL.md" : repositoryPath + "/SKILL.md", StringComparer.Ordinal))
        {
            throw new GhSourceUnavailableException(
                $"The selected Source Skill '{provenance.SourceSkillPath}' no longer contains SKILL.md.");
        }

        var payload = files.Select(path =>
        {
            var relative = repositoryPath.Length == 0 ? path : path[(repositoryPath.Length + 1)..];
            return (relative, client.GetFileContent(provenance.Owner, provenance.Repository, path, revision));
        }).ToList();
        return new GitHubPayload(payload, PayloadHasher.HashFiles(payload));
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
