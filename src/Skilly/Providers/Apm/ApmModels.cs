using System.IO;
using System.Security.Cryptography;
using System.Text;
using YamlDotNet.Serialization;

namespace Skilly.Providers.Apm;

public sealed record ApmSourceSkill(string Name, string Description, string? ProviderSelectionName = null)
{
    public string FileCount => "Not reported";
    public string SkillPath => Name;
    public string FolderName => Name;
    public string DeclaredName => Name;
    public bool MetadataValid => IsSafeName(Name);
    public string? MetadataError => MetadataValid ? null : "APM produced an unsafe canonical Skill folder name.";
    public bool MatchesAlias(string candidate) => string.Equals(candidate, Name, StringComparison.Ordinal);

    private static bool IsSafeName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name is not "." and not ".."
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && !name.Contains('/') && !name.Contains('\\');
}

public sealed record ApmInspection(
    string OriginalReference,
    string NormalizedSource,
    string ProviderVersion,
    IReadOnlyList<ApmSourceSkill> Skills)
{
    public string RequestedTrackingRule => "APM manifest rule";
    public string ResolvedRevision => $"apm-cli {ProviderVersion}";
}

public sealed record ApmInstalledSkill(string SkillPath, string CanonicalPath);

public sealed record ApmInstallResult(IReadOnlyList<ApmInstalledSkill> InstalledSkills)
{
    public int SucceededCount => InstalledSkills.Count;
}

public sealed record ApmUpdateResult(string InstallationId, string InstalledRevision);

public sealed record ApmOutdatedRow(string Package, string Current, string Latest, string Status);

public sealed record ApmDependencyEvidence(
    string Identity,
    string RepositoryUrl,
    string? ResolvedRef,
    string? ResolvedCommit,
    string? ResolvedHash,
    string? ContentHash,
    string PackageType,
    IReadOnlyList<string> SkillSubset,
    IReadOnlyList<string> DeployedFiles,
    IReadOnlyDictionary<string, string> DeployedFileHashes,
    string ManifestHash,
    string LockHash)
{
    public string Revision => ResolvedCommit ?? ResolvedHash ?? ContentHash
        ?? throw new ProviderFailure($"APM lock dependency '{Identity}' has no immutable revision evidence.");

    public string TrackingRule => ResolvedRef ?? "default";

    public State.TrackingRuleKind TrackingRuleKind
        => string.IsNullOrWhiteSpace(ResolvedRef) ? State.TrackingRuleKind.Branch
            : ResolvedRef.Length == 40 && ResolvedRef.All(Uri.IsHexDigit) ? State.TrackingRuleKind.Commit
            : System.Text.RegularExpressions.Regex.IsMatch(ResolvedRef, @"^(?:v)?\d+\.\d+\.\d+", System.Text.RegularExpressions.RegexOptions.CultureInvariant)
                ? State.TrackingRuleKind.Tag
                : State.TrackingRuleKind.Branch;

    public string Evidence => $"microsoft/apm:{Identity}:{Revision}:{ManifestHash}:{LockHash}";
}

