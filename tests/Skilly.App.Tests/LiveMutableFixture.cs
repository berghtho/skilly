using System.IO;

namespace Skilly.App.Tests;

internal sealed class LiveMutableFixture
{
    private LiveMutableFixture(string source, bool isMutable)
    {
        Source = source;
        IsMutable = isMutable;
    }

    public string Source { get; }
    public bool IsMutable { get; }

    public static LiveMutableFixture Prepare(string root, string sourceVariable, string templateVariable)
    {
        var template = Environment.GetEnvironmentVariable(templateVariable);
        if (!string.IsNullOrWhiteSpace(template))
        {
            Assert.True(Directory.Exists(template), $"{templateVariable} must identify an existing provider-compatible local fixture directory.");
            var destination = Path.Combine(root, "mutable-source");
            CopyDirectory(template, destination);
            return new LiveMutableFixture(destination, true);
        }

        var source = Environment.GetEnvironmentVariable(sourceVariable);
        Assert.False(string.IsNullOrWhiteSpace(source), $"Set {sourceVariable} or {templateVariable}.");
        return new LiveMutableFixture(source!, false);
    }

    public void Advance(string skillName)
    {
        Assert.True(IsMutable, "Only a copied mutable fixture can be advanced by the live test.");
        var matches = Directory.EnumerateFiles(Source, "SKILL.md", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetFileName(Path.GetDirectoryName(path)), skillName, StringComparison.OrdinalIgnoreCase))
            .ToList();
        var skillMd = Assert.Single(matches);
        File.AppendAllText(skillMd, $"{Environment.NewLine}Live update marker {Guid.NewGuid():N}{Environment.NewLine}");
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Assert.False(File.GetAttributes(directory).HasFlag(FileAttributes.ReparsePoint), "Mutable live fixture templates must not contain reparse points.");
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }
}
