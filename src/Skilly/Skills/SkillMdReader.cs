using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Skilly.Skills;

public enum MetadataReadStatus
{
    Valid,
    Invalid,
    Absent,
}

public sealed record SkillMetadata(
    MetadataReadStatus Status,
    string? DeclaredName,
    string? Description,
    string? Error)
{
    public static SkillMetadata Invalid(string error) => new(MetadataReadStatus.Invalid, null, null, error);
}

public static partial class SkillMdReader
{
    [GeneratedRegex("^[a-z0-9]+(-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    public const int MaxNameLength = 64;
    public const int MaxDescriptionLength = 1024;

    public static bool IsValidSkillFolderName(string folderName)
        => folderName.Length is >= 1 and <= MaxNameLength
           && NamePattern().IsMatch(folderName);

    public static SkillMetadata Read(string installationPath, string folderName)
    {
        var skillMdPath = Path.Combine(installationPath, "SKILL.md");
        if (!File.Exists(skillMdPath))
        {
            return new SkillMetadata(MetadataReadStatus.Absent, null, null, "SKILL.md was not found.");
        }

        var onDiskName = new FileInfo(skillMdPath).Name;
        if (!string.Equals(onDiskName, "SKILL.md", StringComparison.Ordinal))
        {
            return SkillMetadata.Invalid($"Skill definition file is named '{onDiskName}' instead of the required 'SKILL.md'.");
        }

        string content;
        try
        {
            content = File.ReadAllText(skillMdPath, Encoding.UTF8);
        }
        catch (Exception exception)
        {
            return SkillMetadata.Invalid($"SKILL.md could not be read: {exception.Message}");
        }

        var extraction = ExtractFrontmatter(content);
        if (extraction == FrontmatterResult.NoOpeningDelimiter)
        {
            return SkillMetadata.Invalid("SKILL.md does not start with a YAML frontmatter block delimited by '---'.");
        }

        if (extraction == FrontmatterResult.Unterminated)
        {
            return SkillMetadata.Invalid("SKILL.md has a frontmatter block that is never closed with '---'.");
        }

        var frontmatter = ((FrontmatterResult.Parsed)extraction).Values;
        var declaredName = frontmatter.TryGetValue("name", out var name) ? name : null;
        var description = frontmatter.TryGetValue("description", out var description_) ? description_ : null;

        if (string.IsNullOrWhiteSpace(declaredName))
        {
            return SkillMetadata.Invalid("Frontmatter is missing a 'name' field.");
        }

        if (declaredName!.Length > MaxNameLength || !NamePattern().IsMatch(declaredName))
        {
            return SkillMetadata.Invalid($"Declared name '{declaredName}' is not a valid skill name.");
        }

        if (!string.Equals(declaredName, folderName, StringComparison.Ordinal))
        {
            return SkillMetadata.Invalid($"Declared name '{declaredName}' does not match the folder name '{folderName}'.");
        }

        if (string.IsNullOrWhiteSpace(description))
        {
            return SkillMetadata.Invalid("Frontmatter is missing a 'description' field.");
        }

        if (description!.Length > MaxDescriptionLength)
        {
            return SkillMetadata.Invalid("The 'description' field exceeds 1024 characters.");
        }

        return new SkillMetadata(MetadataReadStatus.Valid, declaredName, description, null);
    }

    private abstract record FrontmatterResult
    {
        public static readonly FrontmatterResult NoOpeningDelimiter = new NoDelimiter();

        public static readonly FrontmatterResult Unterminated = new Unclosed();

        public sealed record Parsed(Dictionary<string, string> Values) : FrontmatterResult;

        private sealed record NoDelimiter : FrontmatterResult;

        private sealed record Unclosed : FrontmatterResult;
    }

    private static FrontmatterResult ExtractFrontmatter(string content)
    {
        var normalized = content.TrimStart('\uFEFF');
        var lines = normalized.Split('\n');
        if (lines.Length == 0 || lines[0].TrimEnd('\r') != "---")
        {
            return FrontmatterResult.NoOpeningDelimiter;
        }

        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 1; index < lines.Length; index++)
        {
            var line = lines[index].TrimEnd('\r');
            if (line == "---")
            {
                return new FrontmatterResult.Parsed(fields);
            }

            var separatorIndex = line.IndexOf(':');
            if (separatorIndex <= 0)
            {
                continue;
            }

            var key = line[..separatorIndex].Trim();
            var value = line[(separatorIndex + 1)..].Trim().Trim('"', '\'');
            fields[key] = value;
        }

        return FrontmatterResult.Unterminated;
    }
}
