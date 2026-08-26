using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skilly.Infrastructure;

namespace Skilly.State;

public sealed class SkillyState
{
    public int SchemaVersion { get; set; } = SkillyPaths.StateSchemaVersion;

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

    public required string NormalizedSource { get; set; }

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
    Adoption,
}

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum OperationOutcome
{
    Installed,
    Updated,
    Reinstalled,
    Adopted,
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

internal sealed class UnsupportedNewerSchemaException(string message) : Exception(message);

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
                var (state, originalVersion) = Deserialize(FilePath);
                if (originalVersion < SkillyPaths.StateSchemaVersion)
                {
                    BackupBeforeMigration();
                    WriteAtomic(state, retainBackup: true);
                    log.Info($"Migrated authority state from schema {originalVersion} to {SkillyPaths.StateSchemaVersion}.");
                }
                log.Info($"Loaded authority state with {state.Records.Count} management record(s).");
                return state;
            }
            catch (UnsupportedNewerSchemaException exception)
            {
                EnterRecoveryRequired(exception.Message);
                throw new RecoveryRequiredException(exception.Message, exception);
            }
            catch (Exception exception)
            {
                log.Error($"Authority state at '{FilePath}' could not be loaded.", exception);
                var backupPath = FilePath + ".bak";
                try
                {
                    if (File.Exists(backupPath))
                    {
                        var (backup, _) = Deserialize(backupPath);
                        WriteAtomic(backup, retainBackup: false);
                        log.Info($"Loaded backup authority state with {backup.Records.Count} management record(s).");
                        return backup;
                    }
                }
                catch (UnsupportedNewerSchemaException backupException)
                {
                    log.Error($"Backup authority state at '{backupPath}' uses an unsupported newer schema.", backupException);
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

            if (state.SchemaVersion != SkillyPaths.StateSchemaVersion)
            {
                throw new InvalidOperationException($"Cannot save authority state schema {state.SchemaVersion}.");
            }

            beforeSave?.Invoke(state);
            WriteAtomic(state, retainBackup: true);

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

    private (SkillyState State, int OriginalVersion) Deserialize(string path)
    {
        var json = File.ReadAllText(path);
        using var document = JsonDocument.Parse(json);
        if (!document.RootElement.TryGetProperty("schemaVersion", out var schemaElement)
            || !schemaElement.TryGetInt32(out var schemaVersion))
        {
            throw new InvalidOperationException("Authority state has no valid integer schema version.");
        }

        if (schemaVersion > SkillyPaths.StateSchemaVersion)
        {
            throw new UnsupportedNewerSchemaException(
                $"Authority state schema {schemaVersion} is newer than supported schema {SkillyPaths.StateSchemaVersion}; Skilly is read-only.");
        }
        if (schemaVersion < 1)
        {
            throw new InvalidOperationException($"Authority state schema {schemaVersion} cannot be migrated safely.");
        }

        if (schemaVersion == 1)
        {
            var node = System.Text.Json.Nodes.JsonNode.Parse(json)?.AsObject()
                       ?? throw new InvalidOperationException("Authority state is empty.");
            if (node["records"] is System.Text.Json.Nodes.JsonArray records)
            {
                foreach (var record in records.OfType<System.Text.Json.Nodes.JsonObject>())
                {
                    if (record["provenance"] is not System.Text.Json.Nodes.JsonObject provenance)
                    {
                        continue;
                    }
                    var host = provenance["host"]?.GetValue<string>() ?? string.Empty;
                    var owner = provenance["owner"]?.GetValue<string>() ?? string.Empty;
                    var repository = provenance["repository"]?.GetValue<string>() ?? string.Empty;
                    provenance["normalizedSource"] = NormalizeSource(host, owner, repository);
                }
            }
            node["schemaVersion"] = SkillyPaths.StateSchemaVersion;
            json = node.ToJsonString(SerializerOptions);
        }

        var state = JsonSerializer.Deserialize<SkillyState>(json, SerializerOptions);
        if (state is null || state.Records is null)
        {
            throw new InvalidOperationException("Authority state is incomplete.");
        }
        return (state, schemaVersion);
    }

    private void BackupBeforeMigration()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        File.Copy(FilePath, FilePath + ".bak", overwrite: true);
    }

    private void WriteAtomic(SkillyState state, bool retainBackup)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
        var tempPath = FilePath + ".tmp";
        try
        {
            File.WriteAllText(tempPath, JsonSerializer.Serialize(state, SerializerOptions));
            if (File.Exists(FilePath))
            {
                File.Replace(tempPath, FilePath, retainBackup ? FilePath + ".bak" : null);
            }
            else
            {
                File.Move(tempPath, FilePath);
            }
        }
        finally
        {
            if (File.Exists(tempPath))
            {
                File.Delete(tempPath);
            }
        }
    }

    private static string NormalizeSource(string host, string owner, string repository)
        => $"{host}/{owner}/{repository}".ToLowerInvariant();
}
