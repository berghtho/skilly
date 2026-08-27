using System.IO;
using Skilly.Infrastructure;
using Skilly.Providers;
using Skilly.Providers.Apm;
using Skilly.Providers.SkillsCli;

namespace Skilly.Skills;

public sealed record InventorySnapshot(IReadOnlyList<InventoryEntry> Entries, DateTimeOffset ScannedAt)
{
    public int HealthyCount { get; } = Entries.Count(static entry => entry.Health == InstallationHealth.Healthy);

    public int AttentionCount { get; } = Entries.Count(static entry => entry.NeedsAttention);

    public int UnmanagedCount { get; } = Entries.Count(static entry => entry.ManagementStatus == ManagementStatus.Unmanaged);
}

public sealed class InventoryScanner
{
    public InventorySnapshot Scan(
        string home,
        State.SkillyState? state = null,
        IReadOnlyList<AdoptionEvidence>? adoptionEvidence = null)
    {
        var recordsByPath = (state?.Records ?? [])
            .GroupBy(static record => record.CanonicalPath.TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);
        var adoptionByPath = (adoptionEvidence ?? [])
            .GroupBy(evidence => NormalizePath(evidence.ProposedRecord.CanonicalPath), StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var observations = DiscoverProviderAttributions(home).ToList();
        var attributionByPath = observations
            .GroupBy(static item => NormalizePath(item.Path), StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single().Attribution, StringComparer.OrdinalIgnoreCase);
        var automaticAdoptionByPath = observations.Where(static item => item.AdoptionEvidence is not null)
            .GroupBy(static item => NormalizePath(item.Path), StringComparer.OrdinalIgnoreCase)
            .Where(static group => group.Count() == 1)
            .ToDictionary(static group => group.Key, static group => group.Single().AdoptionEvidence!, StringComparer.OrdinalIgnoreCase);

        var roots = new[]
        {
            HarnessRoot.Create(RootKind.CanonicalAgents, home),
            HarnessRoot.Create(RootKind.ClaudeSkills, home),
            HarnessRoot.Create(RootKind.CopilotSkills, home),
            HarnessRoot.Create(RootKind.OpenCodeConfigSkills, home),
            HarnessRoot.Create(RootKind.CodexLegacySkills, home),
        };

        var candidates = new List<Candidate>();
        foreach (var root in roots)
        {
            candidates.AddRange(EnumerateRoot(root));
        }

        var realFolders = candidates.Where(candidate => candidate.Kind == EntryKind.RealFolder).ToList();
        var links = candidates.Where(candidate => candidate.Kind == EntryKind.LinkEntry).ToList();

        var canonicalByPath = realFolders
            .Where(candidate => candidate.Root.Kind == RootKind.CanonicalAgents)
            .ToDictionary(
                candidate => NormalizePath(candidate.FileSystemInfo.FullName),
                static candidate => candidate,
                StringComparer.OrdinalIgnoreCase);

        var duplicateNames = realFolders
            .GroupBy(candidate => candidate.FolderName, StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Select(candidate => candidate.Root.Kind).Distinct().Count() > 1)
            .SelectMany(group => group)
            .ToHashSet();

        var entries = new List<InventoryEntry>();
        AttachClaudeExposures(links, canonicalByPath);

        foreach (var candidate in realFolders)
        {
            var record = recordsByPath.TryGetValue(NormalizePath(candidate.FileSystemInfo.FullName), out var matched)
                ? matched
                : null;
            var evidence = adoptionByPath.TryGetValue(NormalizePath(candidate.FileSystemInfo.FullName), out var verified)
                ? verified
                : automaticAdoptionByPath.TryGetValue(NormalizePath(candidate.FileSystemInfo.FullName), out var automatic)
                    ? automatic
                    : null;
            var attribution = attributionByPath.TryGetValue(NormalizePath(candidate.FileSystemInfo.FullName), out var observed)
                ? observed
                : null;
            entries.Add(BuildFolderEntry(candidate, duplicateNames.Contains(candidate), record, evidence, attribution));
        }

        foreach (var link in links.Where(link => !link.ConsumedAsExposure))
        {
            entries.Add(BuildLinkEntry(link));
        }

        return new InventorySnapshot(entries.OrderBy(entry => entry.FolderName, StringComparer.OrdinalIgnoreCase).ToList(), DateTimeOffset.Now);
    }

    private List<Candidate> EnumerateRoot(HarnessRoot root)
    {
        var found = new List<Candidate>();
        if (!Directory.Exists(root.FullPath))
        {
            return found;
        }

        var enumerationOptions = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            AttributesToSkip = FileAttributes.Hidden | FileAttributes.System,
            ReturnSpecialDirectories = false,
        };

        foreach (var directory in new DirectoryInfo(root.FullPath).EnumerateDirectories("*", enumerationOptions))
        {
            var isLink = directory.Attributes.HasFlag(FileAttributes.ReparsePoint);
            found.Add(new Candidate(root, directory.Name, directory, isLink ? EntryKind.LinkEntry : EntryKind.RealFolder));
        }

        return found;
    }

