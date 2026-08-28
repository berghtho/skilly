using System.Text.Json;
using Skilly.Infrastructure;
using Skilly.Skills;

RecordInvocation(args);

if (args is ["--version"])
{
    Console.WriteLine(Environment.GetEnvironmentVariable("FAKE_SKILLS_NODE_VERSION") ?? "v22.20.0");
    return 0;
}

if (args.Length < 3 || args[0] != "--yes" || args[1] != "skills@1.5.23")
{
    Console.Error.WriteLine("FakeSkills requires the exact pinned npx package arguments.");
    return 2;
}

var command = args[2];
if (command == "--version")
{
    if (ShouldFail("readiness")) return 17;
    Console.WriteLine(Environment.GetEnvironmentVariable("FAKE_SKILLS_PROVIDER_VERSION") ?? "1.5.23");
    return 0;
}

if (ShouldFail(command))
{
    Console.Error.WriteLine($"injected {command} failure");
    return 17;
}

var home = Environment.GetEnvironmentVariable("USERPROFILE")
           ?? throw new InvalidOperationException("USERPROFILE is required.");
var sourceRoot = Environment.GetEnvironmentVariable("FAKE_SKILLS_SOURCE_ROOT")
                 ?? throw new InvalidOperationException("FAKE_SKILLS_SOURCE_ROOT is required.");
var canonicalRoot = Path.Combine(home, ".agents", "skills");
var claudeRoot = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR") is { Length: > 0 } claude
    ? Path.Combine(claude, "skills")
    : Path.Combine(home, ".claude", "skills");
var stateHome = Environment.GetEnvironmentVariable("XDG_STATE_HOME");
var lockPath = string.IsNullOrWhiteSpace(stateHome)
    ? Path.Combine(home, ".agents", ".skill-lock.json")
    : Path.Combine(stateHome, "skills", ".skill-lock.json");

if (command == "add" && args.Contains("--list", StringComparer.Ordinal))
{
    // Mirrors the real skills CLI shape: a source title line without indentation
    // and blank clack frame lines between every name and description.
    Console.WriteLine("◇ Available Skills");
    Console.WriteLine("Fixture Skills");
    foreach (var directory in Directory.EnumerateDirectories(Path.Combine(sourceRoot, "skills")).OrderBy(static path => path, StringComparer.Ordinal))
    {
        var name = Path.GetFileName(directory);
        Console.WriteLine("│");
        Console.WriteLine($"│    {name}");
        Console.WriteLine("│");
        Console.WriteLine($"│      {name} from deterministic provider fixture");
    }
    Console.WriteLine("│");
    Console.WriteLine("└ Use --skill <name> to install specific skills");
    return 0;
}

if (FalseSuccess(command))
{
    Console.WriteLine($"{command} completed");
    return 0;
}

if (command == "add")
{
    RequireExactAddArguments(args);
    var source = args[3];
    var selected = ValueAfter(args, "--skill");
    Install(selected, source);
    Console.WriteLine($"Installed {selected}");
    return 0;
}

if (command == "update")
{
    RequireArguments(args, "--global", "--yes");
    var selected = args[3];
    var lockEntries = ReadLock();
    var matching = lockEntries.Single(entry => Sanitize(entry.Key) == Sanitize(selected));
    Install(matching.Key, matching.Value.Source);
    Console.WriteLine($"Updated {selected}");
    return 0;
}

if (command == "remove")
{
    RequireArguments(args, "--global", "--yes");
    var selected = args[3];
    var folder = Sanitize(selected);
    DeleteEntry(Path.Combine(claudeRoot, folder));
    DeleteEntry(Path.Combine(canonicalRoot, folder));
    var entries = ReadLock();
    foreach (var key in entries.Keys.Where(key => Sanitize(key) == folder).ToList()) entries.Remove(key);
    WriteLock(entries);
    Console.WriteLine($"Removed {selected}");
    return 0;
}

if (command == "list" && args.Contains("--global", StringComparer.Ordinal) && args.Contains("--json", StringComparer.Ordinal))
{
    var entries = ReadLock();
    var listed = Directory.Exists(canonicalRoot)
        ? Directory.EnumerateDirectories(canonicalRoot)
            .Where(path => !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint))
            .Select(path =>
            {
                var match = entries.SingleOrDefault(entry => Sanitize(entry.Key) == Path.GetFileName(path));
                return new
                {
                    name = match.Key ?? Path.GetFileName(path),
                    path,
                    scope = "global",
                    source = match.Value?.Source,
                    sourceUrl = match.Value?.SourceUrl,
                    sourceType = match.Value?.SourceType,
                };
            }).ToList()
        : [];
    Console.WriteLine(JsonSerializer.Serialize(listed));
    return 0;
}

