using System.IO;
using System.Security.Cryptography;
using System.Text;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.Providers;

public sealed record PreviewFile(string Path, long Length, string Hash, string? Text);
public sealed record FileChange(string Path, string Change, PreviewFile? Before, PreviewFile? After)
{
    public string BeforeText => Before is null ? "File does not exist in the installed version." : Before.Text ?? "Binary or large file. Text preview unavailable.";
    public string AfterText => After is null ? "File is removed in the new version." : After.Text ?? "Binary or large file. Text preview unavailable.";
    public string Diff => PreviewFiles.TextDiff(Before, After);
}

public sealed record SkillUpdatePreview(string InstallationId, string Name, string LocalPath,
    string InstalledRevision, string TargetRevision, string StartingHash, string TargetHash,
    IReadOnlyList<FileChange> Files)
{
    public string Summary => $"{Name}: {Files.Count} changed file(s), {InstalledRevision} → {TargetRevision}";
}

public sealed record UpdatePreview(string Key, string Provider, IReadOnlyList<SkillUpdatePreview> Skills, string? Blocker = null)
{
    public bool CanApply => Blocker is null && Skills.Count > 0;

    public void VerifyStarting(ManagementRecord record)
    {
        if (!CanApply) throw new ProviderFailure(Blocker ?? "This preview cannot be applied.");
        var expected = Skills.SingleOrDefault(skill => skill.InstallationId == record.InstallationId)
            ?? throw new ProviderFailure("The affected Skill set changed after preview. Prepare a new preview.");
        if (!string.Equals(expected.LocalPath, record.CanonicalPath, StringComparison.OrdinalIgnoreCase)
            || expected.InstalledRevision != record.InstalledRevision
            || !string.Equals(expected.StartingHash, record.InstalledPayloadHash, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(expected.StartingHash, PayloadHasher.HashFolder(record.CanonicalPath), StringComparison.OrdinalIgnoreCase))
            throw new ProviderFailure("Installed content changed after preview. Refresh checks and prepare a new preview.");
        if (record.Provenance.SourceProvider != "apm" && record.LatestCheck?.AvailableRevision != expected.TargetRevision)
            throw new ProviderFailure("The checked revision changed after preview. Prepare a new preview.");
    }

    public void VerifyTarget(string path, string hash)
    {
        var expected = Skills.SingleOrDefault(skill => string.Equals(skill.LocalPath, path, StringComparison.OrdinalIgnoreCase));
        if (expected is null || !string.Equals(expected.TargetHash, hash, StringComparison.OrdinalIgnoreCase))
            throw new ProviderFailure("The update differs from the reviewed content. Prepare a new preview.");
    }
}

public static class PreviewFiles
{
    private const int MaxTextBytes = 128 * 1024;
    private const int MaxTotalTextBytes = 4 * 1024 * 1024;

    public static IReadOnlyList<PreviewFile> ReadFolder(string root)
    {
        if (!Directory.Exists(root)) return [];
        var result = new List<PreviewFile>();
        var pending = new Stack<string>();
        pending.Push(root);
        var textBudget = MaxTotalTextBytes;
        while (pending.TryPop(out var directory))
        {
            RefuseLink(directory);
            foreach (var child in Directory.EnumerateFileSystemEntries(directory))
            {
                RefuseLink(child);
                if (Directory.Exists(child)) { pending.Push(child); continue; }
                if (result.Count >= 10000) throw new ProviderFailure("Preview supports at most 10,000 files per Skill.");
                using var stream = File.OpenRead(child);
                var hash = Convert.ToHexString(SHA256.HashData(stream));
                string? text = null;
                if (stream.Length <= MaxTextBytes && stream.Length <= textBudget)
                {
                    stream.Position = 0;
                    var bytes = new byte[(int)stream.Length];
                    stream.ReadExactly(bytes);
                    text = Decode(bytes);
                    textBudget -= bytes.Length;
                }
                result.Add(new PreviewFile(Path.GetRelativePath(root, child).Replace('\\', '/'), stream.Length, hash, text));
            }
        }
        return result;
    }

    public static IReadOnlyList<PreviewFile> FromPayload(IReadOnlyList<(string RelativePath, byte[] Content)> files)
    {
        var budget = MaxTotalTextBytes;
        return files.Select(file =>
        {
            var text = file.Content.Length <= MaxTextBytes && file.Content.Length <= budget ? Decode(file.Content) : null;
            if (text is not null) budget -= file.Content.Length;
            return new PreviewFile(file.RelativePath, file.Content.Length, Convert.ToHexString(SHA256.HashData(file.Content)), text);
        }).ToList();
    }

