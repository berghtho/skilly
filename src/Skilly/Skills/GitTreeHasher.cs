using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Skilly.Skills;

public static class GitTreeHasher
{
    public static string HashFolder(string path) => HashFolder(path, normalizeTextLineEndings: false);

    public static bool MatchesFolder(string path, string expected)
        => string.Equals(HashFolder(path, normalizeTextLineEndings: false), expected, StringComparison.OrdinalIgnoreCase)
           || string.Equals(HashFolder(path, normalizeTextLineEndings: true), expected, StringComparison.OrdinalIgnoreCase);

    private static string HashFolder(string path, bool normalizeTextLineEndings)
        => Convert.ToHexString(HashDirectory(path, normalizeTextLineEndings)).ToLowerInvariant();

    private static byte[] HashDirectory(string path, bool normalizeTextLineEndings)
    {
        var entries = new DirectoryInfo(path).EnumerateFileSystemInfos()
            .OrderBy(static entry => entry.Name + (entry is DirectoryInfo ? "/" : string.Empty), StringComparer.Ordinal)
            .ToList();
        using var content = new MemoryStream();
        foreach (var entry in entries)
        {
            if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new InvalidDataException("Git tree hashing refuses reparse points.");
            var isDirectory = entry is DirectoryInfo;
            var mode = isDirectory ? "40000" : "100644";
            var hash = isDirectory
                ? HashDirectory(entry.FullName, normalizeTextLineEndings)
                : HashObject("blob", FileBytes(entry.FullName, normalizeTextLineEndings));
            content.Write(Encoding.ASCII.GetBytes(mode + " "));
            content.Write(Encoding.UTF8.GetBytes(entry.Name));
            content.WriteByte(0);
            content.Write(hash);
        }
        return HashObject("tree", content.ToArray());
    }

    private static byte[] HashObject(string type, byte[] content)
    {
        var header = Encoding.ASCII.GetBytes($"{type} {content.Length}\0");
        return SHA1.HashData([.. header, .. content]);
    }

    private static byte[] FileBytes(string path, bool normalizeTextLineEndings)
    {
        var bytes = File.ReadAllBytes(path);
        if (!normalizeTextLineEndings || bytes.Contains((byte)0)) return bytes;
        try
        {
            var text = new UTF8Encoding(false, true).GetString(bytes);
            return Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n", StringComparison.Ordinal));
        }
        catch (DecoderFallbackException)
        {
            return bytes;
        }
    }
}
