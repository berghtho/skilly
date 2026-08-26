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

    public string? LastOperationOutcome { get; set; }

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

    public required string RequestedRef { get; set; }

    public string? RequestedPath { get; set; }

    public required string SourceSkillPath { get; set; }

    public required string TrackingRule { get; set; }

    public required string ResolvedCommit { get; set; }
}

public sealed class PendingOperation
{
    public required string OperationType { get; set; }

    public List<string> AffectedInstallationIds { get; set; } = [];

    public List<string> StartingPaths { get; set; } = [];

    public required DateTimeOffset StartedAt { get; set; }
}

public sealed class StateStore(RollingLog log, string? filePath = null)
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
                var state = JsonSerializer.Deserialize<SkillyState>(File.ReadAllText(FilePath), SerializerOptions);
                if (state is null || state.SchemaVersion != SkillyPaths.StateSchemaVersion)
                {
                    throw new InvalidOperationException(
                        $"State schema version {(state?.SchemaVersion.ToString() ?? "null")} is not supported.");
                }

                log.Info($"Loaded authority state with {state.Records.Count} management record(s).");
                return state;
            }
            catch (Exception exception)
            {
                log.Error($"Authority state at '{FilePath}' could not be loaded.", exception);
                throw new InvalidDataException(
                    "The Skilly authority state could not be loaded. Recovery handling will arrive with lifecycle recovery.",
                    exception);
            }
        }
    }

    public void Save(SkillyState state)
    {
        lock (_gate)
        {
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
}
