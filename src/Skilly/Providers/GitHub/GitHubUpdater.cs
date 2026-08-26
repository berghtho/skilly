using System.IO;
using Skilly.Infrastructure;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.Providers.GitHub;

public sealed record UpdateResult(string InstallationId, string InstalledRevision);

public sealed class GitHubUpdater(
    GitHubChecker checker,
    StateStore stateStore,
    RollingLog log)
{
    public UpdateResult Update(ManagementRecord requestedRecord)
    {
        var state = stateStore.Load();
        if (state.PendingOperation is not null)
        {
            throw new ProviderFailure("Another mutation is pending. Skilly remains read-only until that operation is reconciled.");
        }

        var record = state.Records.SingleOrDefault(candidate =>
            string.Equals(candidate.InstallationId, requestedRecord.InstallationId, StringComparison.Ordinal));
        if (record is null || !string.Equals(record.Provenance.SourceProvider, "github", StringComparison.Ordinal))
        {
            throw new ProviderFailure("The selected installation has no current GitHub Management Record.");
        }

        var check = record.LatestCheck;
        if (record.Provenance.TrackingRuleKind != TrackingRuleKind.Branch
            || check is null
            || check.Status != UpdateStatus.UpdateAvailable
            || check.IsStale
            || check.Failure is not null
            || string.IsNullOrWhiteSpace(check.AvailableRevision)
            || string.IsNullOrWhiteSpace(check.AvailablePayloadHash))
        {
            throw new ProviderFailure("A fresh branch Check reporting Update Available is required before direct update.");
        }

        var currentHash = VerifyInstalledPreconditions(record);

        var payload = checker.FetchPayload(record, check.AvailableRevision);
        if (!string.Equals(payload.Hash, check.AvailablePayloadHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderFailure("The selected source content changed after Check; refresh checks before updating.");
        }

        currentHash = VerifyInstalledPreconditions(record);

        var canonicalParent = Path.GetDirectoryName(record.CanonicalPath)!;
        var operationId = Guid.NewGuid().ToString("N");
        var stagingPath = Path.Combine(canonicalParent, $".{Path.GetFileName(record.CanonicalPath)}.skilly-stage-{operationId}");
        var backupPath = Path.Combine(canonicalParent, $".{Path.GetFileName(record.CanonicalPath)}.skilly-backup-{operationId}");
        var previousRevision = record.InstalledRevision;
        var previousHash = record.InstalledPayloadHash;
        var previousFileCount = record.InstalledFileCount;
        var previousEvidence = record.ProviderEvidence;
        var previousOutcome = record.LastOperationOutcome;
        var previousResolvedCommit = record.Provenance.ResolvedCommit;
        var previousCheck = record.LatestCheck;
        var journaled = false;
        var canonicalMoved = false;
        var replacementMoved = false;

        try
        {
            state.PendingOperation = new PendingOperation
            {
                OperationType = MutationType.Update,
                AffectedInstallationIds = [record.InstallationId],
                StartingPaths = [record.CanonicalPath, record.IntendedClaudeJunctionPath!],
                StartingHashes = [currentHash, null],
                StartedAt = DateTimeOffset.Now,
            };
            stateStore.Save(state);
            journaled = true;

            Materialize(stagingPath, payload.Files);
            VerifyPayload(stagingPath, Path.GetFileName(record.CanonicalPath), payload.Hash, payload.Files.Count);
            VerifyInstalledPreconditions(record);

            Directory.Move(record.CanonicalPath, backupPath);
            canonicalMoved = true;
            Directory.Move(stagingPath, record.CanonicalPath);
            replacementMoved = true;

            VerifyPayload(record.CanonicalPath, Path.GetFileName(record.CanonicalPath), payload.Hash, payload.Files.Count);
            if (!Junction.IsJunctionTo(record.IntendedClaudeJunctionPath!, record.CanonicalPath))
            {
                throw new ProviderFailure("The existing Claude junction did not resolve to the updated canonical installation.");
            }

            record.InstalledRevision = check.AvailableRevision;
            record.InstalledPayloadHash = payload.Hash;
            record.InstalledFileCount = payload.Files.Count;
            var repositoryPath = GitHubChecker.RepositoryPath(record.Provenance);
            record.ProviderEvidence = $"gh api contents/{(repositoryPath.Length == 0 ? "." : repositoryPath)}@{check.AvailableRevision}";
            record.Provenance.ResolvedCommit = check.AvailableRevision;
            record.LastOperationOutcome = OperationOutcome.Updated;
            record.LatestCheck = new CheckSnapshot
            {
                Status = UpdateStatus.Current,
                InstalledRevision = check.AvailableRevision,
                InstalledRevisionDate = check.AvailableRevisionDate,
                AvailableRevision = check.AvailableRevision,
                AvailableRevisionDate = check.AvailableRevisionDate,
                AvailablePayloadHash = payload.Hash,
                CheckedAt = DateTimeOffset.Now,
            };
            state.PendingOperation = null;
            state.LastOperationNote = $"updated GitHub Skill '{record.Provenance.SourceSkillPath}'";
            stateStore.Save(state);

            var persistedState = stateStore.Load();
            var persisted = persistedState.Records.Single(candidate => candidate.InstallationId == record.InstallationId);
            if (!string.Equals(persisted.InstalledRevision, check.AvailableRevision, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(persisted.InstalledPayloadHash, payload.Hash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(persisted.Provenance.ResolvedCommit, check.AvailableRevision, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(persisted.ProviderEvidence, record.ProviderEvidence, StringComparison.Ordinal)
                || persisted.LatestCheck?.Status != UpdateStatus.Current
                || persisted.LastOperationOutcome != OperationOutcome.Updated
                || persistedState.PendingOperation is not null
                || persisted.IntendedClaudeJunctionPath is null
                || !Junction.IsJunctionTo(persisted.IntendedClaudeJunctionPath, persisted.CanonicalPath))
            {
                throw new ProviderFailure("Durable state did not retain the verified GitHub update result.");
            }

            Directory.Delete(backupPath, recursive: true);
            log.Info($"Updated and verified '{record.Provenance.SourceSkillPath}' at revision {record.DisplayRevision}.");
            return new UpdateResult(record.InstallationId, record.InstalledRevision);
        }
        catch (Exception exception)
        {
            var restored = Restore(record.CanonicalPath, stagingPath, backupPath, canonicalMoved, replacementMoved);
            record.InstalledRevision = previousRevision;
            record.InstalledPayloadHash = previousHash;
            record.InstalledFileCount = previousFileCount;
            record.ProviderEvidence = previousEvidence;
            record.LastOperationOutcome = previousOutcome;
            record.Provenance.ResolvedCommit = previousResolvedCommit;
            record.LatestCheck = previousCheck;
            if (journaled && restored)
            {
                state.PendingOperation = null;
            }

            state.LastOperationNote = restored
                ? $"GitHub update failed and was rolled back: {exception.Message}"
                : $"GitHub update failed; recovery required: {exception.Message}";
            if (journaled)
            {
                stateStore.Save(state);
            }

            log.Error(state.LastOperationNote, exception);
            throw new ProviderFailure(restored
                ? $"Update failed; the prior installation was preserved. {exception.Message}"
                : $"Update failed and restoration could not be proven. Recovery Required. {exception.Message}");
        }
    }

    private static string VerifyInstalledPreconditions(ManagementRecord record)
    {
        if (!Directory.Exists(record.CanonicalPath))
        {
            throw new ProviderFailure("The canonical Skill Installation is missing; direct update is unavailable.");
        }

        var currentHash = PayloadHasher.HashFolder(record.CanonicalPath);
        if (!string.Equals(currentHash, record.InstalledPayloadHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderFailure("Direct update refused because installed content no longer matches the recorded payload hash.");
        }

        if (record.IntendedClaudeJunctionPath is null
            || !Junction.IsJunctionTo(record.IntendedClaudeJunctionPath, record.CanonicalPath))
        {
            throw new ProviderFailure("Direct update refused because the existing Claude junction is missing or targets another folder.");
        }

        return currentHash;
    }

    private static void Materialize(string destination, IReadOnlyList<(string RelativePath, byte[] Content)> files)
    {
        Directory.CreateDirectory(destination);
        var destinationRoot = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar)
                              + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
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
    }

    private static void VerifyPayload(string path, string folderName, string expectedHash, int expectedFileCount)
    {
        var actualHash = PayloadHasher.HashFolder(path);
        if (!string.Equals(actualHash, expectedHash, StringComparison.OrdinalIgnoreCase)
            || Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count() != expectedFileCount)
        {
            throw new ProviderFailure("The canonical payload does not match the verified selected source content.");
        }

        var metadata = SkillMdReader.Read(path, folderName);
        if (metadata.Status != MetadataReadStatus.Valid)
        {
            throw new ProviderFailure($"The updated SKILL.md did not validate: {metadata.Error}");
        }
    }

    private static bool Restore(
        string canonicalPath,
        string stagingPath,
        string backupPath,
        bool canonicalMoved,
        bool replacementMoved)
    {
        try
        {
            if (replacementMoved && Directory.Exists(canonicalPath))
            {
                Directory.Delete(canonicalPath, recursive: true);
            }

            if (canonicalMoved && Directory.Exists(backupPath))
            {
                Directory.Move(backupPath, canonicalPath);
            }

            if (Directory.Exists(stagingPath))
            {
                Directory.Delete(stagingPath, recursive: true);
            }

            return !canonicalMoved || Directory.Exists(canonicalPath);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
