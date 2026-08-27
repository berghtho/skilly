using System.IO;
using Skilly.Infrastructure;
using Skilly.Providers.GitHub;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.Providers.SkillsCli;

public sealed class SkillsCliProvider(
    SkillsCliClient client,
    StateStore stateStore,
    RollingLog log,
    string home,
    string? providerLockPath = null)
{
    private readonly SkillsCliLock _lock = new(providerLockPath ?? ResolveLockPath(home));

    public ProviderReadiness GetReadiness() => client.GetReadiness();

    public bool RecoveryRequired => stateStore.RecoveryRequired || TryLoadPending() is not null;

    public string RecoveryDiagnostic => stateStore.RecoveryDiagnostic
                                        ?? "A pending skills provider mutation requires restart recovery.";

    public bool OwnsPendingOperation(PendingOperation? pending)
        => string.Equals(pending?.TargetProviderEvidence, SkillsCliClient.Package, StringComparison.Ordinal);

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
        if (!OwnsPendingOperation(pending))
        {
            return new RecoveryResult(RecoveryDisposition.None, "No pending skills provider mutation requires recovery.");
        }
        var paths = pending!.StartingPaths.Take(pending.StartingPaths.Count - 1).Chunk(2)
            .Select(pair => new MutationPaths(pair[0], pair[1])).ToList();
        var snapshot = ProviderSnapshot.Open(
            pending.RecoveryDirectory!,
            _lock.Path,
            pending.StartingPathStates.LastOrDefault() == PathState.File,
            paths,
            pending.StartingPathStates,
            log);
        if (!snapshot.Restore())
        {
            var diagnostic = "Recovery Required: the pending skills provider mutation could not be restored from verified snapshots.";
            stateStore.EnterRecoveryRequired(diagnostic);
            return new RecoveryResult(RecoveryDisposition.RecoveryRequired, diagnostic);
        }
        state.PendingOperation = null;
        state.LastOperationNote = "Safely restored the pending skills provider mutation without retrying it.";
        stateStore.Save(state);
        snapshot.Cleanup();
        return new RecoveryResult(RecoveryDisposition.Restored, state.LastOperationNote);
    }

    public void RequestMutationCancellation()
    {
        var state = stateStore.Load();
        if (!OwnsPendingOperation(state.PendingOperation)) return;
        state.PendingOperation!.CancellationRequested = true;
        state.LastOperationNote = "Cancellation requested; pending skills provider recovery data retained.";
        stateStore.Save(state);
    }

    public ProviderResult<SkillsCliInspection> Inspect(string source)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(source))
            {
                throw new ProviderFailure("A skills provider source reference is required.");
            }

            ValidateCredentialFreeReference(source);

            var before = ReadOnlyFingerprint();
            var result = client.Inspect(source.Trim());
            var inspection = client.ParseInspection(source.Trim(), result);
            if (!string.Equals(before, ReadOnlyFingerprint(), StringComparison.Ordinal))
            {
                throw new ProviderFailure("Read-only provider inspection changed global Skill content or provider lock state.");
            }
            return ProviderResult<SkillsCliInspection>.Success(
                inspection,
                $"Read-only {SkillsCliClient.Package} inspection found {inspection.Skills.Count} Source Skill(s); nothing changed.");
        }
        catch (Exception exception)
        {
            return ProviderResult<SkillsCliInspection>.Failure(exception.Message);
        }
    }

    public ProviderResult<SkillsCliInstallResult> Install(
        SkillsCliInspection inspection,
        IReadOnlyList<SkillsCliSourceSkill> selected,
        CancellationToken cancellationToken = default)
        => Wrap(() => InstallCore(inspection, selected, cancellationToken), "Installed through the pinned skills provider and verified every postcondition.");

    public ProviderResult<CheckResult> Check(ManagementRecord record)
    {
        try
        {
            var current = RequireManagedRecord(record.InstallationId, requireHealthy: true);
            VerifyCurrentProviderEvidence(current);
            var installedHash = PayloadHasher.HashFolder(current.CanonicalPath);
            var temporaryRoot = Path.Combine(Path.GetDirectoryName(stateStore.FilePath)!, "provider-check-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            try
            {
                var environment = IsolatedEnvironment(temporaryRoot);
                var selectionName = current.Provenance.ProviderSkillName ?? Path.GetFileName(current.CanonicalPath);
                var process = client.Install(current.Provenance.OriginalReference, selectionName, environment);
                SkillsCliClient.RequireExit(process, "Read-only isolated provider acquisition");

                var availableCanonical = Path.Combine(temporaryRoot, ".agents", "skills", Path.GetFileName(current.CanonicalPath));
                var availableClaude = Path.Combine(temporaryRoot, ".claude", "skills", Path.GetFileName(current.CanonicalPath));
                var temporaryLock = new SkillsCliLock(Path.Combine(temporaryRoot, "state", "skills", ".skill-lock.json"));
                var evidence = FindLockEntry(temporaryLock.Read(), selectionName, Path.GetFileName(current.CanonicalPath));
                VerifySourceEvidence(current.Provenance.OriginalReference, evidence);
                VerifyCanonicalAndExposure(availableCanonical, availableClaude);
                VerifyListedSkill(client.ListGlobal(environment), availableCanonical);
                var availableHash = PayloadHasher.HashFolder(availableCanonical);
                var pinned = current.Provenance.TrackingRuleKind is TrackingRuleKind.Tag or TrackingRuleKind.Commit;
                var status = pinned
                    ? UpdateStatus.Pinned
                    : string.Equals(availableHash, installedHash, StringComparison.OrdinalIgnoreCase)
                        ? UpdateStatus.Current
                        : UpdateStatus.UpdateAvailable;
                return ProviderResult<CheckResult>.Success(
                    new CheckResult(
                        status,
                        current.InstalledRevision,
                        null,
                        evidence.SkillFolderHash,
                        evidence.UpdatedAt ?? evidence.InstalledAt,
                        availableHash,
                        DateTimeOffset.Now,
                        pinned && !string.Equals(evidence.SkillFolderHash, current.InstalledRevision, StringComparison.Ordinal)
                            ? "The pinned provider ref resolves to different content; Skilly will not update it automatically."
                            : null,
                        AvailableContentIdentity: evidence.SkillFolderHash),
                    $"Read-only {SkillsCliClient.Package} comparison completed in an isolated temporary home; installed content was not changed.");
            }
            finally
            {
                DeleteDirectorySafe(temporaryRoot);
            }
        }
        catch (Exception exception)
        {
            return ProviderResult<CheckResult>.Failure(exception.Message);
        }
    }

    public ProviderResult<SkillsCliUpdateResult> Update(ManagementRecord record, CancellationToken cancellationToken = default)
        => Wrap(() => UpdateCore(record, cancellationToken), "Updated through the pinned skills provider and reconciled provider and Skilly authority.");

    public ProviderResult<LifecycleResult> Uninstall(ManagementRecord record, CancellationToken cancellationToken = default)
        => Wrap(() => UninstallCore(record, cancellationToken), "Uninstalled through the pinned skills provider and verified absence before authority removal.");

    private SkillsCliInstallResult InstallCore(
        SkillsCliInspection inspection,
        IReadOnlyList<SkillsCliSourceSkill> selected,
        CancellationToken cancellationToken)
    {
        if (selected.Count == 0)
        {
            throw new ProviderFailure("No Source Skills are selected; installation is unavailable.");
        }
        if (selected.Any(static skill => !skill.MetadataValid))
        {
            throw new ProviderFailure("Every selected Source Skill must have a valid provider identity.");
        }
        var duplicateFolders = selected.GroupBy(static skill => skill.FolderName, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(static group => group.Count() > 1);
        if (duplicateFolders is not null)
        {
            throw new ProviderFailure($"Multiple Source Skills resolve to canonical folder '{duplicateFolders.Key}'.");
        }

        var state = RequireWritableState();
        var canonicalRoot = HarnessRoot.Create(RootKind.CanonicalAgents, home).FullPath;
        var claudeRoot = HarnessRoot.Create(RootKind.ClaudeSkills, home).FullPath;
        var paths = selected.Select(skill => new MutationPaths(
            Path.Combine(canonicalRoot, skill.FolderName),
            Path.Combine(claudeRoot, skill.FolderName))).ToList();
        var existingLock = _lock.Read();
        foreach (var path in paths)
        {
            if (PathEntryExists(path.Canonical) || PathEntryExists(path.Claude))
            {
                throw new ProviderFailure($"'{path.Canonical}' or its Claude destination already exists. Collision blocks provider installation.");
            }
            if (existingLock.Keys.Any(name => string.Equals(SkillsCliClient.SanitizeName(name), Path.GetFileName(path.Canonical), StringComparison.OrdinalIgnoreCase)))
            {
                throw new ProviderFailure($"The provider lock already contains '{Path.GetFileName(path.Canonical)}' without matching canonical content.");
            }
        }

        var pending = CreatePending(MutationType.Install, [], paths, paths.Select(static _ => (string?)null).ToList());
        pending.TargetProviderEvidence = SkillsCliClient.Package;
        state.PendingOperation = pending;
        stateStore.Save(state);
        var snapshot = ProviderSnapshot.Create(pending.RecoveryDirectory!, _lock.Path, paths, log);
        try
        {
            SavePhase(state, pending, PendingOperationPhase.SnapshotReady);
            foreach (var skill in selected)
            {
                cancellationToken.ThrowIfCancellationRequested();
                SavePhase(state, pending, PendingOperationPhase.MutationStarted);
                var process = client.Install(inspection.OriginalReference, skill.Name);
                SkillsCliClient.RequireExit(process, $"Install of '{skill.Name}'");
                cancellationToken.ThrowIfCancellationRequested();
            }

            var lockEntries = _lock.Read();
            var listed = client.ListGlobal();
            var records = new List<ManagementRecord>();
            for (var index = 0; index < selected.Count; index++)
            {
                var skill = selected[index];
                var path = paths[index];
                VerifyCanonicalAndExposure(path.Canonical, path.Claude);
                VerifyListedSkill(listed, path.Canonical);
                var evidence = FindLockEntry(lockEntries, skill.Name, skill.FolderName);
                records.Add(CreateRecord(inspection, skill, path, evidence));
            }

            pending.AffectedInstallationIds = records.Select(static record => record.InstallationId).ToList();
            pending.TargetPayloadHash = string.Join(';', records.Select(static record => record.InstalledPayloadHash));
            SavePhase(state, pending, PendingOperationPhase.Verified);
            state.Records.AddRange(records);
            state.PendingOperation = null;
            state.LastOperationNote = $"installed {records.Count} Skill(s) through {SkillsCliClient.Package}";
            stateStore.Save(state);
            VerifyPersisted(records.Select(static record => record.InstallationId));
            snapshot.Cleanup();
            return new SkillsCliInstallResult(records.Select(record => new SkillsCliInstalledSkill(
                record.Provenance.SourceSkillPath,
                record.CanonicalPath)).ToList());
        }
        catch (OperationCanceledException)
        {
            RetainCancellation(state, pending);
            throw;
        }
        catch (Exception exception)
        {
            FailAndRestore(state, pending, snapshot, exception, "Install", []);
            throw;
        }
    }

    private SkillsCliUpdateResult UpdateCore(ManagementRecord requested, CancellationToken cancellationToken)
    {
        var state = RequireWritableState();
        var record = FindRecord(state, requested.InstallationId);
        var startingRecord = CloneRecord(record);
        RequireFreshUpdate(record);
        VerifyManagedTopology(record);
        VerifyCurrentProviderEvidence(record);
        var check = record.LatestCheck!;
        var paths = new List<MutationPaths> { new(record.CanonicalPath, record.IntendedClaudeJunctionPath!) };
        var pending = CreatePending(MutationType.Update, [record.InstallationId], paths, [record.InstalledPayloadHash]);
        pending.TargetPayloadHash = check.AvailablePayloadHash;
        pending.TargetRevision = check.AvailableRevision;
        pending.TargetContentIdentity = check.AvailableContentIdentity;
        pending.TargetProviderEvidence = SkillsCliClient.Package;
        state.PendingOperation = pending;
        stateStore.Save(state);
        var snapshot = ProviderSnapshot.Create(pending.RecoveryDirectory!, _lock.Path, paths, log);
        try
        {
            SavePhase(state, pending, PendingOperationPhase.SnapshotReady);
            cancellationToken.ThrowIfCancellationRequested();
            VerifyManagedTopology(record);
            SavePhase(state, pending, PendingOperationPhase.MutationStarted);
            var name = record.Provenance.ProviderSkillName ?? Path.GetFileName(record.CanonicalPath);
            var process = client.Update(name);
            SkillsCliClient.RequireExit(process, $"Update of '{name}'");
            cancellationToken.ThrowIfCancellationRequested();

            VerifyCanonicalAndExposure(record.CanonicalPath, record.IntendedClaudeJunctionPath!);
            VerifyListedSkill(client.ListGlobal(), record.CanonicalPath);
            var evidence = FindLockEntry(_lock.Read(), name, Path.GetFileName(record.CanonicalPath));
            VerifySourceEvidence(record.Provenance.OriginalReference, evidence);
            var actualHash = PayloadHasher.HashFolder(record.CanonicalPath);
            if (!string.Equals(actualHash, check.AvailablePayloadHash, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(evidence.SkillFolderHash, check.AvailableRevision, StringComparison.Ordinal))
            {
                throw new ProviderFailure("Provider update did not produce the exact payload and lock evidence verified by Check.");
            }

            SavePhase(state, pending, PendingOperationPhase.Verified);
            ApplyEvidence(record, evidence, actualHash, OperationOutcome.Updated);
            record.LatestCheck = new CheckSnapshot
            {
                Status = UpdateStatus.Current,
                InstalledRevision = evidence.SkillFolderHash,
                AvailableRevision = evidence.SkillFolderHash,
                AvailablePayloadHash = actualHash,
                AvailableContentIdentity = evidence.SkillFolderHash,
                CheckedAt = DateTimeOffset.Now,
            };
            state.PendingOperation = null;
            state.LastOperationNote = $"updated skills provider Skill '{record.Provenance.SourceSkillPath}'";
            stateStore.Save(state);
            VerifyPersisted([record.InstallationId]);
            snapshot.Cleanup();
            return new SkillsCliUpdateResult(record.InstallationId, record.InstalledRevision);
        }
        catch (OperationCanceledException)
        {
            RetainCancellation(state, pending);
            throw;
        }
        catch (Exception exception)
        {
            FailAndRestore(state, pending, snapshot, exception, "Update", [startingRecord]);
            throw;
        }
    }

    private LifecycleResult UninstallCore(ManagementRecord requested, CancellationToken cancellationToken)
    {
        var state = RequireWritableState();
        var record = FindRecord(state, requested.InstallationId);
        var startingRecord = CloneRecord(record);
        VerifyManagedTopology(record);
        VerifyCurrentProviderEvidence(record);
        var paths = new List<MutationPaths> { new(record.CanonicalPath, record.IntendedClaudeJunctionPath!) };
        var pending = CreatePending(MutationType.Uninstall, [record.InstallationId], paths, [record.InstalledPayloadHash]);
        pending.TargetProviderEvidence = SkillsCliClient.Package;
        state.PendingOperation = pending;
        stateStore.Save(state);
        var snapshot = ProviderSnapshot.Create(pending.RecoveryDirectory!, _lock.Path, paths, log);
        try
        {
            SavePhase(state, pending, PendingOperationPhase.SnapshotReady);
            cancellationToken.ThrowIfCancellationRequested();
            VerifyManagedTopology(record);
            SavePhase(state, pending, PendingOperationPhase.MutationStarted);
            var name = record.Provenance.ProviderSkillName ?? Path.GetFileName(record.CanonicalPath);
            var process = client.Uninstall(name);
            SkillsCliClient.RequireExit(process, $"Uninstall of '{name}'");
            cancellationToken.ThrowIfCancellationRequested();

            if (PathEntryExists(record.CanonicalPath) || PathEntryExists(record.IntendedClaudeJunctionPath!))
            {
                throw new ProviderFailure("Provider uninstall reported success but canonical content or the Claude Harness Exposure remains.");
            }
            if (_lock.Read().Keys.Any(key => string.Equals(SkillsCliClient.SanitizeName(key), Path.GetFileName(record.CanonicalPath), StringComparison.OrdinalIgnoreCase))
                || client.ListGlobal().Any(skill => PathsEqual(skill.Path, record.CanonicalPath)))
            {
                throw new ProviderFailure("Provider uninstall reported success but provider lock or inventory evidence remains.");
            }

            SavePhase(state, pending, PendingOperationPhase.Verified);
            state.Records.Remove(record);
            state.PendingOperation = null;
            state.LastOperationNote = $"uninstalled skills provider Skill '{record.Provenance.SourceSkillPath}'";
            stateStore.Save(state);
            if (stateStore.Load().Records.Any(candidate => candidate.InstallationId == record.InstallationId))
            {
                throw new ProviderFailure("Durable state retained authority after verified provider uninstall.");
            }
            snapshot.Cleanup();
            return new LifecycleResult(record.CanonicalPath, "Healthy Managed provider uninstall completed and reconciled.");
        }
        catch (OperationCanceledException)
        {
            RetainCancellation(state, pending);
            throw;
        }
        catch (Exception exception)
        {
            FailAndRestore(state, pending, snapshot, exception, "Uninstall", [startingRecord]);
            throw;
        }
    }

    private ManagementRecord CreateRecord(
        SkillsCliInspection inspection,
        SkillsCliSourceSkill skill,
        MutationPaths paths,
        SkillsCliLockEntry evidence)
    {
        var hash = PayloadHasher.HashFolder(paths.Canonical);
        VerifySourceEvidence(inspection.OriginalReference, evidence);
        var metadata = SkillMdReader.Read(paths.Canonical, skill.FolderName);
        if (metadata.Status != MetadataReadStatus.Valid)
        {
            throw new ProviderFailure($"Installed SKILL.md is invalid: {metadata.Error}");
        }
        var record = new ManagementRecord
        {
            InstallationId = Guid.NewGuid().ToString("N"),
            CanonicalPath = Path.GetFullPath(paths.Canonical),
            Provenance = new ProvenanceInfo
            {
                SourceProvider = "skills",
                OriginalReference = inspection.OriginalReference,
                NormalizedSource = evidence.NormalizedSource,
                Host = Uri.TryCreate(evidence.SourceUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty,
                Owner = string.Empty,
                Repository = evidence.Source,
                SourceSkillPath = evidence.SourceSkillPath,
                TrackingRule = evidence.TrackingRule,
                TrackingRuleKind = evidence.TrackingRuleKind,
                ResolvedCommit = evidence.SkillFolderHash,
                SelectedContentIdentity = evidence.SkillFolderHash,
                ProviderVersion = SkillsCliClient.Version,
                ProviderSkillName = skill.Name,
            },
            IntendedClaudeJunctionPath = Path.GetFullPath(paths.Claude),
            InstalledRevision = evidence.SkillFolderHash,
            InstalledPayloadHash = hash,
            InstalledFileCount = Directory.EnumerateFiles(paths.Canonical, "*", SearchOption.AllDirectories).Count(),
            ProviderEvidence = evidence.Evidence,
            LastOperationOutcome = OperationOutcome.Installed,
        };
        return record;
    }

    private void ApplyEvidence(ManagementRecord record, SkillsCliLockEntry evidence, string payloadHash, OperationOutcome outcome)
    {
        record.InstalledRevision = evidence.SkillFolderHash;
        record.InstalledPayloadHash = payloadHash;
        record.InstalledFileCount = Directory.EnumerateFiles(record.CanonicalPath, "*", SearchOption.AllDirectories).Count();
        record.ProviderEvidence = evidence.Evidence;
        record.Provenance.NormalizedSource = evidence.NormalizedSource;
        record.Provenance.SourceSkillPath = evidence.SourceSkillPath;
        record.Provenance.TrackingRule = evidence.TrackingRule;
        record.Provenance.TrackingRuleKind = evidence.TrackingRuleKind;
        record.Provenance.ResolvedCommit = evidence.SkillFolderHash;
        record.Provenance.SelectedContentIdentity = evidence.SkillFolderHash;
        record.Provenance.ProviderVersion = SkillsCliClient.Version;
        record.LastOperationOutcome = outcome;
    }

    private ManagementRecord RequireManagedRecord(string installationId, bool requireHealthy)
    {
        var state = stateStore.Load();
        if (state.PendingOperation is not null)
        {
            throw new ProviderFailure("Checks are unavailable while a mutation is pending.");
        }
        var record = FindRecord(state, installationId);
        if (requireHealthy)
        {
            VerifyManagedTopology(record);
        }
        return record;
    }

    private static ManagementRecord FindRecord(SkillyState state, string installationId)
    {
        var record = state.Records.SingleOrDefault(candidate => candidate.InstallationId == installationId);
        if (record is null || !string.Equals(record.Provenance.SourceProvider, "skills", StringComparison.Ordinal))
        {
            throw new ProviderFailure("The selected installation has no current skills provider Management Record.");
        }
        return record;
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

    private PendingOperation? TryLoadPending()
    {
        try
        {
            return stateStore.Load().PendingOperation;
        }
        catch (RecoveryRequiredException)
        {
            return null;
        }
    }

    private static void RequireFreshUpdate(ManagementRecord record)
    {
        var check = record.LatestCheck;
        if (check?.Status != UpdateStatus.UpdateAvailable || check.IsStale || check.Failure is not null
            || string.IsNullOrWhiteSpace(check.AvailableRevision)
            || string.IsNullOrWhiteSpace(check.AvailablePayloadHash)
            || string.IsNullOrWhiteSpace(check.AvailableContentIdentity))
        {
            throw new ProviderFailure("A fresh read-only Check reporting Update Available is required before provider update.");
        }
    }

    private static void VerifyManagedTopology(ManagementRecord record)
    {
        if (!Directory.Exists(record.CanonicalPath)
            || File.GetAttributes(record.CanonicalPath).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ProviderFailure("The canonical Skill Installation is missing or has conflicting topology.");
        }
        var hash = PayloadHasher.HashFolder(record.CanonicalPath);
        if (!string.Equals(hash, record.InstalledPayloadHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderFailure("The Skill Installation is Locally Modified; provider mutation is blocked.");
        }
        if (record.IntendedClaudeJunctionPath is null
            || !Junction.IsJunctionTo(record.IntendedClaudeJunctionPath, record.CanonicalPath))
        {
            throw new ProviderFailure("The intended Claude Harness Exposure is not a verified junction.");
        }
    }

    private void VerifyCurrentProviderEvidence(ManagementRecord record)
    {
        var name = record.Provenance.ProviderSkillName ?? Path.GetFileName(record.CanonicalPath);
        var evidence = FindLockEntry(_lock.Read(), name, Path.GetFileName(record.CanonicalPath));
        VerifySourceEvidence(record.Provenance.OriginalReference, evidence);
        if (!string.Equals(evidence.Evidence, record.ProviderEvidence, StringComparison.Ordinal)
            || !string.Equals(evidence.SkillFolderHash, record.InstalledRevision, StringComparison.Ordinal)
            || !string.Equals(record.Provenance.ProviderVersion, SkillsCliClient.Version, StringComparison.Ordinal))
        {
            throw new ProviderFailure("Current provider lock evidence no longer matches recorded Provenance; mutation and Check are blocked.");
        }
        VerifyListedSkill(client.ListGlobal(), record.CanonicalPath);
    }

    private static void VerifyCanonicalAndExposure(string canonical, string claude)
    {
        if (!Directory.Exists(canonical) || File.GetAttributes(canonical).HasFlag(FileAttributes.ReparsePoint))
        {
            throw new ProviderFailure("Provider output did not contain one real canonical Skill folder.");
        }
        if (!Junction.IsJunctionTo(claude, canonical))
        {
            throw new ProviderFailure("The provider's Claude output is not a junction to canonical content; copy fallback is a failed operation.");
        }
        if (SkillMdReader.Read(canonical, Path.GetFileName(canonical)).Status != MetadataReadStatus.Valid)
        {
            throw new ProviderFailure("Provider output does not contain valid canonical SKILL.md metadata.");
        }
    }

    private static void VerifyListedSkill(IReadOnlyList<SkillsCliListedSkill> listed, string canonical)
    {
        var matches = listed.Where(skill => PathsEqual(skill.Path, canonical)).ToList();
        if (matches.Count != 1 || !string.Equals(matches[0].Scope, "global", StringComparison.OrdinalIgnoreCase))
        {
            throw new ProviderFailure("Provider global inventory does not identify exactly one canonical installation.");
        }
    }

    private static SkillsCliLockEntry FindLockEntry(
        IReadOnlyDictionary<string, SkillsCliLockEntry> entries,
        string providerName,
        string folderName)
    {
        var matches = entries.Values.Where(entry =>
            string.Equals(entry.Name, providerName, StringComparison.OrdinalIgnoreCase)
            || string.Equals(SkillsCliClient.SanitizeName(entry.Name), folderName, StringComparison.OrdinalIgnoreCase)).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new ProviderFailure("Provider lock did not contain exactly one complete entry for the resulting canonical Skill.");
    }

    private static void VerifySourceEvidence(string requestedSource, SkillsCliLockEntry evidence)
    {
        var requested = ComparableSource(requestedSource);
        var candidates = new[] { evidence.Source, evidence.SourceUrl }
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(value => ComparableSource(value!));
        if (!candidates.Any(candidate => string.Equals(candidate, requested, StringComparison.OrdinalIgnoreCase)
                                         || requested.EndsWith('/' + candidate, StringComparison.OrdinalIgnoreCase)
                                         || candidate.EndsWith('/' + requested, StringComparison.OrdinalIgnoreCase)
                                         || requested.StartsWith(candidate + '/', StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProviderFailure("Provider lock source evidence does not match the requested normalized Skill Library.");
        }
    }

    private static string ComparableSource(string source)
    {
        var normalized = source.Trim().Replace('\\', '/');
        var fragment = normalized.IndexOf('#');
        if (fragment >= 0) normalized = normalized[..fragment];
        var selector = normalized.LastIndexOf('@');
        if (!normalized.StartsWith("git@", StringComparison.OrdinalIgnoreCase)
            && selector > normalized.LastIndexOf('/'))
        {
            normalized = normalized[..selector];
        }
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            var segments = uri.AbsolutePath.Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (uri.Host.Equals("github.com", StringComparison.OrdinalIgnoreCase) && segments.Length >= 2)
            {
                return $"github.com/{segments[0]}/{TrimGitSuffix(segments[1])}";
            }
            normalized = uri.Host + "/" + uri.AbsolutePath.Trim('/');
        }
        return TrimGitSuffix(normalized.TrimEnd('/'));
    }

    private static string TrimGitSuffix(string value)
        => value.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? value[..^4] : value;

    private static void ValidateCredentialFreeReference(string source)
    {
        if (Uri.TryCreate(source.Trim(), UriKind.Absolute, out var uri)
            && (!string.IsNullOrEmpty(uri.UserInfo)
                || uri.Query.Contains("token=", StringComparison.OrdinalIgnoreCase)
                || uri.Query.Contains("key=", StringComparison.OrdinalIgnoreCase)
                || uri.Query.Contains("secret=", StringComparison.OrdinalIgnoreCase)
                || uri.Query.Contains("password=", StringComparison.OrdinalIgnoreCase)))
        {
            throw new ProviderFailure(
                "skills provider source references must not embed credentials; use the provider's existing authentication.");
        }
    }

    private PendingOperation CreatePending(
        MutationType type,
        List<string> installationIds,
        IReadOnlyList<MutationPaths> paths,
        List<string?> hashes)
    {
        var operationId = Guid.NewGuid().ToString("N");
        return new PendingOperation
        {
            OperationId = operationId,
            OperationType = type,
            AffectedInstallationIds = installationIds,
            StartingPaths = paths.SelectMany(static path => new[] { path.Canonical, path.Claude }).Append(_lock.Path).ToList(),
            StartingHashes = hashes,
            StartingPathStates = paths.SelectMany(path => new[]
            {
                PathEntryExists(path.Canonical) ? PathState.Directory : PathState.Missing,
                PathEntryExists(path.Claude) ? PathState.Junction : PathState.Missing,
            }).Append(File.Exists(_lock.Path) ? PathState.File : PathState.Missing).ToList(),
            RecoveryDirectory = Path.Combine(Path.GetDirectoryName(stateStore.FilePath)!, "recovery", operationId),
            Phase = PendingOperationPhase.Journaled,
            StartedAt = DateTimeOffset.Now,
        };
    }

    private void SavePhase(SkillyState state, PendingOperation pending, PendingOperationPhase phase)
    {
        pending.Phase = phase;
        state.PendingOperation = pending;
        stateStore.Save(state);
    }

    private void RetainCancellation(SkillyState state, PendingOperation pending)
    {
        pending.CancellationRequested = true;
        state.PendingOperation = pending;
        state.LastOperationNote = $"{SkillsCliClient.Package} mutation cancellation requested; pending recovery data retained.";
        stateStore.Save(state);
    }

    private void FailAndRestore(
        SkillyState state,
        PendingOperation pending,
        ProviderSnapshot snapshot,
        Exception exception,
        string operation,
        IReadOnlyList<ManagementRecord> startingRecords)
    {
        var restored = snapshot.Restore();
        state = stateStore.Load();
        state.Records.RemoveAll(record => pending.AffectedInstallationIds.Contains(record.InstallationId));
        state.Records.AddRange(startingRecords);
        state.PendingOperation = restored ? null : pending;
        state.LastOperationNote = restored
            ? $"{operation} failed and prior provider/content state was restored: {exception.Message}"
            : $"{operation} failed and restoration could not be proven. Recovery Required: {exception.Message}";
        if (!restored)
        {
            stateStore.EnterRecoveryRequired(state.LastOperationNote);
            throw new ProviderFailure(state.LastOperationNote);
        }
        stateStore.Save(state);
        snapshot.Cleanup();
        throw new ProviderFailure($"{operation} failed; prior state was safely restored. {exception.Message}");
    }

    private void VerifyPersisted(IEnumerable<string> installationIds)
    {
        var ids = installationIds.ToHashSet(StringComparer.Ordinal);
        var persisted = stateStore.Load();
        if (persisted.PendingOperation is not null)
        {
            throw new ProviderFailure("Durable state retained a pending operation after provider reconciliation.");
        }
        foreach (var record in persisted.Records.Where(record => ids.Contains(record.InstallationId)))
        {
            VerifyManagedTopology(record);
            var name = record.Provenance.ProviderSkillName ?? Path.GetFileName(record.CanonicalPath);
            var evidence = FindLockEntry(_lock.Read(), name, Path.GetFileName(record.CanonicalPath));
            if (!string.Equals(record.ProviderEvidence, evidence.Evidence, StringComparison.Ordinal)
                || !string.Equals(record.InstalledRevision, evidence.SkillFolderHash, StringComparison.Ordinal))
            {
                throw new ProviderFailure("Durable Provenance does not match reconciled provider lock evidence.");
            }
        }
        if (ids.Count > 0 && persisted.Records.Count(record => ids.Contains(record.InstallationId)) != ids.Count)
        {
            throw new ProviderFailure("Durable state did not retain every verified provider installation.");
        }
    }

    private string ReadOnlyFingerprint()
    {
        var canonical = HarnessRoot.Create(RootKind.CanonicalAgents, home).FullPath;
        var claude = HarnessRoot.Create(RootKind.ClaudeSkills, home).FullPath;
        var lockHash = File.Exists(_lock.Path) ? Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(_lock.Path))) : "missing";
        return FingerprintRoot(canonical) + "|" + FingerprintRoot(claude) + "|" + lockHash;
    }

    private static string FingerprintRoot(string root)
    {
        if (!Directory.Exists(root)) return "missing";
        return string.Join('|', Directory.EnumerateDirectories(root).OrderBy(static path => path, StringComparer.OrdinalIgnoreCase)
            .Select(path =>
            {
                var info = new DirectoryInfo(path);
                if (!info.Attributes.HasFlag(FileAttributes.ReparsePoint))
                {
                    return info.Name + ":" + PayloadHasher.HashFolder(path);
                }
                var target = info.ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? "broken";
                return info.Name + ":link:" + target;
            }));
    }

    private static IReadOnlyDictionary<string, string?> IsolatedEnvironment(string root)
        => new Dictionary<string, string?>
        {
            ["USERPROFILE"] = root,
            ["HOME"] = root,
            ["XDG_STATE_HOME"] = Path.Combine(root, "state"),
            ["XDG_CONFIG_HOME"] = Path.Combine(root, "config"),
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(root, ".claude"),
            ["CODEX_HOME"] = Path.Combine(root, ".codex"),
        };

    private static ManagementRecord CloneRecord(ManagementRecord record)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(record);
        return System.Text.Json.JsonSerializer.Deserialize<ManagementRecord>(json)!;
    }

    private static string ResolveLockPath(string userHome)
    {
        var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
        return string.IsNullOrWhiteSpace(stateHome)
            ? Path.Combine(userHome, ".agents", ".skill-lock.json")
            : Path.Combine(stateHome, "skills", ".skill-lock.json");
    }

    private static bool PathsEqual(string left, string right)
        => string.Equals(
            Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);

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

    private static void DeleteDirectorySafe(string path)
    {
        if (!Directory.Exists(path))
        {
            return;
        }
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
        {
            Directory.Delete(path, recursive: false);
            return;
        }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.TopDirectoryOnly))
        {
            if (Directory.Exists(entry))
            {
                DeleteDirectorySafe(entry);
            }
            else
            {
                File.Delete(entry);
            }
        }
        Directory.Delete(path, recursive: false);
    }

    private ProviderResult<T> Wrap<T>(Func<T> operation, string success)
    {
        try
        {
            return ProviderResult<T>.Success(operation(), success);
        }
        catch (Exception exception)
        {
            return ProviderResult<T>.Failure(exception.Message);
        }
    }

    private sealed record MutationPaths(string Canonical, string Claude);

    private sealed record SnapshotPath(
        string Canonical,
        string Claude,
        string CanonicalSnapshot,
        bool CanonicalExisted,
        bool ClaudeExisted);

    private sealed class ProviderSnapshot(
        string root,
        string lockPath,
        bool lockExisted,
        IReadOnlyList<SnapshotPath> paths,
        RollingLog log)
    {
        public static ProviderSnapshot Open(
            string root,
            string lockPath,
            bool lockExisted,
            IReadOnlyList<MutationPaths> mutationPaths,
            IReadOnlyList<PathState> states,
            RollingLog log)
        {
            var snapshots = mutationPaths.Select((path, index) => new SnapshotPath(
                path.Canonical,
                path.Claude,
                Path.Combine(root, $"canonical-{index}"),
                states.ElementAtOrDefault(index * 2) == PathState.Directory,
                states.ElementAtOrDefault(index * 2 + 1) == PathState.Junction)).ToList();
            return new ProviderSnapshot(root, lockPath, lockExisted, snapshots, log);
        }

        public static ProviderSnapshot Create(
            string root,
            string lockPath,
            IReadOnlyList<MutationPaths> mutationPaths,
            RollingLog log)
        {
            Directory.CreateDirectory(root);
            var snapshots = new List<SnapshotPath>();
            for (var index = 0; index < mutationPaths.Count; index++)
            {
                var path = mutationPaths[index];
                var canonicalExisted = Directory.Exists(path.Canonical);
                var claudeExisted = PathEntryExists(path.Claude);
                var canonicalSnapshot = Path.Combine(root, $"canonical-{index}");
                if (canonicalExisted)
                {
                    CopyDirectory(path.Canonical, canonicalSnapshot);
                }
                snapshots.Add(new SnapshotPath(path.Canonical, path.Claude, canonicalSnapshot, canonicalExisted, claudeExisted));
            }
            var lockExisted = File.Exists(lockPath);
            if (lockExisted)
            {
                File.Copy(lockPath, Path.Combine(root, "provider-lock.json"), overwrite: true);
            }
            return new ProviderSnapshot(root, lockPath, lockExisted, snapshots, log);
        }

        public bool Restore()
        {
            try
            {
                foreach (var path in paths)
                {
                    if (PathEntryExists(path.Claude))
                    {
                        Directory.Delete(path.Claude, recursive: !File.GetAttributes(path.Claude).HasFlag(FileAttributes.ReparsePoint));
                    }
                    if (Directory.Exists(path.Canonical))
                    {
                        Directory.Delete(path.Canonical, recursive: true);
                    }
                    if (path.CanonicalExisted)
                    {
                        CopyDirectory(path.CanonicalSnapshot, path.Canonical);
                    }
                    if (path.ClaudeExisted)
                    {
                        Junction.Create(path.Claude, path.Canonical);
                    }
                }

                if (File.Exists(lockPath))
                {
                    File.Delete(lockPath);
                }
                if (lockExisted)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
                    File.Copy(Path.Combine(root, "provider-lock.json"), lockPath, overwrite: true);
                }
                var contentRestored = paths.All(path => path.CanonicalExisted
                    ? Directory.Exists(path.Canonical)
                      && string.Equals(
                          PayloadHasher.HashFolder(path.Canonical),
                          PayloadHasher.HashFolder(path.CanonicalSnapshot),
                          StringComparison.OrdinalIgnoreCase)
                      && (!path.ClaudeExisted || Junction.IsJunctionTo(path.Claude, path.Canonical))
                    : !PathEntryExists(path.Canonical) && !PathEntryExists(path.Claude));
                var lockRestored = lockExisted
                    ? File.Exists(lockPath) && File.ReadAllBytes(lockPath).SequenceEqual(File.ReadAllBytes(Path.Combine(root, "provider-lock.json")))
                    : !File.Exists(lockPath);
                return contentRestored && lockRestored;
            }
            catch (Exception exception)
            {
                log.Error("Provider mutation restoration failed.", exception);
                return false;
            }
        }

        public void Cleanup()
        {
            try
            {
                DeleteDirectorySafe(root);
                var parent = Path.GetDirectoryName(root);
                if (parent is not null && Directory.Exists(parent) && !Directory.EnumerateFileSystemEntries(parent).Any())
                {
                    Directory.Delete(parent);
                }
            }
            catch (Exception exception)
            {
                log.Error("Verified provider recovery data could not be removed.", exception);
            }
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint))
                {
                    throw new ProviderFailure($"Provider snapshot refused nested reparse point '{directory}'.");
                }
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: false);
            }
        }

    }
}
