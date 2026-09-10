using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Skilly.Infrastructure;
using Skilly.Providers.GitHub;
using Skilly.State;

namespace Skilly.Skills;

public sealed record SkillSetItem(string FolderName, string Name, string Description, int FileCount, long Bytes, string? Conflict);

public sealed class SkillSetPreview : IDisposable
{
    internal SkillSetPreview(string name, string directory, IReadOnlyList<SkillSetItem> skills, IReadOnlyDictionary<string, string> hashes)
    { Name = name; Directory = directory; Skills = skills; Hashes = hashes; }

    public string Name { get; }
    public IReadOnlyList<SkillSetItem> Skills { get; }
    internal string Directory { get; }
    internal IReadOnlyDictionary<string, string> Hashes { get; }
    internal bool Disposed { get; private set; }

    public void Dispose()
    {
        if (Disposed) return;
        Disposed = true;
        SkillSetArchive.DeleteStaging(Directory);
    }
}

/// <summary>Portable payload snapshots. Archive metadata never grants management authority.</summary>
public sealed class SkillSetArchive(StateStore stateStore, string home)
{
    private const string Format = "skilly-skill-set";
    private const int MaxFiles = 10_000;
    private const int MaxSkills = 1_000;
    private const long MaxBytes = 512L * 1024 * 1024;
    private const int MaxFileBytes = 64 * 1024 * 1024;
    private const int MaxManifestBytes = 4 * 1024 * 1024;
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase, WriteIndented = true };
    private string CanonicalRoot => HarnessRoot.Create(RootKind.CanonicalAgents, home).FullPath;
    private string ClaudeRoot => HarnessRoot.Create(RootKind.ClaudeSkills, home).FullPath;
    private string StagingRoot => Path.Combine(Path.GetFullPath(home), ".agents", ".skilly-transfers");

    private sealed record Manifest(string Format, int Version, string Name, List<ManifestSkill> Skills);
    private sealed record ManifestSkill(string FolderName, List<ManifestFile> Files);
    private sealed record ManifestFile(string Path, long Bytes, string Sha256);

    public void Export(string archivePath, string name, IReadOnlyList<InventoryEntry> selected, CancellationToken cancellationToken = default)
    {
        ValidateName(name);
        if (selected.Count is < 1 or > MaxSkills) throw new InvalidDataException($"Select between 1 and {MaxSkills} Skills.");
        if (selected.Select(item => item.FolderName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != selected.Count)
            throw new InvalidDataException("Select only one installation of each Skill folder name.");
        var destination = Path.GetFullPath(archivePath);
        EnsureNoLinks(destination);
        foreach (var skill in selected)
        {
            ValidateFolder(skill.FolderName);
            if (skill.Kind != EntryKind.RealFolder || SkillMdReader.Read(skill.LocalPath, skill.FolderName).Status != MetadataReadStatus.Valid)
                throw new InvalidDataException($"'{skill.FolderName}' is not a readable Skill folder with valid SKILL.md metadata.");
            if (IsWithin(destination, skill.LocalPath)) throw new InvalidDataException("Save the archive outside the selected Skill folders.");
        }
        var temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var skills = new List<ManifestSkill>();
            long bytes = 0;
            var count = 0;
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (var zip = new ZipArchive(stream, ZipArchiveMode.Create))
            {
                foreach (var skill in selected)
                {
                    var files = new List<ManifestFile>();
                    foreach (var file in EnumeratePayload(skill.LocalPath))
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var relative = Path.GetRelativePath(skill.LocalPath, file).Replace('\\', '/');
                        ValidateRelativePath(relative);
                        var length = new FileInfo(file).Length;
                        bytes += length;
                        if (++count > MaxFiles || length > MaxFileBytes || bytes > MaxBytes) throw new InvalidDataException("Skill set exceeds the file or size limit.");
                        using var input = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.Read);
                        using var output = zip.CreateEntry($"skills/{skill.FolderName}/{relative}", CompressionLevel.Optimal).Open();
                        files.Add(new ManifestFile(relative, length, CopyAndHash(input, output, length, cancellationToken)));
                    }
                    if (!files.Any(file => file.Path == "SKILL.md")) throw new InvalidDataException($"'{skill.FolderName}' has no SKILL.md.");
                    skills.Add(new ManifestSkill(skill.FolderName, files));
                }
                var manifest = JsonSerializer.SerializeToUtf8Bytes(new Manifest(Format, 1, name.Trim(), skills), JsonOptions);
                if (manifest.Length > MaxManifestBytes) throw new InvalidDataException("Skill set manifest is too large.");
                using var outputManifest = zip.CreateEntry("skill-set.json").Open();
                outputManifest.Write(manifest);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    public SkillSetPreview Open(string archivePath, CancellationToken cancellationToken = default)
    {
        EnsureNoLinks(StagingRoot);
        var stage = Path.Combine(StagingRoot, Guid.NewGuid().ToString("N"));
        try
        {
            using var zip = ZipFile.OpenRead(archivePath);
            if (zip.Entries.Count is < 2 or > MaxFiles + 1) throw new InvalidDataException("Invalid archive file count.");
            var entries = new Dictionary<string, ZipArchiveEntry>(StringComparer.OrdinalIgnoreCase);
            long total = 0;
            foreach (var entry in zip.Entries)
            {
                ValidateRelativePath(entry.FullName);
                var unixType = (entry.ExternalAttributes >> 16) & 0xF000;
                if (unixType is not (0 or 0x8000) || (entry.ExternalAttributes & (int)FileAttributes.ReparsePoint) != 0)
                    throw new InvalidDataException("Archives may contain regular files only.");
                if (!entries.TryAdd(entry.FullName, entry)) throw new InvalidDataException("Archive contains duplicate paths.");
                total += entry.Length;
                if (entry.Length > MaxFileBytes || total > MaxBytes + MaxManifestBytes) throw new InvalidDataException("Archive exceeds the size limit.");
            }
            if (!entries.TryGetValue("skill-set.json", out var manifestEntry) || manifestEntry.FullName != "skill-set.json" || manifestEntry.Length > MaxManifestBytes)
                throw new InvalidDataException("Archive has no supported skill-set.json manifest.");
            if (total - manifestEntry.Length > MaxBytes) throw new InvalidDataException("Skill set payload exceeds the size limit.");
            Manifest manifest;
            using (var input = manifestEntry.Open())
            using (var memory = new MemoryStream())
            {
                CopyAndHash(input, memory, manifestEntry.Length, cancellationToken);
                manifest = JsonSerializer.Deserialize<Manifest>(memory.ToArray(), JsonOptions) ?? throw new InvalidDataException("Empty Skill set manifest.");
            }
            if (manifest.Format != Format || manifest.Version != 1) throw new InvalidDataException("Unsupported Skill set format or version.");
            ValidateName(manifest.Name);
            if (manifest.Skills is null || manifest.Skills.Count is < 1 or > MaxSkills) throw new InvalidDataException("Invalid Skill count.");
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var expected = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "skill-set.json" };
            foreach (var skill in manifest.Skills)
            {
                if (skill is null) throw new InvalidDataException("Invalid Skill entry.");
                ValidateFolder(skill.FolderName);
                if (!names.Add(skill.FolderName) || skill.Files is null || skill.Files.Count == 0) throw new InvalidDataException("Duplicate or empty Skill.");
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var file in skill.Files)
                {
                    if (file is null) throw new InvalidDataException("Invalid file entry.");
                    ValidateRelativePath(file.Path);
                    var path = $"skills/{skill.FolderName}/{file.Path}";
                    if (!expected.Add(path) || !paths.Add(file.Path) || !entries.TryGetValue(path, out var entry)
                        || entry.FullName != path || entry.Length != file.Bytes || file.Sha256 is null
                        || file.Sha256.Length != 64 || !file.Sha256.All(Uri.IsHexDigit))
                        throw new InvalidDataException("Manifest contains missing, duplicate or invalid files.");
                }
                if (!skill.Files.Any(file => file.Path == "SKILL.md")) throw new InvalidDataException($"'{skill.FolderName}' has no SKILL.md.");
                foreach (var path in paths)
                {
                    for (var slash = path.IndexOf('/'); slash >= 0; slash = path.IndexOf('/', slash + 1))
                        if (paths.Contains(path[..slash])) throw new InvalidDataException("A file is also used as a directory.");
                }
            }
            if (expected.Count != entries.Count) throw new InvalidDataException("Archive contains files not listed in its manifest.");
            Directory.CreateDirectory(stage);
            var items = new List<SkillSetItem>();
            var hashes = new Dictionary<string, string>(StringComparer.Ordinal);
            var state = stateStore.Load();
            foreach (var skill in manifest.Skills)
            {
                var folder = Path.Combine(stage, skill.FolderName);
                foreach (var file in skill.Files)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var path = Path.Combine(folder, file.Path.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                    using var input = entries[$"skills/{skill.FolderName}/{file.Path}"].Open();
                    using var output = new FileStream(path, FileMode.CreateNew);
                    if (!string.Equals(CopyAndHash(input, output, file.Bytes, cancellationToken), file.Sha256, StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException($"Checksum mismatch in '{skill.FolderName}/{file.Path}'.");
                }
                var metadata = SkillMdReader.Read(folder, skill.FolderName);
                if (metadata.Status != MetadataReadStatus.Valid) throw new InvalidDataException($"Invalid SKILL.md in '{skill.FolderName}': {metadata.Error}");
                hashes.Add(skill.FolderName, GitTreeHasher.HashFolder(folder));
                items.Add(new SkillSetItem(skill.FolderName, metadata.DeclaredName!, metadata.Description!, skill.Files.Count,
                    skill.Files.Sum(file => file.Bytes), Conflict(skill.FolderName, state)));
            }
            return new SkillSetPreview(manifest.Name, stage, items.AsReadOnly(), hashes);
        }
        catch { DeleteStaging(stage); throw; }
    }

    public int Import(SkillSetPreview preview, IReadOnlyList<string> selected, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(preview.Disposed, preview);
        if (!IsWithin(preview.Directory, StagingRoot)) throw new InvalidDataException("Preview belongs to another profile.");
        var state = stateStore.Load();
        if (stateStore.RecoveryRequired || state.PendingOperation is not null) throw new RecoveryRequiredException("Resolve the pending operation before importing.");
        if (selected.Count == 0 || selected.Distinct(StringComparer.Ordinal).Count() != selected.Count) throw new InvalidDataException("Select distinct Skills to import.");
        var targets = new List<SkillSetImportTarget>();
        foreach (var name in selected)
        {
            if (!preview.Hashes.TryGetValue(name, out var hash)) throw new InvalidDataException("Selection is not part of the reviewed Skill set.");
            var conflict = Conflict(name, state);
            if (conflict is not null) throw new IOException($"'{name}': {conflict}");
            var source = Path.Combine(preview.Directory, name);
            EnsureNoLinks(source);
            if (GitTreeHasher.HashFolder(source) != hash) throw new InvalidDataException("Reviewed Skill files changed. Open the archive again.");
            targets.Add(new SkillSetImportTarget(name, hash));
        }
        cancellationToken.ThrowIfCancellationRequested();
        var pending = new PendingOperation
        {
            OperationId = Guid.NewGuid().ToString("N"), OperationType = MutationType.ImportSkillSet,
            StartedAt = DateTimeOffset.Now, SkillSetTargets = targets,
            StartingPaths = targets.SelectMany(target => new[] { Canonical(target.FolderName), Claude(target.FolderName) }).ToList(),
            StartingPathStates = targets.SelectMany(_ => new[] { PathState.Missing, PathState.Missing }).ToList(),
            TemporaryPaths = [preview.Directory],
        };
        state.PendingOperation = pending;
        stateStore.Save(state);
        var created = new List<SkillSetImportTarget>();
        var createdExposures = new List<string>();
        try
        {
            // Save twice before mutating so state.json.bak also contains the pending journal.
            pending.Phase = PendingOperationPhase.MutationStarted;
            stateStore.Save(state);
            EnsureNoLinks(CanonicalRoot);
            EnsureNoLinks(ClaudeRoot);
            Directory.CreateDirectory(CanonicalRoot);
            Directory.CreateDirectory(ClaudeRoot);
            foreach (var target in targets)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var conflict = Conflict(target.FolderName, state);
                if (conflict is not null) throw new IOException(conflict);
                Directory.Move(Path.Combine(preview.Directory, target.FolderName), Canonical(target.FolderName));
                created.Add(target);
                pending.CreatedSkillSetFolders.Add(target.FolderName);
                stateStore.Save(state);
                if (Exists(Claude(target.FolderName))) throw new IOException("The Claude destination appeared during import.");
                Junction.Create(Claude(target.FolderName), Canonical(target.FolderName), requireNew: true);
                createdExposures.Add(target.FolderName);
                pending.CreatedSkillSetExposures.Add(target.FolderName);
                stateStore.Save(state);
                if (!Junction.IsJunctionTo(Claude(target.FolderName), Canonical(target.FolderName))
                    || GitTreeHasher.HashFolder(Canonical(target.FolderName)) != target.PayloadHash)
                    throw new IOException("Imported files or Harness Exposure could not be verified.");
            }
            cancellationToken.ThrowIfCancellationRequested();
            pending.Phase = PendingOperationPhase.Verified;
            stateStore.Save(state);
            state.PendingOperation = null;
            state.LastOperationNote = $"Imported {targets.Count} Skill(s) as Unmanaged from a Skill set.";
            stateStore.Save(state);
            return targets.Count;
        }
        catch (Exception exception)
        {
            state.PendingOperation = pending;
            try
            {
                Rollback(created, createdExposures);
                state.PendingOperation = null;
                state.LastOperationNote = "Skill set import failed; created folders were removed.";
                stateStore.Save(state);
            }
            catch (Exception rollback)
            {
                stateStore.EnterRecoveryRequired($"Skill set import requires recovery: {rollback.Message}");
                throw new RecoveryRequiredException("Skill set import could not be rolled back safely.", exception);
            }
            throw;
        }
    }

    public RecoveryResult RecoverPendingImport()
    {
        try
        {
            var state = stateStore.Load();
            var pending = state.PendingOperation;
            if (pending?.OperationType != MutationType.ImportSkillSet) return new RecoveryResult(RecoveryDisposition.None, "No Skill set import pending.");
            if (pending.SkillSetTargets is null || pending.SkillSetTargets.Count is < 1 or > MaxSkills
                || pending.CreatedSkillSetFolders is null || pending.CreatedSkillSetExposures is null || pending.TemporaryPaths is null
                || pending.SkillSetTargets.Any(target => target is null)
                || pending.SkillSetTargets.Select(target => target.FolderName).Distinct(StringComparer.OrdinalIgnoreCase).Count() != pending.SkillSetTargets.Count)
                throw new InvalidDataException("Invalid Skill set recovery journal.");
            foreach (var target in pending.SkillSetTargets)
            {
                ValidateFolder(target.FolderName);
                if (target.PayloadHash is null || target.PayloadHash.Length != 40 || !target.PayloadHash.All(Uri.IsHexDigit))
                    throw new InvalidDataException("Invalid Skill set recovery hash.");
                EnsureNoLinks(Canonical(target.FolderName));
                EnsureNoLinks(ClaudeRoot);
                if (state.Records.Any(record => string.Equals(record.CanonicalPath, Canonical(target.FolderName), StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("An imported path now has a Management Record.");
            }
            var expectedPaths = pending.SkillSetTargets.SelectMany(target => new[] { Canonical(target.FolderName), Claude(target.FolderName) }).ToList();
            if (pending.StartingPaths is null || !pending.StartingPaths.SequenceEqual(expectedPaths, StringComparer.OrdinalIgnoreCase)
                || pending.StartingPathStates is null || pending.StartingPathStates.Count != expectedPaths.Count
                || pending.StartingPathStates.Any(value => value != PathState.Missing)
                || pending.CreatedSkillSetFolders.Distinct(StringComparer.Ordinal).Count() != pending.CreatedSkillSetFolders.Count
                || pending.CreatedSkillSetFolders.Any(name => !pending.SkillSetTargets.Any(target => target.FolderName == name))
                || pending.CreatedSkillSetExposures.Distinct(StringComparer.Ordinal).Count() != pending.CreatedSkillSetExposures.Count
                || pending.CreatedSkillSetExposures.Any(name => !pending.CreatedSkillSetFolders.Contains(name)))
                throw new InvalidDataException("Invalid Skill set recovery paths.");
            foreach (var directory in pending.TemporaryPaths)
            {
                if (directory is null || !IsWithin(directory, StagingRoot)
                    || !string.Equals(Path.GetDirectoryName(Path.GetFullPath(directory)), StagingRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Invalid staging path in recovery journal.");
                EnsureNoLinks(directory);
                if (Directory.Exists(directory)) _ = EnumeratePayload(directory).Count();
            }
            // A matching payload alone is not evidence that this operation created it.
            foreach (var target in pending.SkillSetTargets.Where(target => !pending.CreatedSkillSetFolders.Contains(target.FolderName)))
                if (Exists(Canonical(target.FolderName)) || Exists(Claude(target.FolderName)))
                    throw new InvalidDataException("A destination exists without a durable import creation record.");
            foreach (var target in pending.SkillSetTargets.Where(target => !pending.CreatedSkillSetExposures.Contains(target.FolderName)))
                if (Exists(Claude(target.FolderName))) throw new InvalidDataException("A Claude entry exists without a durable import creation record.");
            var created = pending.SkillSetTargets.Where(target => pending.CreatedSkillSetFolders.Contains(target.FolderName)).ToList();
            var completed = pending.Phase == PendingOperationPhase.Verified && created.Count == pending.SkillSetTargets.Count
                && pending.CreatedSkillSetExposures.Count == created.Count && created.All(target =>
                Directory.Exists(Canonical(target.FolderName)) && GitTreeHasher.HashFolder(Canonical(target.FolderName)) == target.PayloadHash
                && Junction.IsJunctionTo(Claude(target.FolderName), Canonical(target.FolderName)));
            if (!completed) Rollback(created, pending.CreatedSkillSetExposures);
            foreach (var directory in pending.TemporaryPaths) DeleteStaging(directory);
            state.PendingOperation = null;
            state.LastOperationNote = completed ? "Verified Skill set import completed; Skills remain Unmanaged." : "Interrupted Skill set import rolled back.";
            stateStore.Save(state);
            return new RecoveryResult(completed ? RecoveryDisposition.Completed : RecoveryDisposition.Restored, state.LastOperationNote);
        }
        catch (Exception exception)
        {
            stateStore.EnterRecoveryRequired($"Skill set import recovery could not be proven safe: {exception.Message}");
            return new RecoveryResult(RecoveryDisposition.RecoveryRequired, stateStore.RecoveryDiagnostic!);
        }
    }

    private void Rollback(IReadOnlyList<SkillSetImportTarget> targets, IReadOnlyCollection<string> createdExposures)
    {
        // Validate the entire rollback before removing anything. Changed or foreign content is retained.
        foreach (var target in targets)
        {
            var canonical = Canonical(target.FolderName);
            var claude = Claude(target.FolderName);
            EnsureNoLinks(canonical);
            EnsureNoLinks(ClaudeRoot);
            if (Exists(canonical) && (!Directory.Exists(canonical) || GitTreeHasher.HashFolder(canonical) != target.PayloadHash))
                throw new IOException($"'{canonical}' changed during import.");
            if (Exists(claude) && (!createdExposures.Contains(target.FolderName) || !Junction.IsJunctionTo(claude, canonical)))
                throw new IOException($"'{claude}' is not the imported Harness Exposure.");
        }
        foreach (var target in targets.Reverse())
        {
            if (Exists(Claude(target.FolderName))) Directory.Delete(Claude(target.FolderName));
            if (Directory.Exists(Canonical(target.FolderName))) Directory.Delete(Canonical(target.FolderName), recursive: true);
        }
    }

    private string? Conflict(string name, SkillyState state)
    {
        foreach (var kind in Enum.GetValues<RootKind>())
        {
            var root = HarnessRoot.Create(kind, home).FullPath;
            try { EnsureNoLinks(root); }
            catch (InvalidDataException) { return $"Discovery root is a link: {root}"; }
            if (Exists(Path.Combine(root, name))) return $"Already exists: {Path.Combine(root, name)}";
        }
        return state.Records.Any(record => string.Equals(Path.GetFileName(record.CanonicalPath), name, StringComparison.OrdinalIgnoreCase))
            ? "Reserved by an existing Management Record." : null;
    }

    private string Canonical(string name) => Path.Combine(CanonicalRoot, name);
    private string Claude(string name) => Path.Combine(ClaudeRoot, name);

    private static void ValidateName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || name.Length > 120 || name.Any(char.IsControl)) throw new InvalidDataException("Enter a Skill set name of 1 to 120 characters.");
    }

    private static void ValidateFolder(string? name)
    {
        if (name is null || !SkillMdReader.IsValidSkillFolderName(name)) throw new InvalidDataException("Invalid Skill folder name.");
        ValidateRelativePath(name);
    }

    private static void ValidateRelativePath(string? path)
    {
        if (string.IsNullOrEmpty(path) || path.Length > 1024 || path.Contains('\\')) throw new InvalidDataException("Invalid archive path.");
        foreach (var part in path.Split('/'))
        {
            if (part is "" or "." or ".." || part.Length > 255 || part.EndsWith(' ') || part.EndsWith('.')
                || part.Any(character => character < 32 || "<>:\"|?*".Contains(character)))
                throw new InvalidDataException($"Unsafe archive path '{path}'.");
            var stem = part.Split('.')[0].ToUpperInvariant();
            if (stem is "CON" or "PRN" or "AUX" or "NUL" or "CONIN$" or "CONOUT$"
                || (stem.Length == 4 && (stem.StartsWith("COM", StringComparison.Ordinal) || stem.StartsWith("LPT", StringComparison.Ordinal)) && "123456789¹²³".Contains(stem[3])))
                throw new InvalidDataException($"Reserved archive path '{path}'.");
        }
    }

    private static IEnumerable<string> EnumeratePayload(string directory)
    {
        EnsureNoLinks(directory);
        foreach (var entry in new DirectoryInfo(directory).EnumerateFileSystemInfos().OrderBy(entry => entry.Name, StringComparer.Ordinal))
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException($"Skill contains a link: {entry.Name}");
            if (entry is DirectoryInfo folder)
                foreach (var file in EnumeratePayload(folder.FullName)) yield return file;
            else yield return entry.FullName;
        }
    }

    internal static void DeleteStaging(string directory)
    {
        if (!Directory.Exists(directory)) return;
        // Refuse links even during cleanup, including links introduced after preview.
        _ = EnumeratePayload(directory).Count();
        Directory.Delete(directory, recursive: true);
    }

    private static void EnsureNoLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Exists(current) && File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException($"Linked paths are not supported: {current}");
    }

    private static bool Exists(string path)
    {
        try { _ = File.GetAttributes(path); return true; }
        catch (FileNotFoundException) { return false; }
        catch (DirectoryNotFoundException) { return false; }
    }

    private static bool IsWithin(string path, string directory)
        => Path.GetFullPath(path).StartsWith(Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static string CopyAndHash(Stream input, Stream output, long expected, CancellationToken cancellationToken)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = input.Read(buffer)) > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            total += read;
            if (total > expected) throw new InvalidDataException("File expanded beyond its declared size.");
            output.Write(buffer, 0, read);
            hash.AppendData(buffer, 0, read);
        }
        if (total != expected) throw new InvalidDataException("File size does not match its manifest.");
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}
