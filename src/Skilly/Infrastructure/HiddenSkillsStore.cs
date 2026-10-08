using System.IO;
using System.Text.Json;

namespace Skilly.Infrastructure;

/// <summary>Display preferences only; never changes files or management authority.</summary>
public sealed class HiddenSkillsStore(string path)
{
    public IReadOnlyList<string> Load() => File.Exists(path)
        ? JsonSerializer.Deserialize<List<string>>(File.ReadAllText(path)) ?? [] : [];

    public void Save(IEnumerable<string> paths)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + ".tmp";
        File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(paths.Order(StringComparer.OrdinalIgnoreCase)));
        File.Move(temporary, path, overwrite: true);
    }
}