    private void AttachClaudeExposures(List<Candidate> links, Dictionary<string, Candidate> canonicalByPath)
    {
        foreach (var canonical in canonicalByPath.Values)
        {
            canonical.ClaudeExposure = AnalyzeClaudeExposure(canonical, links, canonicalByPath);
        }
    }

    private HarnessExposure AnalyzeClaudeExposure(
        Candidate canonical,
        List<Candidate> links,
        Dictionary<string, Candidate> canonicalByPath)
    {
        var expected = Path.Combine(
            HarnessRoot.RelativePath(RootKind.ClaudeSkills),
            canonical.FolderName);
        var link = links.FirstOrDefault(candidate =>
            candidate.Root.Kind == RootKind.ClaudeSkills
            && string.Equals(candidate.FolderName, canonical.FolderName, StringComparison.OrdinalIgnoreCase));

        if (link is null)
        {
            return new HarnessExposure(ExposureState.MissingJunction, $"No entry at %USERPROFILE%\\{NormalizeRelative(expected)}");
        }

        var resolved = ResolveFinalTarget(link);
        if (resolved is null)
        {
            return new HarnessExposure(ExposureState.BrokenLink, $"The Claude entry exists but its target could not be resolved ({link.FileSystemInfo.FullName})");
        }

        if (string.Equals(
                NormalizePath(resolved),
                NormalizePath(canonical.FileSystemInfo.FullName),
                StringComparison.OrdinalIgnoreCase))
        {
            link.ConsumedAsExposure = true;
            return new HarnessExposure(ExposureState.VerifiedJunction, $"Junction at {link.FileSystemInfo.FullName} resolves to the canonical installation");
        }

        return new HarnessExposure(ExposureState.SeparateCopy, $"{link.FileSystemInfo.FullName} exists but does not resolve to the canonical installation");
    }

