using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

var arguments = args.ToList();
Record(args);
if (arguments.SequenceEqual(["--version"]))
{
    if (Environment.GetEnvironmentVariable("FAKE_APM_WRONG_BRAND") == "1")
        Console.WriteLine("Another APM version 99.0.0");
    else
        Console.WriteLine($"Agent Package Manager (APM) CLI version {Environment.GetEnvironmentVariable("FAKE_APM_VERSION") ?? "0.28.0"}");
    return 0;
}
if (arguments.Count == 0) return 2;
var operation = arguments[0];
if (Environment.GetEnvironmentVariable("FAKE_APM_FAIL_OPERATION") == operation)
{
    Console.Error.WriteLine($"injected {operation} failure");
    return 19;
}
var home = Environment.GetEnvironmentVariable("HOME") ?? Environment.GetEnvironmentVariable("USERPROFILE")!;
var apmRoot = Path.Combine(home, ".apm");
var canonicalRoot = Path.Combine(home, ".agents", "skills");
var sourceRoot = Environment.GetEnvironmentVariable("FAKE_APM_SOURCE_ROOT")!;
var source = Environment.GetEnvironmentVariable("FAKE_APM_SOURCE") ?? "https://github.com/acme/apm-library.git";

switch (operation)
{
    case "install":
        if (!arguments.Contains("--global") || ValueAfter(arguments, "--target") != "copilot") return 2;
        source = arguments[arguments.IndexOf("--global") + 1];
        var selected = ValuesAfter(arguments, "--skill");
        var explicitSubset = selected.Count > 0;
        if (selected.Count == 0) selected = Directory.EnumerateDirectories(Path.Combine(sourceRoot, "skills")).Select(Path.GetFileName).ToList()!;
        if (Environment.GetEnvironmentVariable("FAKE_APM_FALSE_SUCCESS_OPERATION") != "install")
            Install(selected, source, explicitSubset);
        Console.WriteLine($"Installed {selected.Count} skill(s)");
        return 0;
    case "outdated":
        if (!arguments.SequenceEqual(["outdated", "--global"])) return 2;
        if (Environment.GetEnvironmentVariable("FAKE_APM_BAD_OUTDATED") == "1")
        {
            Console.WriteLine("Everything might be okay");
            return 0;
        }
        var mode = Environment.GetEnvironmentVariable("FAKE_APM_OUTDATED_MODE");
        var current = ReadRevision();
        var latest = Revision(sourceRoot);
        if (mode == "unknown")
            WriteOutdated("acme/apm-library", "main", "-", "unknown", mode);
        else if (mode == "outdated" || !string.Equals(current, latest, StringComparison.Ordinal))
            WriteOutdated("acme/apm-library", "main", latest[..8], "outdated", mode);
        else
            Console.WriteLine("All dependencies are up-to-date");
        return 0;
    case "update":
        if (!arguments.Contains("--global") || !arguments.Contains("--yes") || ValueAfter(arguments, "--target") != "copilot") return 2;
        if (Environment.GetEnvironmentVariable("FAKE_APM_FALSE_SUCCESS_OPERATION") != "update")
            Install(ReadSelected(), source, ManifestHasSkillSubset());
        Console.WriteLine("Update complete");
        return 0;
    case "uninstall":
        if (!arguments.Contains("--global") || arguments.Contains("--dry-run")) return 2;
        if (Environment.GetEnvironmentVariable("FAKE_APM_FALSE_SUCCESS_OPERATION") != "uninstall")
        {
            foreach (var name in ReadSelected())
            {
                var path = Path.Combine(canonicalRoot, name);
                if (Directory.Exists(path)) Directory.Delete(path, true);
            }
            if (Directory.Exists(Path.Combine(apmRoot, "apm_modules"))) Directory.Delete(Path.Combine(apmRoot, "apm_modules"), true);
            if (File.Exists(Path.Combine(apmRoot, "apm.lock.yaml"))) File.Delete(Path.Combine(apmRoot, "apm.lock.yaml"));
            Directory.CreateDirectory(apmRoot);
            File.WriteAllText(Path.Combine(apmRoot, "apm.yml"), "name: fake-global\nversion: 1.0.0\ndependencies:\n  apm: []\n");
        }
        Console.WriteLine("Uninstall complete");
        return 0;
    default:
        return 2;
}

static void WriteOutdated(string package, string current, string latest, string status, string? mode)
{
    if (mode == "rich") Console.WriteLine($"│ {package} │ {current} │ {latest} │ {status} │ git branch │");
    else Console.WriteLine($"{package} {current} {latest} {status} git branch");
}