Console.Error.WriteLine($"Unsupported FakeSkills operation: {string.Join(' ', args)}");
return 2;

void Install(string selected, string source)
{
    var folder = Sanitize(selected);
    var sourcePath = Path.Combine(sourceRoot, "skills", folder);
    if (!Directory.Exists(sourcePath)) throw new InvalidOperationException($"Fixture Skill '{folder}' does not exist.");
    var canonical = Path.Combine(canonicalRoot, folder);
    var claude = Path.Combine(claudeRoot, folder);
    DeleteEntry(claude);
    DeleteEntry(canonical);
    CopyDirectory(sourcePath, canonical);
    if (string.Equals(Environment.GetEnvironmentVariable("FAKE_SKILLS_COPY_FALLBACK"), "1", StringComparison.Ordinal))
    {
        CopyDirectory(sourcePath, claude);
    }
    else
    {
        Junction.Create(claude, canonical);
    }

    if (!string.Equals(Environment.GetEnvironmentVariable("FAKE_SKILLS_LOCK_FAILURE"), "1", StringComparison.Ordinal))
    {
        var entries = ReadLock();
        entries[selected] = new LockEntry
        {
            Source = source,
            SourceUrl = source,
            SourceType = "git",
            SkillPath = $"skills/{folder}/SKILL.md",
            SkillFolderHash = PayloadHasher.HashFolder(sourcePath),
            InstalledAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
            UpdatedAt = DateTimeOffset.Parse("2026-01-02T03:04:05Z"),
        };
        WriteLock(entries);
    }
}

Dictionary<string, LockEntry> ReadLock()
{
    if (!File.Exists(lockPath)) return new Dictionary<string, LockEntry>(StringComparer.OrdinalIgnoreCase);
    using var document = JsonDocument.Parse(File.ReadAllText(lockPath));
    return document.RootElement.GetProperty("skills").Deserialize<Dictionary<string, LockEntry>>(
               new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
           ?? new Dictionary<string, LockEntry>(StringComparer.OrdinalIgnoreCase);
}

void WriteLock(Dictionary<string, LockEntry> entries)
{
    Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
    File.WriteAllText(lockPath, JsonSerializer.Serialize(new { version = 3, skills = entries, dismissed = new { } }, new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
    }));
}

static void RequireExactAddArguments(string[] arguments)
{
    RequireArguments(arguments, "--global", "--yes", "--skill", "--agent");
    var expectedAgents = new[] { "opencode", "codex", "claude-code", "github-copilot" };
    var agents = arguments.Select((value, index) => (value, index))
        .Where(static item => item.value == "--agent")
        .Select(item => arguments[item.index + 1]).ToArray();
    if (!agents.SequenceEqual(expectedAgents)) throw new InvalidOperationException("All four explicit agents are required in settled order.");
}

static void RequireArguments(string[] arguments, params string[] required)
{
    foreach (var value in required)
    {
        if (!arguments.Contains(value, StringComparer.Ordinal)) throw new InvalidOperationException($"Required argument '{value}' is missing.");
    }
}

static string ValueAfter(string[] arguments, string option)
{
    var index = Array.IndexOf(arguments, option);
    return index >= 0 && index + 1 < arguments.Length ? arguments[index + 1] : throw new InvalidOperationException($"{option} has no value.");
}

static string Sanitize(string name)
    => System.Text.RegularExpressions.Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9._]+", "-").Trim('.', '-');

static void CopyDirectory(string source, string destination)
{
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
    }
}

static void DeleteEntry(string path)
{
    if (!Directory.Exists(path)) return;
    var recursive = !File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint);
    Directory.Delete(path, recursive);
}

static bool ShouldFail(string operation)
    => string.Equals(Environment.GetEnvironmentVariable("FAKE_SKILLS_FAIL_OPERATION"), operation, StringComparison.Ordinal);

static bool FalseSuccess(string operation)
    => string.Equals(Environment.GetEnvironmentVariable("FAKE_SKILLS_FALSE_SUCCESS_OPERATION"), operation, StringComparison.Ordinal);

static void RecordInvocation(string[] arguments)
{
    var path = Environment.GetEnvironmentVariable("FAKE_SKILLS_INVOCATIONS");
    if (!string.IsNullOrWhiteSpace(path)) File.AppendAllText(path, JsonSerializer.Serialize(arguments) + Environment.NewLine);
}

sealed class LockEntry
{
    public string Source { get; set; } = string.Empty;
    public string? SourceUrl { get; set; }
    public string SourceType { get; set; } = string.Empty;
    public string? SkillPath { get; set; }
    public string SkillFolderHash { get; set; } = string.Empty;
    public DateTimeOffset? InstalledAt { get; set; }
    public DateTimeOffset? UpdatedAt { get; set; }
}