    private InventoryEntry BuildFolderEntry(
        Candidate candidate,
        bool isDuplicate,
        State.ManagementRecord? record,
        AdoptionEvidence? adoptionEvidence,
        ProviderAttribution? providerAttribution)
    {
        var metadata = SkillMdReader.Read(candidate.FileSystemInfo.FullName, candidate.FolderName);
        var health = metadata.Status == MetadataReadStatus.Valid ? InstallationHealth.Healthy : InstallationHealth.InvalidMetadata;
        string? detail = metadata.Error;
        var exposures = BuildExposuresForFolder(candidate);

        if (record is not null && health == InstallationHealth.Healthy)
        {
            var currentHash = PayloadHasher.HashFolder(candidate.FileSystemInfo.FullName);
            if (!string.Equals(currentHash, record.InstalledPayloadHash, StringComparison.OrdinalIgnoreCase))
            {
                health = InstallationHealth.LocallyModified;
                detail = "Installed content differs from the payload recorded by Skilly.";
            }
            else if (candidate.ClaudeExposure?.State != ExposureState.VerifiedJunction)
            {
                health = candidate.ClaudeExposure?.State == ExposureState.MissingJunction
                    ? InstallationHealth.ExposureProblem
                    : InstallationHealth.Collision;
                detail = candidate.ClaudeExposure?.Detail ?? "The intended Claude junction is missing.";
            }
        }

        if (isDuplicate)
        {
            health = InstallationHealth.Collision;
            detail = $"A real folder with the same name exists under more than one global root. This copy is a separate mutable installation.{(detail is null ? string.Empty : " " + detail)}";
        }

        if (candidate.Root.Kind == RootKind.CodexLegacySkills && health == InstallationHealth.Healthy)
        {
            detail = "Located under the deprecated Codex compatibility root.";
        }

        if (candidate.ClaudeExposure is { } claude)
        {
            exposures[Harness.ClaudeCode] = claude;
        }

        var verifiedAdoption = record is null
                               && adoptionEvidence is not null
                               && candidate.Root.Kind == RootKind.CanonicalAgents
                               && !isDuplicate
                               && health == InstallationHealth.Healthy
                               && candidate.ClaudeExposure?.State is ExposureState.MissingJunction or ExposureState.VerifiedJunction
                               && IsCompleteEvidence(adoptionEvidence, candidate)
                               && string.Equals(
                                   PayloadHasher.HashFolder(candidate.FileSystemInfo.FullName),
                                   adoptionEvidence.ExpectedPayloadHash,
                                   StringComparison.OrdinalIgnoreCase)
                               && Directory.EnumerateFiles(candidate.FileSystemInfo.FullName, "*", SearchOption.AllDirectories).Count()
                                   == adoptionEvidence.ExpectedFileCount;

        return new InventoryEntry
        {
            FolderName = candidate.FolderName,
            LocalPath = candidate.FileSystemInfo.FullName,
            RootKind = candidate.Root.Kind,
            Kind = EntryKind.RealFolder,
            ManagementStatus = record is not null
                ? ManagementStatus.Managed
                : verifiedAdoption
                    ? ManagementStatus.VerifiedAdoptionAvailable
                    : ManagementStatus.Unmanaged,
            Health = health,
            HealthDetail = detail,
            Metadata = metadata,
            Exposures = exposures,
            ManagementRecord = record,
            AdoptionEvidence = verifiedAdoption ? adoptionEvidence : null,
            ProviderAttribution = record is null ? providerAttribution : null,
        };
    }

