using System.IO;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.Providers.GitHub;

public sealed record AdoptionDiscovery(IReadOnlyList<AdoptionEvidence> Evidence, IReadOnlyList<string> Diagnostics);

public sealed class GitHubAdoptionVerifier(GhClient client, string home)
{
    public AdoptionDiscovery Discover(SourceInspection inspection, InventorySnapshot inventory)
    {
        var evidence = new List<AdoptionEvidence>();
        var diagnostics = new List<string>();
        var canonicalCandidates = inventory.Entries.Where(static entry =>
            entry.RootKind == RootKind.CanonicalAgents
            && entry.Kind == EntryKind.RealFolder
            && entry.ManagementStatus == ManagementStatus.Unmanaged
            && entry.Health == InstallationHealth.Healthy
            && entry.Metadata.Status == MetadataReadStatus.Valid
            && entry.Exposures[Harness.ClaudeCode].State is ExposureState.MissingJunction or ExposureState.VerifiedJunction);

        foreach (var entry in canonicalCandidates)
        {
            var matches = inspection.Skills.Where(skill =>
                skill.MetadataValid
                && string.Equals(skill.FolderName, entry.FolderName, StringComparison.OrdinalIgnoreCase)).ToList();
            if (matches.Count != 1)
            {
                if (matches.Count > 1)
                {
                    diagnostics.Add($"'{entry.LocalPath}' has ambiguous Source Skill evidence and remains Unmanaged.");
                }
                continue;
            }

            var skill = matches[0];
            try
            {
                var files = FetchPayload(inspection, skill);
                var sourceHash = PayloadHasher.HashFiles(files);
                var localHash = PayloadHasher.HashFolder(entry.LocalPath);
                if (!string.Equals(sourceHash, localHash, StringComparison.OrdinalIgnoreCase)
                    || files.Count != Directory.EnumerateFiles(entry.LocalPath, "*", SearchOption.AllDirectories).Count())
                {
                    diagnostics.Add($"'{entry.LocalPath}' does not exactly match '{skill.SkillPath}' and remains Unmanaged.");
                    continue;
                }

                var canonicalPath = Path.GetFullPath(entry.LocalPath);
                var claudePath = Path.GetFullPath(Path.Combine(
                    HarnessRoot.Create(RootKind.ClaudeSkills, home).FullPath,
                    entry.FolderName));
                var repositoryPath = skill.RepositoryPath.Length == 0 ? "." : skill.RepositoryPath;
                var record = new ManagementRecord
                {
                    InstallationId = Guid.NewGuid().ToString("N"),
                    CanonicalPath = canonicalPath,
                    Provenance = new ProvenanceInfo
                    {
                        SourceProvider = "github",
                        OriginalReference = inspection.Reference.Original,
                        NormalizedSource = inspection.Reference.NormalizedSource,
                        Host = inspection.Reference.Host,
                        Owner = inspection.Reference.Owner,
                        Repository = inspection.Reference.Repository,
                        RequestedPath = inspection.Reference.RequestedPath,
                        SourceSkillPath = skill.SkillPath,
                        TrackingRule = inspection.RequestedTrackingRule,
                        TrackingRuleKind = inspection.TrackingRuleKind,
                        ResolvedCommit = inspection.Commit.Sha,
                        ProviderVersion = inspection.ProviderVersion,
                    },
                    IntendedClaudeJunctionPath = claudePath,
                    InstalledRevision = inspection.Commit.Sha,
                    InstalledPayloadHash = sourceHash,
                    InstalledFileCount = files.Count,
                    ProviderEvidence = $"gh api contents/{repositoryPath}@{inspection.Commit.Sha}",
                };
                evidence.Add(new AdoptionEvidence(record, sourceHash, files.Count));
            }
            catch (Exception exception)
            {
                diagnostics.Add($"'{entry.LocalPath}' could not be verified and remains Unmanaged: {exception.Message}");
            }
        }

        return new AdoptionDiscovery(evidence, diagnostics);
    }

    private List<(string RelativePath, byte[] Content)> FetchPayload(SourceInspection inspection, SourceSkill skill)
    {
        return skill.FilePaths.Select(path =>
        {
            var relative = skill.RepositoryPath.Length == 0
                ? path
                : path[(skill.RepositoryPath.Length + 1)..];
            return (relative, client.GetFileContent(
                inspection.Reference.Owner,
                inspection.Reference.Repository,
                path,
                inspection.Commit.Sha));
        }).ToList();
    }
}