void Install(IReadOnlyList<string> selected, string requestedSource, bool explicitSubset)
{
    Directory.CreateDirectory(apmRoot);
    Directory.CreateDirectory(canonicalRoot);
    foreach (var name in selected)
    {
        var destination = Path.Combine(canonicalRoot, name);
        if (Directory.Exists(destination)) Directory.Delete(destination, true);
        CopyDirectory(Path.Combine(sourceRoot, "skills", name), destination);
        if (Environment.GetEnvironmentVariable("FAKE_APM_EXTRA_FILE") == "1") File.WriteAllText(Path.Combine(destination, "unexpected.txt"), "not recorded by the provider lock");
        if (Environment.GetEnvironmentVariable("FAKE_APM_CLAUDE_COPY") == "1")
            CopyDirectory(Path.Combine(sourceRoot, "skills", name), Path.Combine(home, ".claude", "skills", name));
    }
    Directory.CreateDirectory(Path.Combine(apmRoot, "apm_modules", "acme", "apm-library"));
    File.WriteAllText(Path.Combine(apmRoot, "apm_modules", "acme", "apm-library", "source.txt"), Revision(sourceRoot));
    var manifest = new StringBuilder("name: fake-global\nversion: 1.0.0\ntargets:\n  - copilot\ndependencies:\n  apm:\n    - git: ")
        .Append(requestedSource).Append("\n      ref: main\n");
    if (explicitSubset)
    {
        manifest.Append("      skills:\n");
        foreach (var name in selected) manifest.Append("        - ").Append(name).Append('\n');
    }
    File.WriteAllText(Path.Combine(apmRoot, "apm.yml"), manifest.ToString());
    if (Environment.GetEnvironmentVariable("FAKE_APM_BAD_LOCK") == "1")
    {
        File.WriteAllText(Path.Combine(apmRoot, "apm.lock.yaml"), "lockfile_version: '99'\n");
        return;
    }
    var revision = Revision(sourceRoot);
    var lockText = new StringBuilder("lockfile_version: '1'\ngenerated_at: '2026-08-26T00:00:00Z'\napm_version: '0.28.0'\ndependencies:\n  - repo_url: ")
        .Append(requestedSource).Append("\n    resolved_ref: main\n    resolved_commit: ").Append(revision)
        .Append("\n    package_type: skill_bundle\n");
    if (explicitSubset)
    {
        lockText.Append("    skill_subset:\n");
        foreach (var name in selected) lockText.Append("      - ").Append(name).Append('\n');
    }
    lockText.Append("    deployed_files:\n");
    foreach (var name in selected)
    {
        lockText.Append("      - .agents/skills/").Append(name).Append('\n');
        lockText.Append("      - .agents/skills/").Append(name).Append("/SKILL.md\n");
    }
    if (Environment.GetEnvironmentVariable("FAKE_APM_EXTRA_DEPLOYMENT") == "1") lockText.Append("      - .copilot/agents/unexpected.agent.md\n");
    if (Environment.GetEnvironmentVariable("FAKE_APM_TRAVERSAL") == "1") lockText.Append("      - .agents/skills/alpha/../../outside.txt\n");
    lockText.Append("    deployed_file_hashes:\n");
    foreach (var name in selected)
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(Path.Combine(canonicalRoot, name, "SKILL.md")))).ToLowerInvariant();
        if (Environment.GetEnvironmentVariable("FAKE_APM_BAD_HASH") == "1") hash = new string('0', 64);
        lockText.Append("      .agents/skills/").Append(name).Append("/SKILL.md: ").Append(hash).Append('\n');
    }
    File.WriteAllText(Path.Combine(apmRoot, "apm.lock.yaml"), lockText.ToString());
}

bool ManifestHasSkillSubset()
    => File.Exists(Path.Combine(apmRoot, "apm.yml"))
       && File.ReadAllText(Path.Combine(apmRoot, "apm.yml")).Contains("skills:", StringComparison.Ordinal);

List<string> ReadSelected()
{
    var lockPath = Path.Combine(apmRoot, "apm.lock.yaml");
    if (!File.Exists(lockPath)) return [];
    return File.ReadAllLines(lockPath)
        .Select(line => line.Trim())
        .Where(line => line.StartsWith("- .agents/skills/", StringComparison.Ordinal))
        .Select(line => line["- .agents/skills/".Length..].Split('/')[0])
        .Distinct(StringComparer.Ordinal)
        .ToList();
}

string ReadRevision()
{
    var lockPath = Path.Combine(apmRoot, "apm.lock.yaml");
    var line = File.ReadLines(lockPath).First(value => value.TrimStart().StartsWith("resolved_commit:", StringComparison.Ordinal));
    return line.Split(':', 2)[1].Trim();
}

static string Revision(string root)
{
    var content = string.Join('|', Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
        .OrderBy(path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))));
    return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(content))).ToLowerInvariant()[..40];
}

static string? ValueAfter(List<string> values, string option)
{
    var index = values.IndexOf(option);
    return index >= 0 && index + 1 < values.Count ? values[index + 1] : null;
}

static List<string> ValuesAfter(List<string> values, string option)
{
    var result = new List<string>();
    for (var index = 0; index < values.Count - 1; index++) if (values[index] == option) result.Add(values[index + 1]);
    return result;
}

static void CopyDirectory(string source, string destination)
{
    Directory.CreateDirectory(destination);
    foreach (var directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var target = Path.Combine(destination, Path.GetRelativePath(source, file));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, true);
    }
}

static void Record(string[] invocation)
{
    var path = Environment.GetEnvironmentVariable("FAKE_APM_INVOCATIONS");
    if (string.IsNullOrWhiteSpace(path)) return;
    Directory.CreateDirectory(Path.GetDirectoryName(path)!);
    File.AppendAllText(path, JsonSerializer.Serialize(invocation) + Environment.NewLine);
}
