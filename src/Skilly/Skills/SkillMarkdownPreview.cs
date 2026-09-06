using System.IO;

namespace Skilly.Skills;

public static class SkillMarkdownPreview
{
    public const int MaxCharacters = 256 * 1024;
    private const string Truncated = "\n\nPreview truncated at 256K characters. Open the original SKILL.md to read the complete file.";

    public static string FromText(string text)
        => text.Length > MaxCharacters ? text[..MaxCharacters] + Truncated : text;

    public static string ReadFile(string path)
    {
        using var reader = new StreamReader(path);
        var buffer = new char[MaxCharacters];
        var count = reader.ReadBlock(buffer, 0, buffer.Length);
        return new string(buffer, 0, count) + (reader.Peek() >= 0 ? Truncated : string.Empty);
    }
}