    private static IEnumerable<ProviderObservation> DiscoverProviderAttributions(string home)
    {
        var canonicalRoot = Path.Combine(home, ".agents", "skills");
        var skillsLockPath = Path.Combine(home, ".agents", ".skill-lock.json");
        if (File.Exists(skillsLockPath))
        {
            IReadOnlyDictionary<string, SkillsCliLockEntry> entries;
            try { entries = new SkillsCliLock(skillsLockPath).Read(); }
            catch (ProviderFailure) { entries = new Dictionary<string, SkillsCliLockEntry>(); }
            foreach (var entry in entries.Values.Where(entry => IsSafeFolderName(entry.Name)))
            {
                var path = Path.Combine(canonicalRoot, entry.Name);
                var attribution = new ProviderAttribution(
                        "skills",
                        SkillsCliClient.Version,
                        SensitiveDataRedactor.Redact(entry.SourceUrl ?? entry.Source),
                        SensitiveDataRedactor.Redact(entry.Source),
                        entry.SourceSkillPath,
                        entry.TrackingRule,
                        entry.TrackingRuleKind);
                AdoptionEvidence? adoption = null;
                try { adoption = CreateSkillsAdoptionEvidence(home, path, entry); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                yield return new ProviderObservation(path, attribution, adoption);
            }
        }

        var apm = new ApmGlobalState(home);
        if (!File.Exists(apm.ManifestPath) || !File.Exists(apm.LockPath)) yield break;
        IReadOnlyList<ApmDependencyEvidence> dependencies;
        try { dependencies = apm.Read(); }
        catch (ProviderFailure) { yield break; }
        foreach (var dependency in dependencies)
        {
            foreach (var folderName in dependency.DeployedFiles.Select(DeployedSkillFolder).Where(static name => name is not null).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var path = Path.Combine(canonicalRoot, folderName!);
                var attribution = new ProviderAttribution(
                        ApmClient.ProviderId,
                        dependency.ProviderVersion,
                        SensitiveDataRedactor.Redact(dependency.RepositoryUrl),
                        SensitiveDataRedactor.Redact(dependency.Identity),
                        folderName!,
                        dependency.TrackingRule,
                        dependency.TrackingRuleKind);
                AdoptionEvidence? adoption = null;
                try { adoption = CreateApmAdoptionEvidence(home, path, folderName!, dependency); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                yield return new ProviderObservation(path, attribution, adoption);
            }
        }
    }

    private static AdoptionEvidence? CreateSkillsAdoptionEvidence(string home, string path, SkillsCliLockEntry entry)
    {
        if (!Directory.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)
            || !IsCredentialFreeSource(entry.SourceUrl ?? entry.Source)
            || entry.SkillFolderHash.Length != 40 || !entry.SkillFolderHash.All(Uri.IsHexDigit)
            || !string.Equals(GitTreeHasher.HashFolder(path), entry.SkillFolderHash, StringComparison.OrdinalIgnoreCase)) return null;
        var payloadHash = PayloadHasher.HashFolder(path);
        var fileCount = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
        var record = new State.ManagementRecord
        {
            InstallationId = Guid.NewGuid().ToString("N"),
            CanonicalPath = Path.GetFullPath(path),
            Provenance = new State.ProvenanceInfo
            {
                SourceProvider = "skills",
                OriginalReference = entry.SourceUrl ?? entry.Source,
                NormalizedSource = entry.NormalizedSource,
                Host = Uri.TryCreate(entry.SourceUrl, UriKind.Absolute, out var uri) ? uri.Host : string.Empty,
                Owner = string.Empty,
                Repository = entry.Source,
                SourceSkillPath = entry.SourceSkillPath,
                TrackingRule = entry.TrackingRule,
                TrackingRuleKind = entry.TrackingRuleKind,
                ResolvedCommit = entry.SkillFolderHash,
                SelectedContentIdentity = entry.SkillFolderHash,
                ProviderVersion = SkillsCliClient.Version,
                ProviderSkillName = entry.Name,
            },
            IntendedClaudeJunctionPath = Path.Combine(HarnessRoot.Create(RootKind.ClaudeSkills, home).FullPath, entry.Name),
            InstalledRevision = entry.SkillFolderHash,
            InstalledPayloadHash = payloadHash,
            InstalledFileCount = fileCount,
            ProviderEvidence = entry.Evidence,
        };
        return new AdoptionEvidence(record, payloadHash, fileCount, entry.SkillFolderHash);
    }

    private static AdoptionEvidence? CreateApmAdoptionEvidence(string home, string path, string folderName, ApmDependencyEvidence dependency)
    {
        if (!Directory.Exists(path) || File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) || !IsCredentialFreeSource(dependency.RepositoryUrl)) return null;
        var deployedFiles = dependency.DeployedFiles.Select(file => (Relative: file, FullPath: ResolveDeployedFile(home, path, file)))
            .Where(item => item.FullPath is not null).ToList();
        var actualFiles = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Select(Path.GetFullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (deployedFiles.Count == 0 || !actualFiles.SetEquals(deployedFiles.Select(item => item.FullPath!))
            || deployedFiles.Any(item => !dependency.DeployedFileHashes.TryGetValue(item.Relative, out var expected) || !ApmFileHashMatches(item.FullPath!, expected))) return null;
        var payloadHash = PayloadHasher.HashFolder(path);
        var fileCount = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Count();
        var record = new State.ManagementRecord
        {
            InstallationId = Guid.NewGuid().ToString("N"), CanonicalPath = Path.GetFullPath(path),
            Provenance = new State.ProvenanceInfo
            {
                SourceProvider = ApmClient.ProviderId, OriginalReference = dependency.RepositoryUrl,
                NormalizedSource = dependency.RepositoryUrl, Host = string.Empty, Owner = string.Empty,
                Repository = dependency.Identity, SourceSkillPath = folderName, TrackingRule = dependency.TrackingRule,
                TrackingRuleKind = dependency.TrackingRuleKind, ResolvedCommit = dependency.Revision,
                SelectedContentIdentity = payloadHash, ProviderVersion = dependency.ProviderVersion,
                ProviderSkillName = dependency.SkillSubset.Contains(folderName, StringComparer.Ordinal) ? folderName : null,
            },
            IntendedClaudeJunctionPath = Path.Combine(HarnessRoot.Create(RootKind.ClaudeSkills, home).FullPath, folderName),
            InstalledRevision = dependency.Revision, InstalledPayloadHash = payloadHash, InstalledFileCount = fileCount,
            ProviderEvidence = dependency.Evidence,
        };
        return new AdoptionEvidence(record, payloadHash, fileCount, payloadHash);
    }

    private static bool ApmFileHashMatches(string path, string expected)
    {
        var bytes = File.ReadAllBytes(path);
        if (!bytes.Contains((byte)0))
        {
            try { bytes = System.Text.Encoding.UTF8.GetBytes(new System.Text.UTF8Encoding(false, true).GetString(bytes).Replace("\r\n", "\n", StringComparison.Ordinal)); }
            catch (System.Text.DecoderFallbackException) { }
        }
        var actual = "sha256:" + Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes)).ToLowerInvariant();
        return string.Equals(actual, expected.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? expected : "sha256:" + expected, StringComparison.OrdinalIgnoreCase);
    }

    private static string? ResolveDeployedFile(string home, string skillPath, string relative)
    {
        var path = Path.GetFullPath(Path.Combine(home, relative.Replace('/', Path.DirectorySeparatorChar)));
        var root = Path.GetFullPath(skillPath) + Path.DirectorySeparatorChar;
        return path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path)
               && !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint) ? path : null;
    }

    private static bool IsCredentialFreeSource(string source)
        => !Uri.TryCreate(source, UriKind.Absolute, out var uri) || string.IsNullOrEmpty(uri.UserInfo) && string.IsNullOrEmpty(uri.Query);

    private static string? DeployedSkillFolder(string path)
    {
        var parts = path.Replace('\\', '/').Trim('/').Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length >= 3
               && string.Equals(parts[0], ".agents", StringComparison.OrdinalIgnoreCase)
               && string.Equals(parts[1], "skills", StringComparison.OrdinalIgnoreCase)
               && IsSafeFolderName(parts[2])
            ? parts[2]
            : null;
    }

    private static bool IsSafeFolderName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name is not "." and not ".."
           && name.IndexOfAny(Path.GetInvalidFileNameChars()) < 0
           && !name.Contains('/') && !name.Contains('\\');

    private sealed record ProviderObservation(string Path, ProviderAttribution Attribution, AdoptionEvidence? AdoptionEvidence);

    private InventoryEntry BuildLinkEntry(Candidate link)
    {
        var resolved = ResolveFinalTarget(link);
        var exposures = BuildExposuresForFolder(link);
        var state = resolved is null ? ExposureState.BrokenLink : ExposureState.ForeignLink;
        var detail = resolved is null
            ? "Unknown reparse point whose target could not be resolved. Inspected without following."
            : $"Reparse point targeting '{resolved}'. It does not expose a canonical installation and stays observation-only.";

        foreach (var harness in link.Root.DirectHarnesses())
        {
            exposures[harness] = new HarnessExposure(state, detail);
        }

        return new InventoryEntry
        {
            FolderName = link.FolderName,
            LocalPath = link.FileSystemInfo.FullName,
            RootKind = link.Root.Kind,
            Kind = EntryKind.LinkEntry,
            LinkTargetPath = resolved,
            ManagementStatus = ManagementStatus.Unmanaged,
            Health = InstallationHealth.Collision,
            HealthDetail = detail,
            Metadata = SkillMdReader.Read(link.FileSystemInfo.FullName, link.FolderName),
            Exposures = exposures,
        };
    }

    private Dictionary<Harness, HarnessExposure> BuildExposuresForFolder(Candidate candidate)
    {
        var direct = candidate.Root.DirectHarnesses();
        var exposures = new Dictionary<Harness, HarnessExposure>();
        foreach (Harness harness in Enum.GetValues(typeof(Harness)))
        {
            exposures[harness] = direct.Contains(harness) ? HarnessExposure.Direct() : HarnessExposure.None();
        }

        if (candidate.Root.Kind == RootKind.ClaudeSkills)
        {
            exposures[Harness.GitHubCopilot] = new HarnessExposure(ExposureState.None, "VS Code surfaces only; CLI and common docs omit this root");
        }

        if (candidate.Root.Kind == RootKind.CanonicalAgents)
        {
            foreach (var harness in new[] { Harness.OpenCode, Harness.Codex, Harness.GitHubCopilot })
            {
                exposures[harness] = HarnessExposure.Canonical();
            }
        }

        return exposures;
    }

    private static string? ResolveFinalTarget(Candidate link)
    {
        try
        {
            var final = link.FileSystemInfo.ResolveLinkTarget(returnFinalTarget: true);
            if (final is null || !final.Exists)
            {
                return null;
            }

            return final.FullName;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string NormalizeRelative(string relativePath) => relativePath.Replace('/', Path.DirectorySeparatorChar);

    private static bool IsCompleteEvidence(AdoptionEvidence evidence, Candidate candidate)
    {
        var record = evidence.ProposedRecord;
        var provenance = record.Provenance;
        var common = !string.IsNullOrWhiteSpace(provenance.SourceSkillPath)
                     && string.Equals(provenance.ResolvedCommit, record.InstalledRevision, StringComparison.OrdinalIgnoreCase)
                     && string.Equals(provenance.SelectedContentIdentity, evidence.ExpectedContentIdentity, StringComparison.Ordinal)
                     && string.Equals(record.InstalledPayloadHash, evidence.ExpectedPayloadHash, StringComparison.OrdinalIgnoreCase)
                     && record.InstalledFileCount == evidence.ExpectedFileCount
                     && string.Equals(NormalizePath(record.CanonicalPath), NormalizePath(candidate.FileSystemInfo.FullName), StringComparison.OrdinalIgnoreCase);
        if (!common) return false;
        if (string.Equals(provenance.SourceProvider, "skills", StringComparison.Ordinal))
            return string.Equals(record.ProviderEvidence, $"skills@{SkillsCliClient.Version}:{provenance.ProviderSkillName}:{record.InstalledRevision}", StringComparison.Ordinal);
        if (string.Equals(provenance.SourceProvider, ApmClient.ProviderId, StringComparison.Ordinal))
            return record.ProviderEvidence.StartsWith($"microsoft/apm:{provenance.Repository}:{record.InstalledRevision}:", StringComparison.Ordinal);

        var normalizedSource = $"{provenance.Host}/{provenance.Owner}/{provenance.Repository}".ToLowerInvariant();
        var requestedPath = provenance.RequestedPath?.Trim('/') ?? string.Empty;
        var repositoryPath = provenance.SourceSkillPath == "."
            ? requestedPath
            : requestedPath.Length == 0
                ? provenance.SourceSkillPath
                : requestedPath + "/" + provenance.SourceSkillPath;
        var expectedProviderEvidence = $"gh api contents/{(repositoryPath.Length == 0 ? "." : repositoryPath)}@{record.InstalledRevision}";
        return string.Equals(provenance.SourceProvider, "github", StringComparison.Ordinal)
               && string.Equals(provenance.NormalizedSource, normalizedSource, StringComparison.Ordinal)
               && string.Equals(record.ProviderEvidence, expectedProviderEvidence, StringComparison.Ordinal)
               && common;
    }

    private sealed class Candidate(HarnessRoot root, string folderName, FileSystemInfo fileSystemInfo, EntryKind kind)
    {
        public HarnessRoot Root { get; } = root;

        public string FolderName { get; } = folderName;

        public FileSystemInfo FileSystemInfo { get; } = fileSystemInfo;

        public EntryKind Kind { get; } = kind;

        public HarnessExposure? ClaudeExposure { get; set; }

        public bool ConsumedAsExposure { get; set; }
    }
}
