using System.IO;

namespace Skilly.Skills;

public sealed record InventorySnapshot(IReadOnlyList<InventoryEntry> Entries, DateTimeOffset ScannedAt)
{
    public int HealthyCount { get; } = Entries.Count(static entry => entry.Health == InstallationHealth.Healthy);

    public int AttentionCount { get; } = Entries.Count(static entry => entry.NeedsAttention);

    public int UnmanagedCount { get; } = Entries.Count(static entry => entry.ManagementStatus == ManagementStatus.Unmanaged);
}

public sealed class InventoryScanner
{
    public InventorySnapshot Scan(string home, State.SkillyState? state = null)
    {
        var recordsByPath = (state?.Records ?? [])
            .GroupBy(static record => record.CanonicalPath.TrimEnd(Path.DirectorySeparatorChar), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(static group => group.Key, static group => group.Last(), StringComparer.OrdinalIgnoreCase);

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
            entries.Add(BuildFolderEntry(candidate, duplicateNames.Contains(candidate), record));
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

        if (canonicalByPath.ContainsKey(NormalizePath(resolved)))
        {
            link.ConsumedAsExposure = true;
            return new HarnessExposure(ExposureState.VerifiedJunction, $"Junction at {link.FileSystemInfo.FullName} resolves to the canonical installation");
        }

        return new HarnessExposure(ExposureState.SeparateCopy, $"{link.FileSystemInfo.FullName} exists but does not resolve to the canonical installation");
    }

    private InventoryEntry BuildFolderEntry(
        Candidate candidate,
        bool isDuplicate,
        State.ManagementRecord? record)
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
                health = InstallationHealth.ExposureProblem;
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

        return new InventoryEntry
        {
            FolderName = candidate.FolderName,
            LocalPath = candidate.FileSystemInfo.FullName,
            RootKind = candidate.Root.Kind,
            Kind = EntryKind.RealFolder,
            ManagementStatus = record is null ? ManagementStatus.Unmanaged : ManagementStatus.Managed,
            Health = health,
            HealthDetail = detail,
            Metadata = metadata,
            Exposures = exposures,
            ManagementRecord = record,
        };
    }

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
            Health = InstallationHealth.ExposureProblem,
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
