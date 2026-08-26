namespace Skilly.Skills;

public sealed record AdoptionEvidence(
    State.ManagementRecord ProposedRecord,
    string ExpectedPayloadHash,
    int ExpectedFileCount,
    string ExpectedContentIdentity);

public enum ManagementStatus
{
    Managed,
    VerifiedAdoptionAvailable,
    Unmanaged,
}

public enum InstallationHealth
{
    Healthy,
    LocallyModified,
    Missing,
    ExposureProblem,
    InvalidMetadata,
    Collision,
}

public enum EntryKind
{
    RealFolder,
    LinkEntry,
}

public enum ExposureState
{
    Canonical,
    Direct,
    VerifiedJunction,
    MissingJunction,
    SeparateCopy,
    ForeignLink,
    BrokenLink,
    None,
}

public sealed record HarnessExposure(ExposureState State, string Detail)
{
    public static HarnessExposure Canonical() => new(ExposureState.Canonical, "Consumes the canonical installation");

    public static HarnessExposure Direct() => new(ExposureState.Direct, "Discovers this root directly");

    public static HarnessExposure None() => new(ExposureState.None, "No documented global discovery");

    public string Display => State switch
    {
        ExposureState.Canonical => "Canonical",
        ExposureState.Direct => "Direct",
        ExposureState.VerifiedJunction => "Verified junction",
        ExposureState.MissingJunction => "Missing",
        ExposureState.SeparateCopy => "Separate copy",
        ExposureState.ForeignLink => "Foreign link",
        ExposureState.BrokenLink => "Broken link",
        ExposureState.None => "None",
        _ => State.ToString(),
    };
}

public sealed class InventoryEntry
{
    public required string FolderName { get; init; }

    public required string LocalPath { get; init; }

    public required RootKind RootKind { get; init; }

    public required EntryKind Kind { get; init; }

    public string? LinkTargetPath { get; init; }

    public required ManagementStatus ManagementStatus { get; init; }

    public required InstallationHealth Health { get; init; }

    public string? HealthDetail { get; init; }

    public required SkillMetadata Metadata { get; init; }

    public required IReadOnlyDictionary<Harness, HarnessExposure> Exposures { get; init; }

    public State.ManagementRecord? ManagementRecord { get; init; }

    public AdoptionEvidence? AdoptionEvidence { get; init; }

    public bool NeedsAttention =>
        Health is InstallationHealth.LocallyModified
            or InstallationHealth.InvalidMetadata
            or InstallationHealth.ExposureProblem
            or InstallationHealth.Collision;

    public string DisplayName => Metadata.Status == MetadataReadStatus.Valid ? Metadata.DeclaredName ?? FolderName : "(invalid)";
}