    public static IReadOnlyList<PreviewFile> ReadVerifiedFolder(string root, string expectedHash)
    {
        if (!string.Equals(PayloadHasher.HashFolder(root), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new ProviderFailure("Source content changed while preparing the preview.");
        var files = ReadFolder(root);
        if (!string.Equals(PayloadHasher.HashFolder(root), expectedHash, StringComparison.OrdinalIgnoreCase))
            throw new ProviderFailure("Source content changed while preparing the preview.");
        return files;
    }

    public static SkillUpdatePreview ForSkill(ManagementRecord record, string revision, string targetHash, IReadOnlyList<PreviewFile> target)
    {
        if (!string.Equals(PayloadHasher.HashFolder(record.CanonicalPath), record.InstalledPayloadHash, StringComparison.OrdinalIgnoreCase))
            throw new ProviderFailure("Local content differs from the Management Record. Refresh checks.");
        var before = ReadFolder(record.CanonicalPath);
        var preview = new SkillUpdatePreview(record.InstallationId, Path.GetFileName(record.CanonicalPath), record.CanonicalPath,
            record.InstalledRevision, revision, record.InstalledPayloadHash, targetHash, Compare(before, target));
        if (!string.Equals(PayloadHasher.HashFolder(record.CanonicalPath), record.InstalledPayloadHash, StringComparison.OrdinalIgnoreCase))
            throw new ProviderFailure("Local content changed during preview. Prepare a new preview.");
        return preview;
    }

    public static IReadOnlyList<FileChange> Compare(IReadOnlyList<PreviewFile> before, IReadOnlyList<PreviewFile> after)
    {
        var old = before.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        var next = after.ToDictionary(file => file.Path, StringComparer.OrdinalIgnoreCase);
        return old.Keys.Union(next.Keys, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
            .Select(path => new FileChange(path, !old.ContainsKey(path) ? "Added" : !next.ContainsKey(path) ? "Removed" : "Changed",
                old.GetValueOrDefault(path), next.GetValueOrDefault(path)))
            .Where(change => change.Before?.Hash != change.After?.Hash).ToList();
    }

    public static string TextDiff(PreviewFile? before, PreviewFile? after)
    {
        if ((before is not null && before.Text is null) || (after is not null && after.Text is null))
            return $"Binary or large file. Installed: {before?.Length ?? 0} bytes; available: {after?.Length ?? 0} bytes.\n\nInstalled SHA256: {before?.Hash ?? "absent"}\nAvailable SHA256: {after?.Hash ?? "absent"}";
        var left = (before?.Text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        var right = (after?.Text ?? string.Empty).Replace("\r\n", "\n").Split('\n');
        if (before is not null && after is not null && left.SequenceEqual(right))
            return "The text is unchanged after normalizing line endings. File bytes differ; inspect Installed / Available for the original text.";
        var prefix = 0;
        while (prefix < Math.Min(left.Length, right.Length) && left[prefix] == right[prefix]) prefix++;
        var suffix = 0;
        while (suffix < Math.Min(left.Length, right.Length) - prefix && left[^(suffix + 1)] == right[^(suffix + 1)]) suffix++;
        var lines = left.Skip(Math.Max(0, prefix - 3)).Take(Math.Min(prefix, 3)).Select(line => "  " + line)
            .Concat(left.Skip(prefix).Take(left.Length - prefix - suffix).Select(line => "- " + line))
            .Concat(right.Skip(prefix).Take(right.Length - prefix - suffix).Select(line => "+ " + line))
            .Concat(right.Skip(right.Length - suffix).Take(3).Select(line => "  " + line)).ToList();
        return $"Changed block, starting at line {prefix + 1}. - installed, + available.\n\n"
            + string.Join('\n', lines.Take(1200)) + (lines.Count > 1200 ? "\nDiff truncated. Use Installed / Available tabs for more text." : string.Empty);
    }

    private static string? Decode(byte[] bytes)
    {
        if (bytes.Contains((byte)0)) return null;
        try { return new UTF8Encoding(false, true).GetString(bytes); }
        catch (DecoderFallbackException) { return null; }
    }

    private static void RefuseLink(string path)
    {
        if (File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            throw new ProviderFailure($"Preview refuses linked content at '{path}'.");
    }
}
