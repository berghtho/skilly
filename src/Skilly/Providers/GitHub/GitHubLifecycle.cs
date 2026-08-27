using System.IO;
using Skilly.Infrastructure;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.Providers.GitHub;

public sealed record ManagedReinstallPlan(
    string InstallationId,
    string ExactPath,
    string Revision,
    string PayloadHash,
    string ContentIdentity,
    int FileCount,
    string ProviderEvidence,
    GitHubPayload Payload,
    string StartingPayloadHash) : IManagedReinstallPlan
{
    public IReadOnlyList<string> AffectedPaths => [ExactPath];
}

public sealed record LifecycleResult(string ExactPath, string Message);

public enum RecoveryDisposition
{
    None,
    Completed,
    Restored,
    RecoveryRequired,
}

public sealed record RecoveryResult(RecoveryDisposition Disposition, string Message);

public sealed class GitHubLifecycle(
    GitHubChecker checker,
    StateStore stateStore,
    RollingLog log)
{
    public bool RecoveryRequired
    {
        get
        {
            if (stateStore.RecoveryRequired)
            {
                return true;
            }

            try
            {
                return stateStore.Load().PendingOperation is not null;
            }
            catch (RecoveryRequiredException)
            {
                return true;
            }
        }
    }

    public string RecoveryDiagnostic => stateStore.RecoveryDiagnostic
                                        ?? "A pending mutation requires restart recovery before further mutation.";

    public ManagedReinstallPlan PlanManagedReinstall(ManagementRecord requestedRecord)
    {
        var state = RequireWritableState();
        var record = FindGitHubRecord(state, requestedRecord.InstallationId);
        var startingPayloadHash = RecheckManagedPath(record, allowLocalModification: true, requireHealthyExposure: false);

        var check = checker.Check(record);
        if (check.Status is UpdateStatus.SourceUnavailable or UpdateStatus.CheckFailed
            || string.IsNullOrWhiteSpace(check.AvailableRevision)
            || string.IsNullOrWhiteSpace(check.AvailablePayloadHash))
        {
            throw new ProviderFailure("Managed Reinstall requires source content at a verified revision.");
        }

        var payload = checker.FetchPayload(record, check.AvailableRevision);
        if (!string.Equals(payload.Hash, check.AvailablePayloadHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(payload.ContentIdentity, check.AvailableContentIdentity, StringComparison.Ordinal))
        {
            throw new ProviderFailure("The source payload changed while preparing Managed Reinstall.");
        }

        VerifyPayloadFiles(payload, Path.GetFileName(record.CanonicalPath));
        var repositoryPath = GitHubChecker.RepositoryPath(record.Provenance);
        return new ManagedReinstallPlan(
            record.InstallationId,
            record.CanonicalPath,
            check.AvailableRevision,
            payload.Hash,
            payload.ContentIdentity,
            payload.Files.Count,
            $"gh api contents/{(repositoryPath.Length == 0 ? "." : repositoryPath)}@{check.AvailableRevision}",
            payload,
            startingPayloadHash);
    }

    public LifecycleResult Adopt(AdoptionEvidence evidence, CancellationToken cancellationToken = default)
    {
        var state = RequireWritableState();
        var record = evidence.ProposedRecord;
        ValidateAdoptionEvidence(state, evidence);

        var sourcePayload = checker.FetchPayload(record, record.InstalledRevision);
        if (!string.Equals(sourcePayload.Hash, evidence.ExpectedPayloadHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(sourcePayload.ContentIdentity, evidence.ExpectedContentIdentity, StringComparison.Ordinal)
            || !string.Equals(sourcePayload.ContentIdentity, record.Provenance.SelectedContentIdentity, StringComparison.Ordinal)
            || sourcePayload.Files.Count != evidence.ExpectedFileCount)
        {
            throw new ProviderFailure("The immutable provider payload no longer matches the verified Adoption evidence.");
        }

        return AdoptVerifiedEvidence(state, evidence, cancellationToken);
    }

    public LifecycleResult AdoptVerifiedProviderEvidence(
        AdoptionEvidence evidence,
        Func<AdoptionEvidence?> reverify,
        CancellationToken cancellationToken = default)
    {
        var state = RequireWritableState();
        ValidateCommonAdoptionEvidence(state, evidence);
        var refreshed = reverify();
        if (refreshed is null || !MatchesProviderEvidence(evidence, refreshed))
        {
            throw new ProviderFailure("The provider lock or installed content changed after verification and remains Unmanaged.");
        }
        return AdoptVerifiedEvidence(state, evidence, cancellationToken);
    }

    private static bool MatchesProviderEvidence(AdoptionEvidence expected, AdoptionEvidence actual)
    {
        var expectedRecord = expected.ProposedRecord;
        var actualRecord = actual.ProposedRecord;
        var expectedProvenance = expectedRecord.Provenance;
        var actualProvenance = actualRecord.Provenance;
        return string.Equals(expected.ExpectedPayloadHash, actual.ExpectedPayloadHash, StringComparison.OrdinalIgnoreCase)
               && expected.ExpectedFileCount == actual.ExpectedFileCount
               && string.Equals(expected.ExpectedContentIdentity, actual.ExpectedContentIdentity, StringComparison.Ordinal)
               && string.Equals(expectedRecord.CanonicalPath, actualRecord.CanonicalPath, StringComparison.OrdinalIgnoreCase)
               && string.Equals(expectedRecord.IntendedClaudeJunctionPath, actualRecord.IntendedClaudeJunctionPath, StringComparison.OrdinalIgnoreCase)
               && string.Equals(expectedRecord.InstalledRevision, actualRecord.InstalledRevision, StringComparison.Ordinal)
               && string.Equals(expectedRecord.ProviderEvidence, actualRecord.ProviderEvidence, StringComparison.Ordinal)
               && string.Equals(expectedProvenance.SourceProvider, actualProvenance.SourceProvider, StringComparison.Ordinal)
               && string.Equals(expectedProvenance.OriginalReference, actualProvenance.OriginalReference, StringComparison.Ordinal)
               && string.Equals(expectedProvenance.NormalizedSource, actualProvenance.NormalizedSource, StringComparison.Ordinal)
               && string.Equals(expectedProvenance.Repository, actualProvenance.Repository, StringComparison.Ordinal)
               && string.Equals(expectedProvenance.SourceSkillPath, actualProvenance.SourceSkillPath, StringComparison.Ordinal)
               && string.Equals(expectedProvenance.TrackingRule, actualProvenance.TrackingRule, StringComparison.Ordinal)
               && expectedProvenance.TrackingRuleKind == actualProvenance.TrackingRuleKind
               && string.Equals(expectedProvenance.ResolvedCommit, actualProvenance.ResolvedCommit, StringComparison.Ordinal)
               && string.Equals(expectedProvenance.SelectedContentIdentity, actualProvenance.SelectedContentIdentity, StringComparison.Ordinal)
               && string.Equals(expectedProvenance.ProviderVersion, actualProvenance.ProviderVersion, StringComparison.Ordinal)
               && string.Equals(expectedProvenance.ProviderSkillName, actualProvenance.ProviderSkillName, StringComparison.Ordinal);
    }

    private LifecycleResult AdoptVerifiedEvidence(SkillyState state, AdoptionEvidence evidence, CancellationToken cancellationToken)
    {
        var record = evidence.ProposedRecord;
        var startingHash = PayloadHasher.HashFolder(record.CanonicalPath);
        if (!string.Equals(startingHash, evidence.ExpectedPayloadHash, StringComparison.OrdinalIgnoreCase)
            || Directory.EnumerateFiles(record.CanonicalPath, "*", SearchOption.AllDirectories).Count() != evidence.ExpectedFileCount)
        {
            throw new ProviderFailure("The Skill Installation changed after verification and remains Unmanaged.");
        }

        var junctionPath = record.IntendedClaudeJunctionPath!;
        var junctionExisted = PathEntryExists(junctionPath);
        if (junctionExisted && !Junction.IsJunctionTo(junctionPath, record.CanonicalPath))
        {
            throw new ProviderFailure("The Claude destination has conflicting topology; Adoption is unavailable.");
        }

        var pending = CreatePending(
            MutationType.Adoption,
            [record.InstallationId],
            [record.CanonicalPath, junctionPath],
            [startingHash, null],
            [PathState.Directory, junctionExisted ? PathState.Junction : PathState.Missing]);
        state.PendingOperation = pending;
        stateStore.Save(state);
        var createdJunction = false;
        var authorityAdded = false;
        try
        {
            ThrowIfCancellationRequested(pending, cancellationToken);
            SavePhase(state, pending, PendingOperationPhase.MutationStarted);
            if (!junctionExisted)
            {
                Junction.Create(junctionPath, record.CanonicalPath);
                createdJunction = true;
            }

            if (!MatchesHash(record.CanonicalPath, startingHash)
                || !Junction.IsJunctionTo(junctionPath, record.CanonicalPath))
            {
                throw new ProviderFailure("Adoption postconditions did not preserve exact content and Claude exposure topology.");
            }

            ThrowIfCancellationRequested(pending, cancellationToken);
            SavePhase(state, pending, PendingOperationPhase.Verified);
            record.LastOperationOutcome = OperationOutcome.Adopted;
            state.Records.Add(record);
            authorityAdded = true;
            state.PendingOperation = null;
            state.LastOperationNote = $"adopted {record.Provenance.SourceProvider} Skill '{record.Provenance.SourceSkillPath}' without rewriting content";
            stateStore.Save(state);
            return new LifecycleResult(record.CanonicalPath, "Adoption recorded exact verified Provenance; Skill content was preserved.");
        }
        catch (OperationCanceledException)
        {
            RetainForRestart(state, pending, "Adoption cancellation requested; restart recovery will reconcile it.");
            throw;
        }
        catch (Exception exception)
        {
            if (authorityAdded)
            {
                state.Records.Remove(record);
            }
            var restored = !createdJunction || RemoveCreatedAdoptionJunction(junctionPath, record.CanonicalPath);
            FinishFailedMutation(state, pending, restored, $"{record.Provenance.SourceProvider} Adoption failed: {exception.Message}");
            throw Failure("Adoption", restored, exception);
        }
    }

    public LifecycleResult ManagedReinstall(ManagedReinstallPlan plan, CancellationToken cancellationToken = default)
    {
        var state = RequireWritableState();
        var record = FindGitHubRecord(state, plan.InstallationId);
        if (!string.Equals(record.CanonicalPath, plan.ExactPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderFailure("The exact Managed Reinstall path changed after confirmation.");
        }

        var sourceAgain = checker.FetchPayload(record, plan.Revision);
        if (!string.Equals(sourceAgain.Hash, plan.PayloadHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(sourceAgain.ContentIdentity, plan.ContentIdentity, StringComparison.Ordinal))
        {
            throw new ProviderFailure("The verified Managed Reinstall payload changed after confirmation.");
        }

        var startingHash = RecheckManagedPath(record, allowLocalModification: true, requireHealthyExposure: false);
        if (!string.Equals(startingHash, plan.StartingPayloadHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderFailure("The local Skill content changed after the Managed Reinstall plan was confirmed; prepare a new plan.");
        }
        var junctionPath = record.IntendedClaudeJunctionPath!;
        var junctionExisted = PathEntryExists(junctionPath);
        if (junctionExisted && !Junction.IsJunctionTo(junctionPath, record.CanonicalPath))
        {
            throw new ProviderFailure($"The Claude destination '{junctionPath}' is a Collision and cannot be replaced.");
        }

        var pending = CreatePending(
            MutationType.ManagedReinstall,
            [record.InstallationId],
            [record.CanonicalPath, junctionPath],
            [startingHash, null],
            [PathState.Directory, junctionExisted ? PathState.Junction : PathState.Missing]);
        pending.TargetRevision = plan.Revision;
        pending.TargetPayloadHash = plan.PayloadHash;
        pending.TargetContentIdentity = plan.ContentIdentity;
        pending.TargetFileCount = plan.FileCount;
        pending.TargetProviderEvidence = plan.ProviderEvidence;
        return ReplaceManaged(state, record, pending, plan.Payload, OperationOutcome.Reinstalled, cancellationToken);
    }

    public LifecycleResult Uninstall(ManagementRecord requestedRecord, CancellationToken cancellationToken = default)
    {
        var state = RequireWritableState();
        var record = FindGitHubRecord(state, requestedRecord.InstallationId);
        var startingHash = RecheckManagedPath(record, allowLocalModification: false, requireHealthyExposure: true);
        var junctionPath = record.IntendedClaudeJunctionPath!;
        var pending = CreatePending(
            MutationType.Uninstall,
            [record.InstallationId],
            [record.CanonicalPath, junctionPath],
            [startingHash, null],
            [PathState.Directory, PathState.Junction]);

        state.PendingOperation = pending;
        stateStore.Save(state);
        try
        {
            SnapshotDirectory(record.CanonicalPath, pending);
            SavePhase(state, pending, PendingOperationPhase.SnapshotReady);
            ThrowIfCancellationRequested(pending, cancellationToken);
            SavePhase(state, pending, PendingOperationPhase.MutationStarted);

            Directory.Delete(junctionPath, recursive: false);
            Directory.Delete(record.CanonicalPath, recursive: true);
            if (PathEntryExists(junctionPath) || PathEntryExists(record.CanonicalPath))
            {
                throw new ProviderFailure("Uninstall did not remove all provider-owned content and Skilly-owned Harness Exposures.");
            }

            ThrowIfCancellationRequested(pending, cancellationToken);
            SavePhase(state, pending, PendingOperationPhase.Verified);
            state.Records.Remove(record);
            state.PendingOperation = null;
            state.LastOperationNote = $"uninstalled GitHub Skill '{record.Provenance.SourceSkillPath}'";
            stateStore.Save(state);
            CleanupRecoverySafe(pending);
            return new LifecycleResult(record.CanonicalPath, "Healthy Managed uninstall completed and verified.");
        }
        catch (OperationCanceledException)
        {
            RetainForRestart(state, pending, "Uninstall cancellation requested; restart recovery will reconcile it.");
            throw;
        }
        catch (Exception exception)
        {
            if (!state.Records.Contains(record))
            {
                state.Records.Add(record);
            }
            var restored = RestoreSnapshotIfSafe(pending, record.CanonicalPath, junctionPath, startingHash, null);
            FinishFailedMutation(state, pending, restored, $"GitHub uninstall failed: {exception.Message}");
            throw Failure("Uninstall", restored, exception);
        }
    }

    public LifecycleResult RemoveLocalFolder(string exactPath, CancellationToken cancellationToken = default)
    {
        var state = RequireWritableState();
        exactPath = Path.GetFullPath(exactPath);
        if (state.Records.Any(record => string.Equals(record.CanonicalPath, exactPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProviderFailure("Remove Local Folder is only available for an Unmanaged Installation.");
        }

        if (!Directory.Exists(exactPath) || IsReparsePoint(exactPath))
        {
            throw new ProviderFailure("Remove Local Folder requires the exact path of a real Unmanaged Installation folder.");
        }

        var startingHash = PayloadHasher.HashFolder(exactPath);
        var pending = CreatePending(
            MutationType.RemoveLocalFolder,
            [],
            [exactPath],
            [startingHash],
            [PathState.Directory]);
        state.PendingOperation = pending;
        stateStore.Save(state);
        try
        {
            SnapshotDirectory(exactPath, pending);
            SavePhase(state, pending, PendingOperationPhase.SnapshotReady);
            ThrowIfCancellationRequested(pending, cancellationToken);
            SavePhase(state, pending, PendingOperationPhase.MutationStarted);
            if (!string.Equals(PayloadHasher.HashFolder(exactPath), startingHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProviderFailure("The Unmanaged Installation changed after confirmation; nothing was removed.");
            }

            Directory.Delete(exactPath, recursive: true);
            if (PathEntryExists(exactPath))
            {
                throw new ProviderFailure("The exact Unmanaged Installation path still exists after removal.");
            }

            ThrowIfCancellationRequested(pending, cancellationToken);
            SavePhase(state, pending, PendingOperationPhase.Verified);
            state.PendingOperation = null;
            state.LastOperationNote = $"removed Unmanaged Installation folder '{exactPath}'";
            stateStore.Save(state);
            CleanupRecoverySafe(pending);
            return new LifecycleResult(exactPath, "Remove Local Folder completed and verified.");
        }
        catch (OperationCanceledException)
        {
            RetainForRestart(state, pending, "Remove Local Folder cancellation requested; restart recovery will reconcile it.");
            throw;
        }
        catch (Exception exception)
        {
            var restored = RestoreSnapshotIfSafe(pending, exactPath, null, startingHash, null);
            FinishFailedMutation(state, pending, restored, $"Remove Local Folder failed: {exception.Message}");
            throw Failure("Remove Local Folder", restored, exception);
        }
    }

    public void RequestCancellation()
    {
        var state = stateStore.Load();
        if (state.PendingOperation is null)
        {
            return;
        }

        state.PendingOperation.CancellationRequested = true;
        state.LastOperationNote = "Cancellation requested; pending operation retained for restart recovery.";
        stateStore.Save(state);
    }

    public RecoveryResult RecoverPendingOperation()
    {
        SkillyState state;
        try
        {
            state = stateStore.Load();
        }
        catch (RecoveryRequiredException exception)
        {
            return new RecoveryResult(RecoveryDisposition.RecoveryRequired, exception.Message);
        }

        var pending = state.PendingOperation;
        if (pending is null)
        {
            return new RecoveryResult(RecoveryDisposition.None, "No pending mutation requires recovery.");
        }

        try
        {
            return pending.OperationType switch
            {
                MutationType.ManagedReinstall or MutationType.Update => RecoverReplacement(state, pending),
                MutationType.Uninstall => RecoverUninstall(state, pending),
                MutationType.RemoveLocalFolder => RecoverLocalRemoval(state, pending),
                MutationType.Install => RecoverInstall(state, pending),
                MutationType.Adoption => RecoverAdoption(state, pending),
                _ => RequireManualRecovery("The pending mutation type is not recoverable by this version."),
            };
        }
        catch (Exception exception)
        {
            return RequireManualRecovery($"Pending mutation recovery could not be proven safe: {exception.Message}");
        }
    }

    private LifecycleResult ReplaceManaged(
        SkillyState state,
        ManagementRecord record,
        PendingOperation pending,
        GitHubPayload payload,
        OperationOutcome outcome,
        CancellationToken cancellationToken)
    {
        var oldRevision = record.InstalledRevision;
        var oldHash = record.InstalledPayloadHash;
        var oldFileCount = record.InstalledFileCount;
        var oldEvidence = record.ProviderEvidence;
        var oldResolvedCommit = record.Provenance.ResolvedCommit;
        var oldContentIdentity = record.Provenance.SelectedContentIdentity;
        var oldOutcome = record.LastOperationOutcome;
        var oldCheck = record.LatestCheck;
        var junctionPath = record.IntendedClaudeJunctionPath!;

        state.PendingOperation = pending;
        stateStore.Save(state);
        try
        {
            SnapshotDirectory(record.CanonicalPath, pending);
            var stagePath = Path.Combine(pending.RecoveryDirectory!, "replacement");
            Materialize(stagePath, payload.Files);
            VerifyMaterialized(stagePath, Path.GetFileName(record.CanonicalPath), pending.TargetPayloadHash!, pending.TargetFileCount!.Value);
            SavePhase(state, pending, PendingOperationPhase.SnapshotReady);
            ThrowIfCancellationRequested(pending, cancellationToken);

            var currentHash = PayloadHasher.HashFolder(record.CanonicalPath);
            if (!string.Equals(currentHash, pending.StartingHashes[0], StringComparison.OrdinalIgnoreCase))
            {
                throw new ProviderFailure("The Skill Installation changed after its recovery snapshot was created.");
            }

            SavePhase(state, pending, PendingOperationPhase.MutationStarted);
            if (PathEntryExists(junctionPath))
            {
                Directory.Delete(junctionPath, recursive: false);
            }
            Directory.Delete(record.CanonicalPath, recursive: true);
            Directory.Move(stagePath, record.CanonicalPath);
            Junction.Create(junctionPath, record.CanonicalPath);
            VerifyMaterialized(record.CanonicalPath, Path.GetFileName(record.CanonicalPath), pending.TargetPayloadHash!, pending.TargetFileCount.Value);
            if (!Junction.IsJunctionTo(junctionPath, record.CanonicalPath))
            {
                throw new ProviderFailure("Managed Reinstall produced the wrong Harness Exposure topology.");
            }

            ThrowIfCancellationRequested(pending, cancellationToken);
            SavePhase(state, pending, PendingOperationPhase.Verified);
            record.InstalledRevision = pending.TargetRevision!;
            record.InstalledPayloadHash = pending.TargetPayloadHash!;
            record.InstalledFileCount = pending.TargetFileCount.Value;
            record.ProviderEvidence = pending.TargetProviderEvidence!;
            record.Provenance.ResolvedCommit = pending.TargetRevision!;
            record.Provenance.SelectedContentIdentity = pending.TargetContentIdentity!;
            record.LastOperationOutcome = outcome;
            record.LatestCheck = new CheckSnapshot
            {
                Status = record.Provenance.TrackingRuleKind == TrackingRuleKind.Branch ? UpdateStatus.Current : UpdateStatus.Pinned,
                InstalledRevision = pending.TargetRevision!,
                AvailableRevision = pending.TargetRevision,
                AvailablePayloadHash = pending.TargetPayloadHash,
                AvailableContentIdentity = pending.TargetContentIdentity,
                CheckedAt = DateTimeOffset.Now,
            };
            state.PendingOperation = null;
            state.LastOperationNote = $"Managed Reinstall completed for '{record.Provenance.SourceSkillPath}'";
            stateStore.Save(state);
            CleanupRecoverySafe(pending);
            return new LifecycleResult(record.CanonicalPath, "Managed Reinstall completed from clean verified source content; no files were merged.");
        }
        catch (OperationCanceledException)
        {
            RetainForRestart(state, pending, "Managed Reinstall cancellation requested; restart recovery will reconcile it.");
            throw;
        }
        catch (Exception exception)
        {
            record.InstalledRevision = oldRevision;
            record.InstalledPayloadHash = oldHash;
            record.InstalledFileCount = oldFileCount;
            record.ProviderEvidence = oldEvidence;
            record.Provenance.ResolvedCommit = oldResolvedCommit;
            record.Provenance.SelectedContentIdentity = oldContentIdentity;
            record.LastOperationOutcome = oldOutcome;
            record.LatestCheck = oldCheck;
            var restored = RestoreSnapshotIfSafe(pending, record.CanonicalPath, junctionPath, pending.StartingHashes[0]!, pending.TargetPayloadHash);
            FinishFailedMutation(state, pending, restored, $"Managed Reinstall failed: {exception.Message}");
            throw Failure("Managed Reinstall", restored, exception);
        }
    }

    private RecoveryResult RecoverReplacement(SkillyState state, PendingOperation pending)
    {
        var record = state.Records.SingleOrDefault(candidate => pending.AffectedInstallationIds.Contains(candidate.InstallationId));
        if (record is null || pending.StartingPaths.Count < 2 || pending.StartingHashes.Count == 0)
        {
            return RequireManualRecovery("The replacement journal does not identify its prior Management Record and paths.");
        }

        var canonical = pending.StartingPaths[0];
        var junction = pending.StartingPaths[1];
        if (pending.Phase == PendingOperationPhase.Journaled && MatchesHash(canonical, pending.StartingHashes[0]))
        {
            ClearRecovered(state, pending, "Pending replacement had not mutated content; journal cleared.");
            return new RecoveryResult(RecoveryDisposition.Restored, state.LastOperationNote!);
        }

        if (pending.TargetPayloadHash is not null
            && MatchesHash(canonical, pending.TargetPayloadHash)
            && Junction.IsJunctionTo(junction, canonical)
            && pending.Phase == PendingOperationPhase.Verified)
        {
            record.InstalledRevision = pending.TargetRevision!;
            record.InstalledPayloadHash = pending.TargetPayloadHash;
            record.InstalledFileCount = pending.TargetFileCount!.Value;
            record.ProviderEvidence = pending.TargetProviderEvidence!;
            record.Provenance.ResolvedCommit = pending.TargetRevision!;
            record.Provenance.SelectedContentIdentity = pending.TargetContentIdentity!;
            record.LastOperationOutcome = pending.OperationType == MutationType.ManagedReinstall
                ? OperationOutcome.Reinstalled
                : OperationOutcome.Updated;
            record.LatestCheck = new CheckSnapshot
            {
                Status = record.Provenance.TrackingRuleKind == TrackingRuleKind.Branch ? UpdateStatus.Current : UpdateStatus.Pinned,
                InstalledRevision = pending.TargetRevision!,
                AvailableRevision = pending.TargetRevision,
                AvailablePayloadHash = pending.TargetPayloadHash,
                AvailableContentIdentity = pending.TargetContentIdentity,
                CheckedAt = DateTimeOffset.Now,
            };
            ClearRecovered(state, pending, "Verified pending replacement completion and committed authority.");
            return new RecoveryResult(RecoveryDisposition.Completed, state.LastOperationNote!);
        }

        if (RestoreSnapshotIfSafe(pending, canonical, junction, pending.StartingHashes[0]!, pending.TargetPayloadHash))
        {
            ClearRecovered(state, pending, "Safely restored the prior Skill Installation from its recovery snapshot.");
            return new RecoveryResult(RecoveryDisposition.Restored, state.LastOperationNote!);
        }

        return RequireManualRecovery("The pending replacement does not match either its verified result or a safely restorable state.");
    }

    private RecoveryResult RecoverUninstall(SkillyState state, PendingOperation pending)
    {
        if (pending.StartingPaths.Count < 2)
        {
            return RequireManualRecovery("The uninstall journal is incomplete.");
        }

        if (pending.Phase == PendingOperationPhase.Journaled
            && MatchesHash(pending.StartingPaths[0], pending.StartingHashes[0])
            && Junction.IsJunctionTo(pending.StartingPaths[1], pending.StartingPaths[0]))
        {
            ClearRecovered(state, pending, "Pending uninstall had not mutated content; journal cleared.");
            return new RecoveryResult(RecoveryDisposition.Restored, state.LastOperationNote!);
        }

        if (!PathEntryExists(pending.StartingPaths[0]) && !PathEntryExists(pending.StartingPaths[1]))
        {
            state.Records.RemoveAll(record => pending.AffectedInstallationIds.Contains(record.InstallationId));
            ClearRecovered(state, pending, "Verified pending uninstall completion and removed authority.");
            return new RecoveryResult(RecoveryDisposition.Completed, state.LastOperationNote!);
        }

        if (RestoreSnapshotIfSafe(pending, pending.StartingPaths[0], pending.StartingPaths[1], pending.StartingHashes[0]!, null))
        {
            ClearRecovered(state, pending, "Safely restored the pending uninstall from its recovery snapshot.");
            return new RecoveryResult(RecoveryDisposition.Restored, state.LastOperationNote!);
        }

        return RequireManualRecovery("The pending uninstall could not be completed or safely restored.");
    }

    private RecoveryResult RecoverLocalRemoval(SkillyState state, PendingOperation pending)
    {
        if (pending.StartingPaths.Count != 1 || pending.StartingHashes.Count != 1)
        {
            return RequireManualRecovery("The Remove Local Folder journal is incomplete.");
        }

        var path = pending.StartingPaths[0];
        if (!PathEntryExists(path))
        {
            ClearRecovered(state, pending, "Verified pending Remove Local Folder completion.");
            return new RecoveryResult(RecoveryDisposition.Completed, state.LastOperationNote!);
        }

        if (MatchesHash(path, pending.StartingHashes[0]))
        {
            ClearRecovered(state, pending, "The Unmanaged Installation still matches its starting snapshot; no recovery mutation was needed.");
            return new RecoveryResult(RecoveryDisposition.Restored, state.LastOperationNote!);
        }

        return RequireManualRecovery("The Unmanaged Installation changed while Remove Local Folder was pending.");
    }

    private RecoveryResult RecoverInstall(SkillyState state, PendingOperation pending)
    {
        var unchanged = pending.StartingPaths.Select((path, index) =>
            pending.StartingPathStates.ElementAtOrDefault(index) == PathState.Missing && !PathEntryExists(path)).All(static value => value);
        if (unchanged)
        {
            ClearRecovered(state, pending, "Pending install had not created destination content; journal cleared.");
            return new RecoveryResult(RecoveryDisposition.Restored, state.LastOperationNote!);
        }

        return RequireManualRecovery("An interrupted install created content without committed management authority.");
    }

    private RecoveryResult RecoverAdoption(SkillyState state, PendingOperation pending)
    {
        if (pending.StartingPaths.Count != 2 || pending.StartingHashes.Count == 0
            || !MatchesHash(pending.StartingPaths[0], pending.StartingHashes[0]))
        {
            return RequireManualRecovery("The pending Adoption no longer matches its verified canonical payload.");
        }

        var junction = pending.StartingPaths[1];
        if (pending.StartingPathStates.ElementAtOrDefault(1) == PathState.Missing && PathEntryExists(junction))
        {
            if (!RemoveCreatedAdoptionJunction(junction, pending.StartingPaths[0]))
            {
                return RequireManualRecovery("The Claude entry created by pending Adoption cannot be safely removed.");
            }
        }
        else if (pending.StartingPathStates.ElementAtOrDefault(1) == PathState.Junction
                 && !Junction.IsJunctionTo(junction, pending.StartingPaths[0]))
        {
            return RequireManualRecovery("The pre-existing Claude junction changed while Adoption was pending.");
        }

        state.Records.RemoveAll(record => pending.AffectedInstallationIds.Contains(record.InstallationId));
        ClearRecovered(state, pending, "Pending Adoption was safely rolled back; the installation remains Unmanaged.");
        return new RecoveryResult(RecoveryDisposition.Restored, state.LastOperationNote!);
    }

    private RecoveryResult RequireManualRecovery(string diagnostic)
    {
        stateStore.EnterRecoveryRequired($"Recovery Required: {diagnostic}");
        return new RecoveryResult(RecoveryDisposition.RecoveryRequired, stateStore.RecoveryDiagnostic!);
    }

    private SkillyState RequireWritableState()
    {
        if (stateStore.RecoveryRequired)
        {
            throw new ProviderFailure(stateStore.RecoveryDiagnostic ?? "Recovery Required; Skilly is read-only.");
        }

        var state = stateStore.Load();
        if (state.PendingOperation is not null)
        {
            throw new ProviderFailure("Another mutation is pending. Skilly remains read-only until recovery reconciles it.");
        }

        return state;
    }

    private static ManagementRecord FindGitHubRecord(SkillyState state, string installationId)
    {
        var record = state.Records.SingleOrDefault(candidate => candidate.InstallationId == installationId);
        if (record is null || !string.Equals(record.Provenance.SourceProvider, "github", StringComparison.Ordinal))
        {
            throw new ProviderFailure("The selected installation has no current GitHub Management Record.");
        }

        return record;
    }

    private static void ValidateAdoptionEvidence(SkillyState state, AdoptionEvidence evidence)
    {
        var record = evidence.ProposedRecord;
        var provenance = record.Provenance;
        var normalizedSource = $"{provenance.Host}/{provenance.Owner}/{provenance.Repository}".ToLowerInvariant();
        var repositoryPath = GitHubChecker.RepositoryPath(provenance);
        var providerEvidence = $"gh api contents/{(repositoryPath.Length == 0 ? "." : repositoryPath)}@{record.InstalledRevision}";
        if (!string.Equals(record.Provenance.SourceProvider, "github", StringComparison.Ordinal)
            || !string.Equals(provenance.NormalizedSource, normalizedSource, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(record.Provenance.SourceSkillPath)
            || string.IsNullOrWhiteSpace(record.Provenance.ResolvedCommit)
            || string.IsNullOrWhiteSpace(record.Provenance.SelectedContentIdentity)
            || !string.Equals(record.Provenance.ResolvedCommit, record.InstalledRevision, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(record.Provenance.SelectedContentIdentity, evidence.ExpectedContentIdentity, StringComparison.Ordinal)
            || !string.Equals(record.InstalledPayloadHash, evidence.ExpectedPayloadHash, StringComparison.OrdinalIgnoreCase)
            || record.InstalledFileCount != evidence.ExpectedFileCount
            || !string.Equals(record.ProviderEvidence, providerEvidence, StringComparison.Ordinal))
        {
            throw new ProviderFailure("Adoption evidence does not contain exact normalized source, path, revision, content, and provider identity.");
        }
        ValidateCommonAdoptionEvidence(state, evidence);
    }

    private static void ValidateCommonAdoptionEvidence(SkillyState state, AdoptionEvidence evidence)
    {
        var record = evidence.ProposedRecord;
        if (state.Records.Any(candidate =>
                candidate.InstallationId == record.InstallationId
                || string.Equals(candidate.CanonicalPath, record.CanonicalPath, StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProviderFailure("The selected Skill Installation already has management authority.");
        }
        if (!Directory.Exists(record.CanonicalPath) || IsReparsePoint(record.CanonicalPath)
            || SkillMdReader.Read(record.CanonicalPath, Path.GetFileName(record.CanonicalPath)).Status != MetadataReadStatus.Valid
            || record.IntendedClaudeJunctionPath is null)
        {
            throw new ProviderFailure("Adoption requires a valid real canonical Skill folder and intended Claude Harness Exposure.");
        }
    }

    private static bool RemoveCreatedAdoptionJunction(string junctionPath, string canonicalPath)
    {
        try
        {
            if (!PathEntryExists(junctionPath))
            {
                return true;
            }
            if (!Junction.IsJunctionTo(junctionPath, canonicalPath))
            {
                return false;
            }
            Directory.Delete(junctionPath, recursive: false);
            return !PathEntryExists(junctionPath);
        }
        catch
        {
            return false;
        }
    }

    private static string RecheckManagedPath(ManagementRecord record, bool allowLocalModification, bool requireHealthyExposure)
    {
        if (!Directory.Exists(record.CanonicalPath) || IsReparsePoint(record.CanonicalPath))
        {
            throw new ProviderFailure("The canonical destination is missing or has conflicting topology; mutation is blocked as a Collision.");
        }

        var hash = PayloadHasher.HashFolder(record.CanonicalPath);
        if (!allowLocalModification && !string.Equals(hash, record.InstalledPayloadHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderFailure("The Skill Installation is Locally Modified; direct lifecycle mutation is blocked.");
        }

        if (record.IntendedClaudeJunctionPath is null)
        {
            throw new ProviderFailure("The Management Record does not identify its intended Claude Harness Exposure.");
        }

        var exposureExists = PathEntryExists(record.IntendedClaudeJunctionPath);
        if ((requireHealthyExposure && !Junction.IsJunctionTo(record.IntendedClaudeJunctionPath, record.CanonicalPath))
            || (exposureExists && !Junction.IsJunctionTo(record.IntendedClaudeJunctionPath, record.CanonicalPath)))
        {
            throw new ProviderFailure("The intended Claude destination has conflicting topology; mutation is blocked as a Collision.");
        }

        return hash;
    }

    private PendingOperation CreatePending(
        MutationType type,
        List<string> installationIds,
        List<string> paths,
        List<string?> hashes,
        List<PathState> pathStates)
    {
        var operationId = Guid.NewGuid().ToString("N");
        return new PendingOperation
        {
            OperationId = operationId,
            OperationType = type,
            AffectedInstallationIds = installationIds,
            StartingPaths = paths,
            StartingHashes = hashes,
            StartingPathStates = pathStates,
            RecoveryDirectory = Path.Combine(Path.GetDirectoryName(stateStore.FilePath)!, "recovery", operationId),
            Phase = PendingOperationPhase.Journaled,
            StartedAt = DateTimeOffset.Now,
        };
    }

    private void SnapshotDirectory(string source, PendingOperation pending)
    {
        var snapshot = SnapshotPath(pending);
        Directory.CreateDirectory(pending.RecoveryDirectory!);
        CopyDirectory(source, snapshot);
        if (!MatchesHash(snapshot, pending.StartingHashes[0]))
        {
            throw new ProviderFailure("The temporary recovery snapshot does not match the starting content.");
        }
    }

    private void SavePhase(SkillyState state, PendingOperation pending, PendingOperationPhase phase)
    {
        pending.Phase = phase;
        state.PendingOperation = pending;
        stateStore.Save(state);
    }

    private void RetainForRestart(SkillyState state, PendingOperation pending, string note)
    {
        pending.CancellationRequested = true;
        state.PendingOperation = pending;
        state.LastOperationNote = note;
        try
        {
            stateStore.Save(state);
        }
        catch (Exception exception)
        {
            log.Error("Could not persist the cancellation note; the previously persisted pending journal remains authoritative.", exception);
        }
    }

    private void FinishFailedMutation(SkillyState state, PendingOperation pending, bool restored, string note)
    {
        state.PendingOperation = restored ? null : pending;
        state.LastOperationNote = restored ? note + " Prior state was safely restored." : note + " Recovery Required.";
        if (!restored)
        {
            stateStore.EnterRecoveryRequired(state.LastOperationNote);
            return;
        }

        try
        {
            stateStore.Save(state);
            CleanupRecoverySafe(pending);
        }
        catch (Exception saveException)
        {
            log.Error("The restored state could not be committed; the durable pending journal is retained for restart recovery.", saveException);
        }
    }

    private void ClearRecovered(SkillyState state, PendingOperation pending, string note)
    {
        state.PendingOperation = null;
        state.LastOperationNote = note;
        stateStore.Save(state);
        CleanupRecoverySafe(pending);
    }

    private bool RestoreSnapshotIfSafe(
        PendingOperation pending,
        string destination,
        string? junctionPath,
        string startingHash,
        string? targetHash)
    {
        try
        {
            var snapshot = SnapshotPathForRead(pending);
            if (!Directory.Exists(snapshot) || !MatchesHash(snapshot, startingHash))
            {
                return false;
            }

            if (PathEntryExists(destination)
                && !MatchesHash(destination, startingHash)
                && (targetHash is null || !MatchesHash(destination, targetHash)))
            {
                return false;
            }

            if (junctionPath is not null
                && PathEntryExists(junctionPath)
                && !Junction.IsJunctionTo(junctionPath, destination)
                && !IsEmptyRealDirectory(junctionPath))
            {
                return false;
            }

            if (junctionPath is not null && PathEntryExists(junctionPath))
            {
                Directory.Delete(junctionPath, recursive: false);
            }
            if (Directory.Exists(destination))
            {
                Directory.Delete(destination, recursive: true);
            }
            CopyDirectory(snapshot, destination);
            if (junctionPath is not null
                && pending.StartingPathStates.ElementAtOrDefault(1) == PathState.Junction)
            {
                Junction.Create(junctionPath, destination);
            }

            return MatchesHash(destination, startingHash)
                   && (junctionPath is null
                       || pending.StartingPathStates.ElementAtOrDefault(1) == PathState.Missing
                       || Junction.IsJunctionTo(junctionPath, destination));
        }
        catch (Exception exception)
        {
            log.Error("Recovery snapshot restoration failed.", exception);
            return false;
        }
    }

    private static void ThrowIfCancellationRequested(PendingOperation pending, CancellationToken cancellationToken)
    {
        if (pending.CancellationRequested || cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Mutation cancellation was requested.", cancellationToken);
        }
    }

    private static ProviderFailure Failure(string operation, bool restored, Exception exception)
        => new(restored
            ? $"{operation} failed; prior state was safely restored. {exception.Message}"
            : $"{operation} failed and restoration could not be proven safe. Recovery Required. {exception.Message}");

    private static string SnapshotPath(PendingOperation pending) => Path.Combine(pending.RecoveryDirectory!, "snapshot");

    private static string SnapshotPathForRead(PendingOperation pending)
    {
        var snapshot = SnapshotPath(pending);
        if (Directory.Exists(snapshot))
        {
            return snapshot;
        }

        return pending.OperationType == MutationType.Update && pending.RecoveryDirectory is not null
            ? pending.RecoveryDirectory
            : snapshot;
    }

    private static void Materialize(string destination, IReadOnlyList<(string RelativePath, byte[] Content)> files)
    {
        Directory.CreateDirectory(destination);
        var root = Path.GetFullPath(destination).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        foreach (var file in files)
        {
            var target = Path.GetFullPath(Path.Combine(destination, file.RelativePath.Replace('/', Path.DirectorySeparatorChar)));
            if (!target.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            {
                throw new ProviderFailure($"Source path '{file.RelativePath}' escapes its Source Skill folder.");
            }
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.WriteAllBytes(target, file.Content);
        }
    }

    private static void VerifyPayloadFiles(GitHubPayload payload, string folderName)
    {
        var skillMd = payload.Files.SingleOrDefault(file => string.Equals(file.RelativePath.Replace('\\', '/'), "SKILL.md", StringComparison.Ordinal));
        if (skillMd == default || skillMd.Content.Length == 0)
        {
            throw new ProviderFailure($"The verified payload for '{folderName}' does not contain SKILL.md.");
        }
    }

    private static void VerifyMaterialized(string path, string folderName, string expectedHash, int expectedFileCount)
    {
        if (!Directory.Exists(path)
            || !string.Equals(PayloadHasher.HashFolder(path), expectedHash, StringComparison.OrdinalIgnoreCase)
            || Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count() != expectedFileCount)
        {
            throw new ProviderFailure("The materialized payload does not match verified clean source content.");
        }

        var metadata = SkillMdReader.Read(path, folderName);
        if (metadata.Status != MetadataReadStatus.Valid)
        {
            throw new ProviderFailure($"The materialized SKILL.md did not validate: {metadata.Error}");
        }
    }

    private static bool MatchesHash(string path, string? hash)
        => hash is not null
           && Directory.Exists(path)
           && !IsReparsePoint(path)
           && string.Equals(PayloadHasher.HashFolder(path), hash, StringComparison.OrdinalIgnoreCase);

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            if (IsReparsePoint(directory))
            {
                throw new ProviderFailure($"Recovery snapshot refused nested reparse point '{directory}'.");
            }
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            if (File.GetAttributes(file).HasFlag(FileAttributes.ReparsePoint))
            {
                throw new ProviderFailure($"Recovery snapshot refused nested reparse point '{file}'.");
            }
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: false);
        }
    }

    private static void CleanupRecovery(PendingOperation pending)
    {
        foreach (var temporaryPath in pending.TemporaryPaths)
        {
            if (Directory.Exists(temporaryPath))
            {
                Directory.Delete(temporaryPath, recursive: true);
            }
            else if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }

        if (pending.RecoveryDirectory is not null && Directory.Exists(pending.RecoveryDirectory))
        {
            Directory.Delete(pending.RecoveryDirectory, recursive: true);
            var parent = Path.GetDirectoryName(pending.RecoveryDirectory);
            if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
            {
                Directory.Delete(parent);
            }
        }
    }

    private void CleanupRecoverySafe(PendingOperation pending)
    {
        try
        {
            CleanupRecovery(pending);
        }
        catch (Exception exception)
        {
            log.Error($"Verified authority was committed, but temporary recovery data for '{pending.OperationId}' could not be removed.", exception);
        }
    }

    private static bool IsReparsePoint(string path)
        => File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);

    private static bool IsEmptyRealDirectory(string path)
        => Directory.Exists(path)
           && !IsReparsePoint(path)
           && !Directory.EnumerateFileSystemEntries(path).Any();

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
