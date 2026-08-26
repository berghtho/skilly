using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Skilly.Skills;

public static class PayloadHasher
{
    public static string HashFiles(IEnumerable<(string RelativePath, byte[] Content)> files)
    {
        using var sha = SHA256.Create();
        foreach (var file in files.OrderBy(static file => Normalize(file.RelativePath), StringComparer.Ordinal))
        {
            var relative = Encoding.UTF8.GetBytes(Normalize(file.RelativePath));
            var lengthPrefix = BitConverter.GetBytes(relative.LongLength);
            sha.TransformBlock(lengthPrefix, 0, lengthPrefix.Length, null, 0);
            sha.TransformBlock(relative, 0, relative.Length, null, 0);
            var contentLength = BitConverter.GetBytes(file.Content.LongLength);
            sha.TransformBlock(contentLength, 0, contentLength.Length, null, 0);
            sha.TransformBlock(file.Content, 0, file.Content.Length, null, 0);
            sha.TransformBlock([0x00], 0, 1, null, 0);
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash!);
    }

    public static string HashFolder(string folderPath)
    {
        var root = Path.GetFullPath(folderPath);
        var files = Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .Select(file => (RelativePath: Path.GetRelativePath(root, file), Content: File.ReadAllBytes(file)))
            .ToList();
        return HashFiles(files);
    }

    private static string Normalize(string relativePath)
        => relativePath.Replace('\\', '/');
}
