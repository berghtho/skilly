using System.IO;
using System.Security.Cryptography;
using Skilly.Infrastructure;
using Skilly.Providers.GitHub;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.Providers.Apm;

public sealed class ApmProvider(
    ApmClient client,
    StateStore stateStore,
    RollingLog log,
    string home,
    string? apmHome = null)
{
    private const string PendingOwner = "microsoft/apm:apm-cli";
    private readonly ApmGlobalState _apm = new(apmHome is null ? home : Path.GetDirectoryName(apmHome)!);
    private readonly string _apmRoot = apmHome ?? Path.Combine(home, ".apm");

    public ProviderReadiness GetReadiness() => client.GetReadiness();
    public bool RecoveryRequired => stateStore.RecoveryRequired || TryLoadPending() is not null;
    public string RecoveryDiagnostic => stateStore.RecoveryDiagnostic ?? "A pending APM mutation requires restart recovery.";
    public bool OwnsPendingOperation(PendingOperation? pending)
        => string.Equals(pending?.TargetProviderEvidence, PendingOwner, StringComparison.Ordinal);

    public ProviderResult<ApmInspection> Inspect(string source)
    {
        var temporaryRoot = string.Empty;
        try
        {
            source = RequireSource(source);
            var version = client.RequireSupportedVersion();
            var before = ReadOnlyFingerprint();
            temporaryRoot = Path.Combine(Path.GetDirectoryName(stateStore.FilePath)!, "apm-inspect-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temporaryRoot);
            var environment = IsolatedEnvironment(temporaryRoot);
            var process = client.Install(source, [], environment);
            ApmClient.RequireExit(process, "Isolated APM source inspection");
            var isolatedApm = new ApmGlobalState(temporaryRoot);
            var canonicalRoot = Path.Combine(temporaryRoot, ".agents", "skills");
            var claudeRoot = Path.Combine(temporaryRoot, ".claude", "skills");
            if (Directory.Exists(claudeRoot) && Directory.EnumerateFileSystemEntries(claudeRoot).Any())
            {
                throw new ProviderFailure("APM inspection produced a separate Claude Skill copy; target topology is incompatible.");
            }
            var discovered = Directory.Exists(canonicalRoot)
                ? Directory.EnumerateDirectories(canonicalRoot).OrderBy(Path.GetFileName, StringComparer.Ordinal)
                    .Select(path =>
                    {
                        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
                            throw new ProviderFailure("APM inspection produced a linked canonical Skill instead of a real folder.");
                        var metadata = SkillMdReader.Read(path, Path.GetFileName(path));
                        if (metadata.Status != MetadataReadStatus.Valid)
                            throw new ProviderFailure($"APM inspection produced invalid SKILL.md metadata at '{path}': {metadata.Error}");
                        var evidence = isolatedApm.FindForSkill(Path.GetFileName(path));
                        VerifyCanonicalOnly(evidence);
                        return (Skill: new ApmSourceSkill(Path.GetFileName(path), metadata.Description ?? string.Empty), Evidence: evidence);
                    }).ToList()
                : [];
            var skills = discovered.Select(item => item.Skill with
            {
                ProviderSelectionName = item.Evidence.SkillSubset.Contains(item.Skill.Name, StringComparer.Ordinal)
                                        || discovered.Count(other => string.Equals(other.Evidence.Identity, item.Evidence.Identity, StringComparison.OrdinalIgnoreCase)) > 1
                    ? item.Skill.Name
                    : null,
            }).ToList();
            if (skills.Count == 0)
                throw new ProviderFailure("APM source inspection did not discover any canonical Source Skills.");
            if (!string.Equals(before, ReadOnlyFingerprint(), StringComparison.Ordinal))
                throw new ProviderFailure("Read-only APM inspection changed the user's manifest, lock, payload, or Harness Exposures.");
            return ProviderResult<ApmInspection>.Success(
                new ApmInspection(source, ApmGlobalState.NormalizeSource(source), version, skills),
                $"Read-only APM inspection found {skills.Count} Source Skill(s) in an isolated home; user state was not changed.");
        }
        catch (Exception exception)
        {
            return ProviderResult<ApmInspection>.Failure(exception.Message);
        }
        finally
        {
            if (temporaryRoot.Length > 0) DeleteDirectorySafe(temporaryRoot);
        }
    }

    public ProviderResult<ApmInstallResult> Install(ApmInspection inspection, IReadOnlyList<ApmSourceSkill> selected, CancellationToken cancellationToken = default)
        => Wrap(() => InstallCore(inspection, selected, cancellationToken), "Installed through Microsoft APM and reconciled manifest, lock, payload, Provenance, state, and exposures.");

    public ProviderResult<CheckResult> Check(ManagementRecord record)
    {
        try
        {
            client.RequireSupportedVersion();
            var current = RequireManagedRecord(record.InstallationId);
            VerifyAllManagedApmState(stateStore.Load());
            var before = ReadOnlyFingerprint();
            var process = client.Outdated();
            var rows = client.ParseOutdated(process);
            if (!string.Equals(before, ReadOnlyFingerprint(), StringComparison.Ordinal))
                throw new ProviderFailure("APM outdated changed manifest, lock, payload, or Harness Exposures; Check failed closed.");
            var identity = current.Provenance.Repository;
            var matches = rows.Where(row => IdentityMatches(row.Package, identity)).ToList();
            if (matches.Count == 0)
            {
                if (rows.Count != 0) throw new ProviderFailure($"APM outdated output did not contain exactly one row for '{identity}' (parsed: {string.Join(", ", rows.Select(row => row.Package))}).");
                return ProviderResult<CheckResult>.Success(
                    new CheckResult(UpdateStatus.Current, current.InstalledRevision, null, current.InstalledRevision, null,
                        current.InstalledPayloadHash, DateTimeOffset.Now, AvailableContentIdentity: current.Provenance.SelectedContentIdentity),
                    "APM outdated reported all dependencies current; installed content was not changed.");
            }
            if (matches.Count != 1) throw new ProviderFailure($"APM outdated output was ambiguous for '{identity}'.");
            var row = matches[0];
            var status = row.Status switch
            {
                "up-to-date" => UpdateStatus.Current,
                "outdated" => UpdateStatus.UpdateAvailable,
                "unknown" => UpdateStatus.SourceUnavailable,
                _ => throw new ProviderFailure($"APM outdated returned unsupported status '{row.Status}'."),
            };
            return ProviderResult<CheckResult>.Success(
                new CheckResult(status, current.InstalledRevision, null,
                    row.Latest == "-" ? null : row.Latest,
                    null,
                    status == UpdateStatus.Current ? current.InstalledPayloadHash : null,
                    DateTimeOffset.Now,
                    status == UpdateStatus.SourceUnavailable ? "APM could not query or resolve the authoritative source." : null,
                    status == UpdateStatus.Current ? current.Provenance.SelectedContentIdentity : row.Latest),
                $"Read-only APM outdated Check reported {row.Status}; installed content was not changed.");
        }
        catch (Exception exception)
        {
            return ProviderResult<CheckResult>.Failure(exception.Message);
        }
    }

    public ProviderResult<ApmUpdateResult> Update(ManagementRecord record, CancellationToken cancellationToken = default)
        => Wrap(() => UpdateCore(record, cancellationToken), "Updated through APM with affirmative noninteractive consent and reconciled every postcondition.");

    public ProviderResult<LifecycleResult> Uninstall(ManagementRecord record, CancellationToken cancellationToken = default)
        => Wrap(() => UninstallCore(record, cancellationToken), "Uninstalled through APM without dry-run and verified provider, filesystem, exposure, and authority absence.");

    public RecoveryResult RecoverPendingOperation()
    {
        try
        {
            var state = stateStore.Load();
            var pending = state.PendingOperation;
            if (!OwnsPendingOperation(pending)) return new RecoveryResult(RecoveryDisposition.None, "No pending APM mutation requires recovery.");
            var paths = PendingPaths(pending!);
            var snapshot = ApmSnapshot.Open(pending!.RecoveryDirectory!, _apmRoot, paths, pending.StartingPathStates, log);
            if (!snapshot.Restore())
            {
                var diagnostic = "Recovery Required: the pending APM mutation could not be restored from verified snapshots.";
                stateStore.EnterRecoveryRequired(diagnostic);
                return new RecoveryResult(RecoveryDisposition.RecoveryRequired, diagnostic);
            }
            state.PendingOperation = null;
            state.LastOperationNote = "Safely restored the pending APM mutation without retrying it.";
            stateStore.Save(state);
            snapshot.Cleanup();
            return new RecoveryResult(RecoveryDisposition.Restored, state.LastOperationNote);
        }
        catch (Exception exception)
        {
            stateStore.EnterRecoveryRequired($"Recovery Required: {exception.Message}");
            return new RecoveryResult(RecoveryDisposition.RecoveryRequired, stateStore.RecoveryDiagnostic!);
        }
    }

    public void RequestMutationCancellation()
    {
        var state = stateStore.Load();
        if (!OwnsPendingOperation(state.PendingOperation)) return;
        state.PendingOperation!.CancellationRequested = true;
        state.LastOperationNote = "Cancellation requested; pending APM recovery data retained.";
        stateStore.Save(state);
    }

    private ApmInstallResult InstallCore(ApmInspection inspection, IReadOnlyList<ApmSourceSkill> selected, CancellationToken cancellationToken)
    {
        if (selected.Count == 0) throw new ProviderFailure("No Source Skills are selected; installation is unavailable.");
        if (selected.Any(skill => !skill.MetadataValid) || selected.GroupBy(skill => skill.FolderName, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1))
            throw new ProviderFailure("Selected APM Source Skills must resolve to unique safe canonical folder names.");
        var version = client.RequireSupportedVersion();
        if (!string.Equals(version, inspection.ProviderVersion, StringComparison.Ordinal))
            throw new ProviderFailure("APM version changed since inspection; inspect the source again before installation.");
        var state = RequireWritableState();
        var startingRecords = CloneRecords(state.Records);
        VerifyAllManagedApmState(state);
        var paths = AllApmPaths(state).Concat(selected.Select(skill => PathsFor(skill.FolderName))).DistinctBy(path => path.Canonical, StringComparer.OrdinalIgnoreCase).ToList();
        foreach (var skill in selected)
        {
            var path = PathsFor(skill.FolderName);
            if (PathEntryExists(path.Canonical) || PathEntryExists(path.Claude))
                throw new ProviderFailure($"'{path.Canonical}' or its Claude destination already exists. Collision blocks APM installation.");
        }
        var beforeFolders = CanonicalFolderNames();
        var pending = CreatePending(MutationType.Install, [], paths);
        state.PendingOperation = pending;
        stateStore.Save(state);
        var snapshot = ApmSnapshot.Create(pending.RecoveryDirectory!, _apmRoot, paths, CanonicalRoot(), log);
        try
        {
            SavePhase(state, pending, PendingOperationPhase.SnapshotReady);
            cancellationToken.ThrowIfCancellationRequested();
            SavePhase(state, pending, PendingOperationPhase.MutationStarted);
            var process = client.Install(inspection.OriginalReference, selected.Select(skill => skill.ProviderSelectionName)
                .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).ToList());
            ApmClient.RequireExit(process, "APM global install");
            cancellationToken.ThrowIfCancellationRequested();
            var allowedNew = selected.Select(skill => skill.FolderName).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var unexpected = CanonicalFolderNames().Except(beforeFolders, StringComparer.OrdinalIgnoreCase).Except(allowedNew, StringComparer.OrdinalIgnoreCase).ToList();
            if (unexpected.Count > 0) throw new ProviderFailure($"APM deployed unexpected canonical Skills: {string.Join(", ", unexpected)}.");
            var records = new List<ManagementRecord>();
            foreach (var skill in selected)
            {
                var path = PathsFor(skill.FolderName);
                VerifyCanonicalWithoutClaude(path);
                var evidence = _apm.FindForSkill(skill.FolderName);
                VerifyCanonicalOnly(evidence);
                VerifyRequestedSource(inspection.NormalizedSource, evidence);
                Junction.Create(path.Claude, path.Canonical);
                VerifyTopology(path);
                records.Add(CreateRecord(inspection, skill, path, evidence));
            }
            pending.AffectedInstallationIds = records.Select(record => record.InstallationId).ToList();
            SavePhase(state, pending, PendingOperationPhase.Verified);
            state.Records.AddRange(records);
            RefreshAllApmEvidence(state);
            state.PendingOperation = null;
            state.LastOperationNote = $"installed {records.Count} Skill(s) through Microsoft APM";
            stateStore.Save(state);
            VerifyPersisted(records.Select(record => record.InstallationId));
            snapshot.Cleanup();
            return new ApmInstallResult(records.Select(record => new ApmInstalledSkill(record.Provenance.SourceSkillPath, record.CanonicalPath)).ToList());
        }
        catch (OperationCanceledException)
        {
            RetainCancellation(state, pending);
            throw;
        }
        catch (Exception exception)
        {
            FailAndRestore(pending, snapshot, exception, "Install", startingRecords);
            throw;
        }
    }

    private ApmUpdateResult UpdateCore(ManagementRecord requested, CancellationToken cancellationToken)
    {
        client.RequireSupportedVersion();
        var state = RequireWritableState();
        var record = FindRecord(state, requested.InstallationId);
        RequireFreshUpdate(record);
        VerifyAllManagedApmState(state);
        var startingRecords = CloneRecords(state.Records);
        var paths = AllApmPaths(state);
        var pending = CreatePending(MutationType.Update, [record.InstallationId], paths);
        pending.TargetRevision = record.LatestCheck!.AvailableRevision;
        pending.TargetContentIdentity = record.LatestCheck.AvailableContentIdentity;
        state.PendingOperation = pending;
        stateStore.Save(state);
        var snapshot = ApmSnapshot.Create(pending.RecoveryDirectory!, _apmRoot, paths, CanonicalRoot(), log);
        try
        {
            SavePhase(state, pending, PendingOperationPhase.SnapshotReady);
            cancellationToken.ThrowIfCancellationRequested();
            SavePhase(state, pending, PendingOperationPhase.MutationStarted);
            var oldRevision = record.InstalledRevision;
            var oldHash = record.InstalledPayloadHash;
            var process = client.Update(record.Provenance.Repository);
            ApmClient.RequireExit(process, $"APM update of '{record.Provenance.Repository}'");
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var candidate in state.Records.Where(IsApmRecord)) VerifyTopology(PathsForRecord(candidate));
            var evidence = _apm.FindForSkill(Path.GetFileName(record.CanonicalPath));
            VerifyCanonicalOnly(evidence);
            var actualHash = PayloadHasher.HashFolder(record.CanonicalPath);
            if (string.Equals(oldRevision, evidence.Revision, StringComparison.OrdinalIgnoreCase)
                && string.Equals(oldHash, actualHash, StringComparison.OrdinalIgnoreCase))
                throw new ProviderFailure("APM update reported success but neither lock revision nor selected Skill payload changed.");
            if (!MatchesAvailable(record.LatestCheck.AvailableRevision!, evidence))
                throw new ProviderFailure("APM update did not produce the revision reported by the read-only outdated Check.");
            SavePhase(state, pending, PendingOperationPhase.Verified);
            RefreshAllApmEvidence(state, OperationOutcome.Updated);
            foreach (var candidate in state.Records.Where(IsApmRecord))
            {
                candidate.LatestCheck = new CheckSnapshot
                {
                    Status = UpdateStatus.Current,
                    InstalledRevision = candidate.InstalledRevision,
                    AvailableRevision = candidate.InstalledRevision,
                    AvailablePayloadHash = candidate.InstalledPayloadHash,
                    AvailableContentIdentity = candidate.Provenance.SelectedContentIdentity,
                    CheckedAt = DateTimeOffset.Now,
                };
            }
            state.PendingOperation = null;
            state.LastOperationNote = $"updated APM dependency '{record.Provenance.Repository}' with --yes";
            stateStore.Save(state);
            VerifyPersisted([record.InstallationId]);
            snapshot.Cleanup();
            return new ApmUpdateResult(record.InstallationId, record.InstalledRevision);
        }
        catch (OperationCanceledException)
        {
            RetainCancellation(state, pending);
            throw;
        }
        catch (Exception exception)
        {
            FailAndRestore(pending, snapshot, exception, "Update", startingRecords);
            throw;
        }
    }

    private LifecycleResult UninstallCore(ManagementRecord requested, CancellationToken cancellationToken)
    {
        client.RequireSupportedVersion();
        var state = RequireWritableState();
        var record = FindRecord(state, requested.InstallationId);
        VerifyAllManagedApmState(state);
        var startingRecords = CloneRecords(state.Records);
        var packageRecords = state.Records.Where(candidate => IsApmRecord(candidate)
            && string.Equals(candidate.Provenance.Repository, record.Provenance.Repository, StringComparison.OrdinalIgnoreCase)).ToList();
        var paths = AllApmPaths(state);
        var pending = CreatePending(MutationType.Uninstall, packageRecords.Select(candidate => candidate.InstallationId).ToList(), paths);
        state.PendingOperation = pending;
        stateStore.Save(state);
        var snapshot = ApmSnapshot.Create(pending.RecoveryDirectory!, _apmRoot, paths, CanonicalRoot(), log);
        try
        {
            SavePhase(state, pending, PendingOperationPhase.SnapshotReady);
            cancellationToken.ThrowIfCancellationRequested();
            SavePhase(state, pending, PendingOperationPhase.MutationStarted);
            var process = client.Uninstall(record.Provenance.Repository);
            ApmClient.RequireExit(process, $"APM uninstall of '{record.Provenance.Repository}'");
            cancellationToken.ThrowIfCancellationRequested();
            if (_apm.ManifestDeclaresIdentity(record.Provenance.Repository))
                throw new ProviderFailure("APM uninstall reported success but manifest ownership remains.");
            if (File.Exists(_apm.LockPath)
                && (!File.Exists(_apm.ManifestPath) || _apm.ContainsIdentity(record.Provenance.Repository)))
                throw new ProviderFailure("APM uninstall reported success but lock ownership remains or provider evidence is incomplete.");
            foreach (var candidate in packageRecords)
            {
                if (PathEntryExists(candidate.CanonicalPath)) throw new ProviderFailure("APM uninstall reported success but canonical Skill content remains.");
                if (PathEntryExists(candidate.IntendedClaudeJunctionPath!)) Directory.Delete(candidate.IntendedClaudeJunctionPath!, false);
                if (PathEntryExists(candidate.IntendedClaudeJunctionPath!)) throw new ProviderFailure("Skilly could not remove the Claude Harness Exposure after APM uninstall.");
            }
            SavePhase(state, pending, PendingOperationPhase.Verified);
            state.Records.RemoveAll(candidate => packageRecords.Contains(candidate));
            RefreshAllApmEvidence(state);
            state.PendingOperation = null;
            state.LastOperationNote = $"uninstalled APM dependency '{record.Provenance.Repository}' without dry-run";
            stateStore.Save(state);
            if (stateStore.Load().Records.Any(candidate => packageRecords.Any(removed => removed.InstallationId == candidate.InstallationId)))
                throw new ProviderFailure("Durable state retained authority after verified APM uninstall.");
            snapshot.Cleanup();
            return new LifecycleResult(record.CanonicalPath, $"Removed {packageRecords.Count} APM-owned Skill Installation(s) and their exposures.");
        }
        catch (OperationCanceledException)
        {
            RetainCancellation(state, pending);
            throw;
        }
        catch (Exception exception)
        {
            FailAndRestore(pending, snapshot, exception, "Uninstall", startingRecords);
            throw;
        }
    }

    private ManagementRecord CreateRecord(ApmInspection inspection, ApmSourceSkill skill, MutationPaths paths, ApmDependencyEvidence evidence)
    {
        var hash = PayloadHasher.HashFolder(paths.Canonical);
        return new ManagementRecord
        {
            InstallationId = Guid.NewGuid().ToString("N"),
            CanonicalPath = Path.GetFullPath(paths.Canonical),
            Provenance = new ProvenanceInfo
            {
                SourceProvider = ApmClient.ProviderId,
                OriginalReference = inspection.OriginalReference,
                NormalizedSource = inspection.NormalizedSource,
                Host = SourceHost(evidence.RepositoryUrl),
                Owner = string.Empty,
                Repository = evidence.Identity,
                SourceSkillPath = skill.Name,
                TrackingRule = evidence.TrackingRule,
                TrackingRuleKind = evidence.TrackingRuleKind,
                ResolvedCommit = evidence.Revision,
                SelectedContentIdentity = hash,
                ProviderVersion = inspection.ProviderVersion,
                ProviderSkillName = skill.Name,
            },
            IntendedClaudeJunctionPath = Path.GetFullPath(paths.Claude),
            InstalledRevision = evidence.Revision,
            InstalledPayloadHash = hash,
            InstalledFileCount = Directory.EnumerateFiles(paths.Canonical, "*", SearchOption.AllDirectories).Count(),
            ProviderEvidence = evidence.Evidence,
            LastOperationOutcome = OperationOutcome.Installed,
        };
    }

    private void RefreshAllApmEvidence(SkillyState state, OperationOutcome? outcome = null)
    {
        foreach (var record in state.Records.Where(IsApmRecord))
        {
            var evidence = _apm.FindForSkill(Path.GetFileName(record.CanonicalPath));
            VerifyCanonicalOnly(evidence);
            var hash = PayloadHasher.HashFolder(record.CanonicalPath);
            record.ProviderEvidence = evidence.Evidence;
            record.InstalledRevision = evidence.Revision;
            record.InstalledPayloadHash = hash;
            record.InstalledFileCount = Directory.EnumerateFiles(record.CanonicalPath, "*", SearchOption.AllDirectories).Count();
            record.Provenance.Repository = evidence.Identity;
            record.Provenance.TrackingRule = evidence.TrackingRule;
            record.Provenance.TrackingRuleKind = evidence.TrackingRuleKind;
            record.Provenance.ResolvedCommit = evidence.Revision;
            record.Provenance.SelectedContentIdentity = hash;
            record.Provenance.ProviderVersion = client.RequireSupportedVersion();
            if (outcome is not null) record.LastOperationOutcome = outcome;
        }
    }

    private void VerifyAllManagedApmState(SkillyState state)
    {
        foreach (var record in state.Records.Where(IsApmRecord))
        {
            VerifyTopology(PathsForRecord(record));
            if (!string.Equals(PayloadHasher.HashFolder(record.CanonicalPath), record.InstalledPayloadHash, StringComparison.OrdinalIgnoreCase))
                throw new ProviderFailure("The APM Skill Installation is Locally Modified; provider mutation and Check are blocked.");
            var evidence = _apm.FindForSkill(Path.GetFileName(record.CanonicalPath));
            VerifyCanonicalOnly(evidence);
            if (!string.Equals(record.ProviderEvidence, evidence.Evidence, StringComparison.Ordinal)
                || !string.Equals(record.InstalledRevision, evidence.Revision, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(record.Provenance.Repository, evidence.Identity, StringComparison.OrdinalIgnoreCase))
                throw new ProviderFailure("Current APM manifest/lock evidence no longer matches recorded Provenance; mutation and Check are blocked.");
        }
    }

    private static void VerifyRequestedSource(string source, ApmDependencyEvidence evidence)
    {
        var requested = NormalizeIdentity(source);
        var repository = NormalizeIdentity(evidence.RepositoryUrl);
        if (!requested.Contains(repository, StringComparison.OrdinalIgnoreCase)
            && !repository.Contains(requested, StringComparison.OrdinalIgnoreCase))
            throw new ProviderFailure("APM lock source evidence does not match the requested normalized Skill Library.");
    }

    private static void VerifyCanonicalOnly(ApmDependencyEvidence evidence)
    {
        if (evidence.DeployedFiles.Count == 0
            || evidence.DeployedFiles.Any(file => !file.Replace('\\', '/').TrimStart('/').StartsWith(".agents/skills/", StringComparison.OrdinalIgnoreCase)))
            throw new ProviderFailure($"APM dependency '{evidence.Identity}' deployed outside the canonical .agents/skills destination.");
    }

    private static bool MatchesAvailable(string available, ApmDependencyEvidence evidence)
    {
        var candidate = available.Split([' ', '(', ')'], StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault(value => value.Length >= 7) ?? available;
        return evidence.Revision.StartsWith(candidate, StringComparison.OrdinalIgnoreCase)
               || candidate.StartsWith(evidence.Revision, StringComparison.OrdinalIgnoreCase)
               || string.Equals(evidence.ResolvedRef, candidate, StringComparison.OrdinalIgnoreCase);
    }

    private ManagementRecord RequireManagedRecord(string installationId)
    {
        var state = stateStore.Load();
        if (state.PendingOperation is not null) throw new ProviderFailure("Checks are unavailable while a mutation is pending.");
        return FindRecord(state, installationId);
    }

    private static ManagementRecord FindRecord(SkillyState state, string installationId)
        => state.Records.SingleOrDefault(record => record.InstallationId == installationId && IsApmRecord(record))
           ?? throw new ProviderFailure("The selected installation has no current APM Management Record.");

    private SkillyState RequireWritableState()
    {
        if (stateStore.RecoveryRequired) throw new ProviderFailure(stateStore.RecoveryDiagnostic ?? "Recovery Required; Skilly is read-only.");
        var state = stateStore.Load();
        if (state.PendingOperation is not null) throw new ProviderFailure("Another mutation is pending. Skilly remains read-only until recovery reconciles it.");
        return state;
    }

    private static void RequireFreshUpdate(ManagementRecord record)
    {
        var check = record.LatestCheck;
        if (check?.Status != UpdateStatus.UpdateAvailable || check.IsStale || check.Failure is not null || string.IsNullOrWhiteSpace(check.AvailableRevision))
            throw new ProviderFailure("A fresh read-only APM outdated Check reporting Update Available is required before update.");
    }

    private static bool IsApmRecord(ManagementRecord record)
        => string.Equals(record.Provenance.SourceProvider, ApmClient.ProviderId, StringComparison.Ordinal);

    private MutationPaths PathsFor(string name) => new(Path.Combine(CanonicalRoot(), name), Path.Combine(ClaudeRoot(), name));
    private static MutationPaths PathsForRecord(ManagementRecord record) => new(record.CanonicalPath, record.IntendedClaudeJunctionPath!);
    private List<MutationPaths> AllApmPaths(SkillyState state) => state.Records.Where(IsApmRecord).Select(PathsForRecord).ToList();
    private string CanonicalRoot() => HarnessRoot.Create(RootKind.CanonicalAgents, home).FullPath;
    private string ClaudeRoot() => HarnessRoot.Create(RootKind.ClaudeSkills, home).FullPath;

    private static void VerifyCanonicalWithoutClaude(MutationPaths paths)
    {
        if (!Directory.Exists(paths.Canonical) || File.GetAttributes(paths.Canonical).HasFlag(FileAttributes.ReparsePoint)
            || SkillMdReader.Read(paths.Canonical, Path.GetFileName(paths.Canonical)).Status != MetadataReadStatus.Valid)
            throw new ProviderFailure("APM did not produce one valid real canonical Skill folder.");
        if (PathEntryExists(paths.Claude))
            throw new ProviderFailure("APM produced a separate Claude copy; Skilly requires APM target 'copilot' and owns the Claude junction.");
    }

    private static void VerifyTopology(MutationPaths paths)
    {
        if (!Directory.Exists(paths.Canonical) || File.GetAttributes(paths.Canonical).HasFlag(FileAttributes.ReparsePoint))
            throw new ProviderFailure("The canonical APM Skill Installation is missing or has conflicting topology.");
        if (!Junction.IsJunctionTo(paths.Claude, paths.Canonical))
            throw new ProviderFailure("The intended Claude Harness Exposure is not a verified junction.");
    }

    private void VerifyPersisted(IEnumerable<string> installationIds)
    {
        var ids = installationIds.ToHashSet(StringComparer.Ordinal);
        var state = stateStore.Load();
        if (state.PendingOperation is not null) throw new ProviderFailure("Durable state retained a pending operation after APM reconciliation.");
        VerifyAllManagedApmState(state);
        if (ids.Count > 0 && state.Records.Count(record => ids.Contains(record.InstallationId)) != ids.Count)
            throw new ProviderFailure("Durable state did not retain every verified APM installation.");
    }

    private PendingOperation CreatePending(MutationType type, List<string> ids, IReadOnlyList<MutationPaths> paths)
    {
        var operationId = Guid.NewGuid().ToString("N");
        return new PendingOperation
        {
            OperationId = operationId,
            OperationType = type,
            AffectedInstallationIds = ids,
            StartingPaths = paths.SelectMany(path => new[] { path.Canonical, path.Claude })
                .Concat([_apm.ManifestPath, _apm.LockPath, _apm.ModulesPath]).ToList(),
            StartingHashes = paths.Select(path => Directory.Exists(path.Canonical) ? PayloadHasher.HashFolder(path.Canonical) : null).ToList(),
            StartingPathStates = paths.SelectMany(path => new[]
            {
                PathEntryExists(path.Canonical) ? PathState.Directory : PathState.Missing,
                PathEntryExists(path.Claude) ? PathState.Junction : PathState.Missing,
            }).Concat(new[]
            {
                File.Exists(_apm.ManifestPath) ? PathState.File : PathState.Missing,
                File.Exists(_apm.LockPath) ? PathState.File : PathState.Missing,
                Directory.Exists(_apm.ModulesPath) ? PathState.Directory : PathState.Missing,
            }).ToList(),
            RecoveryDirectory = Path.Combine(Path.GetDirectoryName(stateStore.FilePath)!, "recovery", operationId),
            TargetProviderEvidence = PendingOwner,
            Phase = PendingOperationPhase.Journaled,
            StartedAt = DateTimeOffset.Now,
        };
    }

    private static IReadOnlyList<MutationPaths> PendingPaths(PendingOperation pending)
        => pending.StartingPaths.Take(pending.StartingPaths.Count - 3).Chunk(2).Select(pair => new MutationPaths(pair[0], pair[1])).ToList();

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
        state.LastOperationNote = "APM mutation cancellation requested; pending recovery data retained.";
        stateStore.Save(state);
    }

    private void FailAndRestore(PendingOperation pending, ApmSnapshot snapshot, Exception exception, string operation, List<ManagementRecord> startingRecords)
    {
        var restored = snapshot.Restore();
        var state = stateStore.Load();
        state.Records = startingRecords;
        state.PendingOperation = restored ? null : pending;
        state.LastOperationNote = restored
            ? $"{operation} failed and prior APM/content state was restored: {exception.Message}"
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

    private PendingOperation? TryLoadPending()
    {
        try { return stateStore.Load().PendingOperation; }
        catch (RecoveryRequiredException) { return null; }
    }

    private string ReadOnlyFingerprint()
        => FingerprintPath(_apmRoot, "config.json") + "|" + FingerprintPath(CanonicalRoot()) + "|" + FingerprintPath(ClaudeRoot());

    private static string FingerprintPath(string path, string? excludedRelativePath = null)
    {
        if (!PathEntryExists(path)) return "missing";
        if (File.Exists(path)) return Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var values = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.OrdinalIgnoreCase))
        {
            var relative = Path.GetRelativePath(path, entry);
            if (string.Equals(relative.Replace('\\', '/'), excludedRelativePath, StringComparison.OrdinalIgnoreCase)) continue;
            if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint))
                values.Add(relative + "=>" + (new DirectoryInfo(entry).ResolveLinkTarget(true)?.FullName ?? "broken"));
            else if (File.Exists(entry))
                values.Add(relative + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(entry))));
        }
        return Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(string.Join('|', values))));
    }

    private HashSet<string> CanonicalFolderNames()
        => Directory.Exists(CanonicalRoot())
            ? Directory.EnumerateDirectories(CanonicalRoot()).Select(path => Path.GetFileName(path)).ToHashSet(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    private static IReadOnlyDictionary<string, string?> IsolatedEnvironment(string root)
        => new Dictionary<string, string?>
        {
            ["USERPROFILE"] = root,
            ["HOME"] = root,
            ["CLAUDE_CONFIG_DIR"] = Path.Combine(root, ".claude"),
            ["APM_PROGRESS"] = "never",
        };

    private static string RequireSource(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) throw new ProviderFailure("An APM source reference is required.");
        return source.Trim();
    }

    private static string NormalizeIdentity(string value)
    {
        var normalized = value.Trim().Replace('\\', '/').Trim('/');
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri)) normalized = uri.Host + uri.AbsolutePath;
        if (normalized.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)) normalized = normalized["github.com/".Length..];
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) normalized = normalized[..^4];
        return normalized.ToLowerInvariant();
    }

    private static bool IdentityMatches(string left, string right)
    {
        left = NormalizeIdentity(left);
        right = NormalizeIdentity(right);
        return string.Equals(left, right, StringComparison.OrdinalIgnoreCase)
               || left.EndsWith('/' + right, StringComparison.OrdinalIgnoreCase)
               || right.EndsWith('/' + left, StringComparison.OrdinalIgnoreCase);
    }

    private static string SourceHost(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var uri) ? uri.Host : source.Split('/').FirstOrDefault() ?? string.Empty;

    private static List<ManagementRecord> CloneRecords(List<ManagementRecord> records)
        => System.Text.Json.JsonSerializer.Deserialize<List<ManagementRecord>>(System.Text.Json.JsonSerializer.Serialize(records))!;

    private ProviderResult<T> Wrap<T>(Func<T> operation, string success)
    {
        try { return ProviderResult<T>.Success(operation(), success); }
        catch (Exception exception) { return ProviderResult<T>.Failure(exception.Message); }
    }

    private static bool PathEntryExists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static void DeleteDirectorySafe(string path)
    {
        if (!Directory.Exists(path)) return;
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)) { Directory.Delete(path, false); return; }
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (Directory.Exists(entry)) DeleteDirectorySafe(entry); else File.Delete(entry);
        }
        Directory.Delete(path, false);
    }

    private sealed record MutationPaths(string Canonical, string Claude);

    private sealed class ApmSnapshot(string root, string apmRoot, IReadOnlyList<MutationPaths> paths, IReadOnlyList<PathState> states, RollingLog log)
    {
        public static ApmSnapshot Create(string root, string apmRoot, IReadOnlyList<MutationPaths> paths, string canonicalRoot, RollingLog log)
        {
            Directory.CreateDirectory(root);
            var states = paths.SelectMany(path => new[]
            {
                PathEntryExists(path.Canonical) ? PathState.Directory : PathState.Missing,
                PathEntryExists(path.Claude) ? PathState.Junction : PathState.Missing,
            }).Concat(new[]
            {
                File.Exists(Path.Combine(apmRoot, "apm.yml")) ? PathState.File : PathState.Missing,
                File.Exists(Path.Combine(apmRoot, "apm.lock.yaml")) ? PathState.File : PathState.Missing,
                Directory.Exists(Path.Combine(apmRoot, "apm_modules")) ? PathState.Directory : PathState.Missing,
            }).ToList();
            for (var index = 0; index < paths.Count; index++)
                if (states[index * 2] == PathState.Directory) CopyDirectory(paths[index].Canonical, Path.Combine(root, $"canonical-{index}"));
            CopyIfExists(Path.Combine(apmRoot, "apm.yml"), Path.Combine(root, "apm.yml"));
            CopyIfExists(Path.Combine(apmRoot, "apm.lock.yaml"), Path.Combine(root, "apm.lock.yaml"));
            if (File.Exists(Path.Combine(apmRoot, ".gitignore")))
            {
                File.Copy(Path.Combine(apmRoot, ".gitignore"), Path.Combine(root, ".gitignore"));
                File.WriteAllText(Path.Combine(root, ".gitignore.existed"), string.Empty);
            }
            if (states[^1] == PathState.Directory) CopyDirectory(Path.Combine(apmRoot, "apm_modules"), Path.Combine(root, "apm_modules"));
            var baseline = Directory.Exists(canonicalRoot) ? Directory.EnumerateDirectories(canonicalRoot).Select(path => Path.GetFileName(path)) : [];
            File.WriteAllLines(Path.Combine(root, "canonical-baseline.txt"), baseline);
            return new ApmSnapshot(root, apmRoot, paths, states, log);
        }

        public static ApmSnapshot Open(string root, string apmRoot, IReadOnlyList<MutationPaths> paths, IReadOnlyList<PathState> states, RollingLog log)
            => new(root, apmRoot, paths, states, log);

        public bool Restore()
        {
            try
            {
                var canonicalRoot = paths.Select(path => Path.GetDirectoryName(path.Canonical)!).FirstOrDefault();
                if (canonicalRoot is not null && Directory.Exists(canonicalRoot))
                {
                    var baseline = File.ReadAllLines(Path.Combine(root, "canonical-baseline.txt")).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    foreach (var directory in Directory.EnumerateDirectories(canonicalRoot).Where(path => !baseline.Contains(Path.GetFileName(path))).ToList()) DeleteDirectorySafe(directory);
                }
                for (var index = 0; index < paths.Count; index++)
                {
                    if (PathEntryExists(paths[index].Claude)) Directory.Delete(paths[index].Claude, !File.GetAttributes(paths[index].Claude).HasFlag(FileAttributes.ReparsePoint));
                    if (Directory.Exists(paths[index].Canonical)) DeleteDirectorySafe(paths[index].Canonical);
                    if (states[index * 2] == PathState.Directory) CopyDirectory(Path.Combine(root, $"canonical-{index}"), paths[index].Canonical);
                    if (states[index * 2 + 1] == PathState.Junction) Junction.Create(paths[index].Claude, paths[index].Canonical);
                }
                RestoreFile("apm.yml", states[^3]);
                RestoreFile("apm.lock.yaml", states[^2]);
                RestoreOptionalFile(".gitignore");
                var modules = Path.Combine(apmRoot, "apm_modules");
                if (Directory.Exists(modules)) DeleteDirectorySafe(modules);
                if (states[^1] == PathState.Directory) CopyDirectory(Path.Combine(root, "apm_modules"), modules);
                return Verify();
            }
            catch (Exception exception)
            {
                log.Error("APM mutation restoration failed.", exception);
                return false;
            }
        }

        public void Cleanup()
        {
            try { DeleteDirectorySafe(root); }
            catch (Exception exception) { log.Error("Verified APM recovery data could not be removed.", exception); }
        }

        private void RestoreFile(string name, PathState state)
        {
            var destination = Path.Combine(apmRoot, name);
            if (File.Exists(destination)) File.Delete(destination);
            if (state == PathState.File)
            {
                Directory.CreateDirectory(apmRoot);
                File.Copy(Path.Combine(root, name), destination);
            }
        }

        private void RestoreOptionalFile(string name)
        {
            var destination = Path.Combine(apmRoot, name);
            if (File.Exists(destination)) File.Delete(destination);
            if (File.Exists(Path.Combine(root, name + ".existed"))) File.Copy(Path.Combine(root, name), destination);
        }

        private bool Verify()
        {
            for (var index = 0; index < paths.Count; index++)
            {
                if (states[index * 2] == PathState.Directory)
                {
                    if (!Directory.Exists(paths[index].Canonical)
                        || !string.Equals(PayloadHasher.HashFolder(paths[index].Canonical), PayloadHasher.HashFolder(Path.Combine(root, $"canonical-{index}")), StringComparison.OrdinalIgnoreCase)) return false;
                }
                else if (PathEntryExists(paths[index].Canonical)) return false;
                if (states[index * 2 + 1] == PathState.Junction && !Junction.IsJunctionTo(paths[index].Claude, paths[index].Canonical)) return false;
                if (states[index * 2 + 1] == PathState.Missing && PathEntryExists(paths[index].Claude)) return false;
            }
            return VerifyFile("apm.yml", states[^3]) && VerifyFile("apm.lock.yaml", states[^2]);
        }

        private bool VerifyFile(string name, PathState state)
        {
            var destination = Path.Combine(apmRoot, name);
            return state == PathState.File
                ? File.Exists(destination) && File.ReadAllBytes(destination).SequenceEqual(File.ReadAllBytes(Path.Combine(root, name)))
                : !File.Exists(destination);
        }

        private static void CopyIfExists(string source, string destination)
        {
            if (File.Exists(source)) File.Copy(source, destination);
        }

        private static void CopyDirectory(string source, string destination)
        {
            Directory.CreateDirectory(destination);
            foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
            {
                if (File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint)) throw new ProviderFailure($"APM snapshot refused nested reparse point '{directory}'.");
                Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
            }
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var target = Path.Combine(destination, Path.GetRelativePath(source, file));
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target);
            }
        }
    }
}
