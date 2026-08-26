using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skilly.Infrastructure;

namespace Skilly.State;

public sealed class SkillyState
{
    public int SchemaVersion { get; set; } = 1;

    public List<ManagementRecord> Records { get; set; } = [];

    public PendingOperation? PendingOperation { get; set; }

    public string? LastOperationNote { get; set; }
}

public sealed class ManagementRecord
{
    public required string InstallationId { get; set; }

    public required string CanonicalPath { get; set; }

    public required ProvenanceInfo Provenance { get; set; }

    public string? IntendedClaudeJunctionPath { get; set; }

    public required string InstalledRevision { get; set; }

    public required string InstalledPayloadHash { get; set; }

    public required int InstalledFileCount { get; set; }

    public required string ProviderEvidence { get; set; }

    public OperationOutcome? LastOperationOutcome { get; set; }

    public CheckSnapshot? LatestCheck { get; set; }

    [JsonIgnore]
    public string DisplayRevision => InstalledRevision.Length > 12 ? InstalledRevision[..12] : InstalledRevision;
}

public sealed class ProvenanceInfo
{
    public required string SourceProvider { get; set; }

    public required string OriginalReference { get; set; }

    public required string Host { get; set; }

    public required string Owner { get; set; }

    public required string Repository { get; set; }

    public string? RequestedPath { get; set; }

    public required string SourceSkillPath { get; set; }

    public required string TrackingRule { get; set; }

    public TrackingRuleKind TrackingRuleKind { get; set; }

    public required string ResolvedCommit { get; set; }

    public required string ProviderVersion { get; set; }
}

public sealed class PendingOperation
{
    public required string OperationId { get; set; }

    public required MutationType OperationType { get; set; }

    public List<string> AffectedInstallationIds { get; set; } = [];

    public List<string> StartingPaths { get; set; } = [];

    public List<string?> StartingHashes { get; set; } = [];

    public List<PathState> StartingPathStates { get; set; } = [];

    public string? RecoveryDirectory { get; set; }

    public List<string> TemporaryPaths { get; set; } = [];

    public PendingOperationPhase Phase { get; set; }

    public string? TargetRevision { get; set; }

    public string? TargetPayloadHash { get; set; }

    public int? TargetFileCount { get; set; }

    public string? TargetProviderEvidence { get; set; }

    public bool CancellationRequested { get; set; }

    public required DateTimeOffset StartedAt { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum MutationType
{
    Install,
    Update,
    ManagedReinstall,
    Uninstall,
    RemoveLocalFolder,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationOutcome
{
    Installed,
    Updated,
    Reinstalled,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PathState
{
    Missing,
    Directory,
    Junction,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum PendingOperationPhase
{
    Journaled,
    SnapshotReady,
    MutationStarted,
    Verified,
}

public sealed class CheckSnapshot
{
    public required UpdateStatus Status { get; set; }

    public required string InstalledRevision { get; set; }

    public DateTimeOffset? InstalledRevisionDate { get; set; }

    public string? AvailableRevision { get; set; }

    public DateTimeOffset? AvailableRevisionDate { get; set; }

    public string? AvailablePayloadHash { get; set; }

    public required DateTimeOffset CheckedAt { get; set; }

    public bool IsStale { get; set; }

    public string? Warning { get; set; }

    public string? Failure { get; set; }
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum UpdateStatus
{
    Current,
    UpdateAvailable,
    Pinned,
    SourceUnavailable,
    CheckFailed,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum TrackingRuleKind
{
    Branch,
    Tag,
    Commit,
}

public sealed class RecoveryRequiredException(string message, Exception? inner = null) : Exception(message, inner);

public sealed class StateStore(RollingLog log, string? filePath = null, Action<SkillyState>? beforeSave = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly object _gate = new();

    public string FilePath { get; } = filePath ?? SkillyPaths.StateFilePath;

    public bool RecoveryRequired { get; private set; }

    public string? RecoveryDiagnostic { get; private set; }

    public SkillyState Load()
    {
        lock (_gate)
        {
            if (!File.Exists(FilePath))
            {
                log.Info($"No authority state found at '{FilePath}'; starting empty.");
                return new SkillyState();
            }

            try
            {
                var state = Deserialize(FilePath);
                log.Info($"Loaded authority state with {state.Records.Count} management record(s).");
                return state;
            }
            catch (Exception exception)
            {
                log.Error($"Authority state at '{FilePath}' could not be loaded.", exception);
                var backupPath = FilePath + ".bak";
                try
                {
                    if (File.Exists(backupPath))
                    {
                        var backup = Deserialize(backupPath);
                        log.Info($"Loaded backup authority state with {backup.Records.Count} management record(s).");
                        return backup;
                    }
                }
                catch (Exception backupException)
                {
                    log.Error($"Backup authority state at '{backupPath}' could not be loaded.", backupException);
                }

                RecoveryRequired = true;
                RecoveryDiagnostic = "Authority state and its backup could not be loaded safely.";
                throw new RecoveryRequiredException(RecoveryDiagnostic, exception);
            }
        }
    }

    public void Save(SkillyState state)
    {
        lock (_gate)
        {
            if (RecoveryRequired)
            {
                throw new RecoveryRequiredException(RecoveryDiagnostic ?? "Skilly is read-only because recovery is required.");
            }

            beforeSave?.Invoke(state);
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tempPath = FilePath + ".tmp";
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, SerializerOptions));
            if (File.Exists(FilePath))
            {
                var backupPath = FilePath + ".bak";
                File.Replace(tempPath, FilePath, backupPath);
            }
            else
            {
                File.Move(tempPath, FilePath);
            }

            log.Info($"Saved authority state ({state.Records.Count} record(s), pending={(state.PendingOperation is not null)})");
        }
    }

    public void EnterRecoveryRequired(string diagnostic)
    {
        lock (_gate)
        {
            RecoveryRequired = true;
            RecoveryDiagnostic = diagnostic;
            log.Error(diagnostic);
        }
    }

    private static SkillyState Deserialize(string path)
    {
        var state = JsonSerializer.Deserialize<SkillyState>(File.ReadAllText(path), SerializerOptions);
        if (state is null || state.SchemaVersion != SkillyPaths.StateSchemaVersion)
        {
            throw new InvalidOperationException(
                $"State schema version {(state?.SchemaVersion.ToString() ?? "null")} is not supported.");
        }

        return state;
    }
}
