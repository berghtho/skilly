using System.IO;
using System.Text.Json;
using Skilly.Providers.GitHub;

namespace Skilly.Providers.SkillsCli;

public sealed record SkillsCliSourceSkill(string Name, string Description)
{
    public string FileCount => "Not reported";

    public string SkillPath => Name;

    public string FolderName => SkillsCliClient.SanitizeName(Name);

    public string DeclaredName => Name;

    public bool MetadataValid => FolderName.Length > 0;

    public string? MetadataError => MetadataValid ? null : "The provider returned an invalid Skill name.";

    public bool MatchesAlias(string candidate) => string.Equals(candidate, Name, StringComparison.Ordinal);
}

public sealed record SkillsCliInspection(
    string OriginalReference,
    string NormalizedSource,
    string ProviderVersion,
    IReadOnlyList<SkillsCliSourceSkill> Skills)
{
    public string RequestedTrackingRule => "Provider default";

    public string ResolvedRevision => ProviderVersion;
}

public sealed record SkillsCliLockEntry(
    string Name,
    string Source,
    string? SourceUrl,
    string SourceType,
    string? Ref,
    string? SkillPath,
    string SkillFolderHash,
    DateTimeOffset? InstalledAt,
    DateTimeOffset? UpdatedAt)
{
    public string NormalizedSource => (SourceUrl ?? Source).Trim().Replace('\\', '/');

    public string TrackingRule => string.IsNullOrWhiteSpace(Ref) ? "default" : Ref;

    public State.TrackingRuleKind TrackingRuleKind => string.IsNullOrWhiteSpace(Ref)
        ? State.TrackingRuleKind.Branch
        : IsCommit(Ref) ? State.TrackingRuleKind.Commit : State.TrackingRuleKind.Tag;

    public string SourceSkillPath => string.IsNullOrWhiteSpace(SkillPath)
        ? Name
        : SkillPath.Replace('\\', '/').TrimEnd('/').EndsWith("/SKILL.md", StringComparison.Ordinal)
            ? SkillPath.Replace('\\', '/')[..^"/SKILL.md".Length]
            : SkillPath.Replace('\\', '/');

    public string Evidence => $"skills@{SkillsCliClient.Version}:{Name}:{SkillFolderHash}";

    private static bool IsCommit(string value)
        => value.Length == 40 && value.All(static character => Uri.IsHexDigit(character));
}

public sealed class SkillsCliLock(string path)
{
    public string Path { get; } = path;

    public IReadOnlyDictionary<string, SkillsCliLockEntry> Read()
    {
        if (!File.Exists(Path))
        {
            return new Dictionary<string, SkillsCliLockEntry>(StringComparer.OrdinalIgnoreCase);
        }

        using var document = JsonDocument.Parse(File.ReadAllText(Path));
        var root = document.RootElement;
        if (!root.TryGetProperty("version", out var version) || version.GetInt32() != 3
            || !root.TryGetProperty("skills", out var skills) || skills.ValueKind != JsonValueKind.Object)
        {
            throw new ProviderFailure("The skills provider lock is missing or does not use the expected schema version 3.");
        }

        var entries = new Dictionary<string, SkillsCliLockEntry>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in skills.EnumerateObject())
        {
            var value = property.Value;
            var source = RequiredString(value, "source", property.Name);
            var sourceType = RequiredString(value, "sourceType", property.Name);
            var hash = RequiredString(value, "skillFolderHash", property.Name);
            entries[property.Name] = new SkillsCliLockEntry(
                property.Name,
                source,
                OptionalString(value, "sourceUrl"),
                sourceType,
                OptionalString(value, "ref"),
                OptionalString(value, "skillPath"),
                hash,
                OptionalDate(value, "installedAt"),
                OptionalDate(value, "updatedAt"));
        }
        return entries;
    }

    private static string RequiredString(JsonElement value, string property, string skill)
    {
        var result = OptionalString(value, property);
        return string.IsNullOrWhiteSpace(result)
            ? throw new ProviderFailure($"The skills provider lock entry for '{skill}' has no valid {property} evidence.")
            : result;
    }

    private static string? OptionalString(JsonElement value, string property)
        => value.TryGetProperty(property, out var element) && element.ValueKind == JsonValueKind.String
            ? element.GetString()
            : null;

    private static DateTimeOffset? OptionalDate(JsonElement value, string property)
        => DateTimeOffset.TryParse(OptionalString(value, property), out var parsed) ? parsed : null;
}

public sealed record SkillsCliInstalledSkill(string SkillPath, string CanonicalPath);

public sealed record SkillsCliInstallResult(IReadOnlyList<SkillsCliInstalledSkill> InstalledSkills)
{
    public int SucceededCount => InstalledSkills.Count;
}

public sealed record SkillsCliUpdateResult(string InstallationId, string InstalledRevision);