public sealed class ApmGlobalState(string home)
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder().Build();

    public string RootPath { get; } = Path.Combine(home, ".apm");
    public string ManifestPath => Path.Combine(RootPath, "apm.yml");
    public string LockPath => Path.Combine(RootPath, "apm.lock.yaml");
    public string ModulesPath => Path.Combine(RootPath, "apm_modules");

    public IReadOnlyList<ApmDependencyEvidence> Read()
    {
        if (!File.Exists(ManifestPath) || !File.Exists(LockPath))
        {
            throw new ProviderFailure("APM global manifest and lock evidence are both required.");
        }

        Dictionary<object, object?> manifest;
        Dictionary<object, object?> root;
        try
        {
            manifest = Deserializer.Deserialize<Dictionary<object, object?>>(File.ReadAllText(ManifestPath))
                       ?? throw new InvalidDataException("manifest is empty");
            root = Deserializer.Deserialize<Dictionary<object, object?>>(File.ReadAllText(LockPath))
                   ?? throw new InvalidDataException("lockfile is empty");
        }
        catch (Exception exception)
        {
            throw new ProviderFailure($"APM global YAML evidence is invalid: {exception.Message}");
        }

        var lockVersion = Scalar(root, "lockfile_version");
        if (lockVersion is not "1" and not "2")
        {
            throw new ProviderFailure($"APM lockfile version '{lockVersion ?? "missing"}' is unsupported.");
        }
        var manifestText = Flatten(manifest);
        var manifestSources = ManifestSources(manifest);
        var manifestHash = HashFile(ManifestPath);
        var lockHash = HashFile(LockPath);
        if (Value(root, "dependencies") is not IEnumerable<object> dependencies)
        {
            throw new ProviderFailure("APM lockfile has no dependency list.");
        }

        var result = new List<ApmDependencyEvidence>();
        foreach (var item in dependencies)
        {
            if (item is not Dictionary<object, object?> dependency)
            {
                throw new ProviderFailure("APM lockfile contains a malformed dependency entry.");
            }
            var repository = RequiredScalar(dependency, "repo_url");
            var identity = Identity(repository, Scalar(dependency, "host"), Scalar(dependency, "virtual_path"));
            var subset = Strings(dependency, "skill_subset");
            if (!manifestSources.Any(value => Comparable(value).Contains(Comparable(repository), StringComparison.OrdinalIgnoreCase)
                                              || Comparable(repository).Contains(Comparable(value), StringComparison.OrdinalIgnoreCase)))
            {
                throw new ProviderFailure($"APM manifest does not declare lock dependency '{identity}'.");
            }
            if (subset.Count > 0 && subset.Any(skill => !manifestText.Contains(skill, StringComparer.Ordinal)))
            {
                throw new ProviderFailure($"APM manifest does not preserve the lockfile Skill subset for '{identity}'.");
            }
            result.Add(new ApmDependencyEvidence(
                identity,
                repository,
                Scalar(dependency, "resolved_ref"),
                Scalar(dependency, "resolved_commit"),
                Scalar(dependency, "resolved_hash"),
                Scalar(dependency, "content_hash"),
                Scalar(dependency, "package_type") ?? string.Empty,
                subset,
                Strings(dependency, "deployed_files"),
                StringMap(dependency, "deployed_file_hashes"),
                manifestHash,
                lockHash));
        }
        return result;
    }

    public ApmDependencyEvidence FindForSkill(string folderName)
    {
        var prefix = $".agents/skills/{folderName}/";
        var matches = Read().Where(dependency => dependency.DeployedFiles.Any(file =>
            NormalizePath(file).StartsWith(prefix, StringComparison.OrdinalIgnoreCase))).ToList();
        return matches.Count == 1
            ? matches[0]
            : throw new ProviderFailure($"APM lock evidence did not identify exactly one owner for canonical Skill '{folderName}'.");
    }

    public bool ContainsIdentity(string identity)
        => File.Exists(ManifestPath) && File.Exists(LockPath)
           && Read().Any(dependency => string.Equals(dependency.Identity, identity, StringComparison.OrdinalIgnoreCase));

    public bool ManifestDeclaresIdentity(string identity)
    {
        if (!File.Exists(ManifestPath)) return false;
        try
        {
            var manifest = Deserializer.Deserialize<Dictionary<object, object?>>(File.ReadAllText(ManifestPath));
            var normalized = Comparable(identity);
            return manifest is not null && ManifestSources(manifest).Any(value =>
            {
                var candidate = Comparable(value);
                return candidate.Contains(normalized, StringComparison.OrdinalIgnoreCase)
                       || normalized.Contains(candidate, StringComparison.OrdinalIgnoreCase);
            });
        }
        catch (Exception exception)
        {
            throw new ProviderFailure($"APM global manifest evidence is invalid: {exception.Message}");
        }
    }

    public static string NormalizeSource(string source)
        => source.Trim().Replace('\\', '/');

    private static object? Value(Dictionary<object, object?> value, string key)
        => value.FirstOrDefault(pair => string.Equals(pair.Key?.ToString(), key, StringComparison.Ordinal)).Value;

    private static string? Scalar(Dictionary<object, object?> value, string key) => Value(value, key)?.ToString();

    private static string RequiredScalar(Dictionary<object, object?> value, string key)
        => Scalar(value, key) is { Length: > 0 } result
            ? result
            : throw new ProviderFailure($"APM lock dependency has no '{key}' evidence.");

    private static IReadOnlyList<string> Strings(Dictionary<object, object?> value, string key)
        => Value(value, key) is IEnumerable<object> items
            ? items.Select(item => item?.ToString() ?? string.Empty).Where(item => item.Length > 0).ToList()
            : [];

    private static IReadOnlyDictionary<string, string> StringMap(Dictionary<object, object?> value, string key)
        => Value(value, key) is Dictionary<object, object?> map
            ? map.ToDictionary(pair => pair.Key.ToString()!, pair => pair.Value?.ToString() ?? string.Empty, StringComparer.Ordinal)
            : new Dictionary<string, string>(StringComparer.Ordinal);

    private static IReadOnlyList<string> Flatten(object? value)
    {
        var result = new List<string>();
        Add(value, result);
        return result;
    }

    private static void Add(object? value, List<string> result)
    {
        switch (value)
        {
            case Dictionary<object, object?> map:
                foreach (var pair in map)
                {
                    Add(pair.Key, result);
                    Add(pair.Value, result);
                }
                break;
            case IEnumerable<object> values:
                foreach (var item in values) Add(item, result);
                break;
            case not null:
                result.Add(value.ToString()!);
                break;
        }
    }

    private static void AddValues(object? value, List<string> result)
    {
        switch (value)
        {
            case Dictionary<object, object?> map:
                foreach (var pair in map) AddValues(pair.Value, result);
                break;
            case IEnumerable<object> values:
                foreach (var item in values) AddValues(item, result);
                break;
            case not null:
                result.Add(value.ToString()!);
                break;
        }
    }

    private static IReadOnlyList<string> ManifestSources(Dictionary<object, object?> manifest)
    {
        var result = new List<string>();
        foreach (var sectionName in new[] { "dependencies", "devDependencies" })
        {
            if (Value(manifest, sectionName) is not Dictionary<object, object?> section
                || Value(section, "apm") is not IEnumerable<object> items) continue;
            foreach (var item in items)
            {
                if (item is string scalar)
                {
                    result.Add(scalar);
                    continue;
                }
                if (item is not Dictionary<object, object?> entry) continue;
                foreach (var pair in entry)
                {
                    var key = pair.Key?.ToString();
                    if (key is "git" or "repo" or "url" or "id" or "registry" or "local") AddValues(pair.Value, result);
                }
            }
        }
        return result;
    }

    private static string Identity(string repository, string? host, string? virtualPath)
    {
        var normalized = Comparable(repository);
        if (!string.IsNullOrWhiteSpace(host) && !host.Equals("github.com", StringComparison.OrdinalIgnoreCase)
            && !normalized.StartsWith(host + "/", StringComparison.OrdinalIgnoreCase))
        {
            normalized = host.ToLowerInvariant() + "/" + normalized;
        }
        if (!string.IsNullOrWhiteSpace(virtualPath)) normalized += "/" + virtualPath.Trim('/');
        return normalized;
    }

    private static string Comparable(string value)
    {
        var normalized = value.Trim().Replace('\\', '/').TrimEnd('/');
        if (Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            normalized = uri.Host + uri.AbsolutePath;
        }
        if (normalized.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase)) normalized = normalized["github.com/".Length..];
        if (normalized.EndsWith(".git", StringComparison.OrdinalIgnoreCase)) normalized = normalized[..^4];
        return normalized.Trim('/').ToLowerInvariant();
    }

    private static string NormalizePath(string value) => value.Replace('\\', '/').TrimStart('/');
    private static string HashFile(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
