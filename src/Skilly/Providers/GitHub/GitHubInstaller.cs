using System.IO;
using Skilly.Infrastructure;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.Providers.GitHub;

public sealed class ProviderFailure : Exception
{
    public ProviderFailure(string message) : base(message)
    {
    }
}

public sealed record InstalledSkill(string SkillPath, string CanonicalPath);

public sealed record InstallResult(IReadOnlyList<InstalledSkill> InstalledSkills)
{
    public int SucceededCount => InstalledSkills.Count;
}

public sealed class GitHubInstaller(
    GhClient client,
    StateStore stateStore,
    RollingLog log,
    string home)
{
    public InstallResult Install(
        SourceInspection inspection,
        IReadOnlyList<SourceSkill> selected,
        CancellationToken cancellationToken = default)
    {
        if (selected.Count == 0)
        {
            throw new ProviderFailure("No Source Skills are selected; installation is unavailable.");
        }

        if (selected.Any(static skill => !skill.MetadataValid))
        {
            throw new ProviderFailure("Every selected Source Skill must have valid SKILL.md metadata before installation.");
        }

        var canonicalRoot = HarnessRoot.Create(RootKind.CanonicalAgents, home).FullPath;
        var claudeRoot = HarnessRoot.Create(RootKind.ClaudeSkills, home).FullPath;
        var destinations = selected.ToDictionary(
            skill => skill.SkillPath,
            skill => Path.Combine(canonicalRoot, skill.FolderName),
            StringComparer.Ordinal);
        var claudeDestinations = selected.ToDictionary(
            skill => skill.SkillPath,
            skill => Path.Combine(claudeRoot, skill.FolderName),
            StringComparer.Ordinal);

        if (destinations.Values.Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count)
        {
            throw new ProviderFailure("Two selected Source Skills resolve to the same canonical folder name.");
        }

        foreach (var destination in destinations.Values)
        {
            if (PathEntryExists(destination))
            {
                throw new ProviderFailure(
                    $"'{destination}' already exists. Installing over it would create a Collision; existing content is never silently overwritten.");
            }
        }

        foreach (var claudeDestination in claudeDestinations.Values)
        {
            if (PathEntryExists(claudeDestination))
            {
                throw new ProviderFailure(
                    $"'{claudeDestination}' already exists. A copied or foreign Claude entry cannot be accepted as an exposure.");
            }
        }

        var state = stateStore.Load();
        if (state.PendingOperation is not null)
        {
            throw new ProviderFailure("Another mutation is pending. Skilly remains read-only until that operation is reconciled.");
        }

        var installationIds = selected.ToDictionary(
            static skill => skill.SkillPath,
            static _ => Guid.NewGuid().ToString("N"),
            StringComparer.Ordinal);
        var startingPaths = destinations.Values.Concat(claudeDestinations.Values).ToList();
        var pending = new PendingOperation
        {
            OperationId = Guid.NewGuid().ToString("N"),
            OperationType = MutationType.Install,
            AffectedInstallationIds = [.. installationIds.Values],
            StartingPaths = startingPaths,
            StartingHashes = [.. startingPaths.Select(static _ => (string?)null)],
            StartingPathStates = [.. startingPaths.Select(static _ => PathState.Missing)],
            Phase = PendingOperationPhase.Journaled,
            StartedAt = DateTimeOffset.Now,
        };

        state.PendingOperation = pending;
        stateStore.Save(state);
        log.Info($"Pending install recorded for {destinations.Count} skill(s).");

        var created = new List<(string Destination, string ClaudeJunction)>();
        var records = new List<ManagementRecord>();
        try
        {
            Directory.CreateDirectory(canonicalRoot);
            foreach (var skill in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var destination = destinations[skill.SkillPath];
                var claudeJunctionPath = claudeDestinations[skill.SkillPath];

                created.Add((destination, claudeJunctionPath));
                var record = InstallSingle(
                    inspection,
                    skill,
                    installationIds[skill.SkillPath],
                    destination,
                    claudeJunctionPath,
                    cancellationToken);
                records.Add(record);
                log.Info($"Verified '{skill.SkillPath}' at revision {record.DisplayRevision}.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            state.Records.AddRange(records);
            state.PendingOperation = null;
            state.LastOperationNote = $"installed {records.Count} GitHub Skill(s)";
            stateStore.Save(state);

            return new InstallResult(records.Select(record => new InstalledSkill(
                record.Provenance.SourceSkillPath,
                record.CanonicalPath)).ToList());
        }
        catch (OperationCanceledException)
        {
            pending.CancellationRequested = true;
            state.PendingOperation = pending;
            state.LastOperationNote = "Install cancellation requested; pending operation retained for restart recovery.";
            stateStore.Save(state);
            throw;
        }
        catch (Exception exception)
        {
            var installationIdSet = installationIds.Values.ToHashSet(StringComparer.Ordinal);
            state.Records.RemoveAll(record => installationIdSet.Contains(record.InstallationId));
            var restored = true;
            foreach (var path in created.AsEnumerable().Reverse())
            {
                restored &= Rollback(path.Destination, path.ClaudeJunction);
            }

            if (restored)
            {
                state.PendingOperation = null;
            }

            state.LastOperationNote = restored
                ? $"GitHub install failed and was rolled back: {exception.Message}"
                : $"GitHub install failed; recovery required: {exception.Message}";
            stateStore.Save(state);
            log.Error(state.LastOperationNote, exception);
            throw new ProviderFailure(restored
                ? $"Installation failed; created content was removed. {exception.Message}"
                : $"Installation failed and restoration could not be proven. Recovery Required. {exception.Message}");
        }
    }

    private ManagementRecord InstallSingle(
        SourceInspection inspection,
        SourceSkill skill,
        string installationId,
        string destination,
        string claudeJunctionPath,
        CancellationToken cancellationToken)
    {
        var files = client.FetchFolder(
            inspection.Reference.Owner,
            inspection.Reference.Repository,
            inspection.Commit.Sha,
            skill.RepositoryPath,
            skill.FilePaths,
            cancellationToken,
            skill.BlobIdentities);

        foreach (var file in files)
        {
            var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)
                                  + Path.DirectorySeparatorChar;
            var target = Path.GetFullPath(Path.Combine(
                destination,
                file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(destinationRoot, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProviderFailure($"Source path '{file.RelativePath}' escapes its selected Source Skill folder.");
            }

            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.Content);
        }

        cancellationToken.ThrowIfCancellationRequested();
        Junction.Create(claudeJunctionPath, destination);

        var expectedHash = PayloadHasher.HashFiles(files);
        var actualHash = PayloadHasher.HashFolder(destination);
        if (!string.Equals(expectedHash, actualHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderFailure("The materialized canonical payload does not match the fetched source content.");
        }

        var metadata = SkillMdReader.Read(destination, skill.FolderName);
        if (metadata.Status != MetadataReadStatus.Valid)
        {
            throw new ProviderFailure($"The installed SKILL.md did not validate: {metadata.Error}");
        }

        if (!Junction.IsJunctionTo(claudeJunctionPath, destination))
        {
            throw new ProviderFailure($"The Claude exposure at '{claudeJunctionPath}' is not a junction to the canonical installation.");
        }

        return new ManagementRecord
        {
            InstallationId = installationId,
            CanonicalPath = Path.GetFullPath(destination),
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
                SelectedContentIdentity = skill.ContentIdentity,
                ProviderVersion = inspection.ProviderVersion,
            },
            IntendedClaudeJunctionPath = Path.GetFullPath(claudeJunctionPath),
            InstalledRevision = inspection.Commit.Sha,
            InstalledPayloadHash = actualHash,
            InstalledFileCount = files.Count,
            ProviderEvidence = $"gh api contents/{(skill.RepositoryPath.Length == 0 ? "." : skill.RepositoryPath)}@{inspection.Commit.Sha}",
            LastOperationOutcome = OperationOutcome.Installed,
        };
    }

    private bool Rollback(string destination, string claudeJunctionPath)
    {
        var restored = true;
        try
        {
            if (PathEntryExists(claudeJunctionPath))
            {
                Directory.Delete(claudeJunctionPath, recursive: false);
            }
        }
        catch (Exception exception)
        {
            restored = false;
            log.Error($"Could not remove the Claude entry '{claudeJunctionPath}' during rollback.", exception);
        }

        try
        {
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
        }
        catch (Exception exception)
        {
            restored = false;
            log.Error($"Could not remove the canonical folder '{destination}' during rollback.", exception);
        }

        return restored;
    }

    private static bool PathEntryExists(string path)
    {
        try
        {
            _ = File.GetAttributes(path);
            return true;
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }
}
