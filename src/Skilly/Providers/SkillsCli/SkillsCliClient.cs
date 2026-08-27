using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using Skilly.Infrastructure;

namespace Skilly.Providers.SkillsCli;

public sealed record SkillsCliListedSkill(string Name, string Path, string Scope, string? Source, string? SourceUrl, string? SourceType);

public sealed class SkillsCliClient
{
    public const string Version = "1.5.23";
    public const string Package = "skills@1.5.23";
    public static readonly Version MinimumNodeVersion = new(22, 20, 0);

    private static readonly string[] HarnessArguments =
    [
        "--agent", "opencode",
        "--agent", "codex",
        "--agent", "claude-code",
        "--agent", "github-copilot",
    ];

    private readonly ProcessRunner _runner;
    private readonly string _nodeExecutable;
    private readonly string _npmExecutable;
    private readonly IReadOnlyList<string> _npmPrefix;
    private readonly string _npxExecutable;
    private readonly IReadOnlyList<string> _npxPrefix;
    private readonly string _gitExecutable;

    public SkillsCliClient(
        ProcessRunner runner,
        string nodeExecutable = "node.exe",
        string npmExecutable = "npm.cmd",
        string npxExecutable = "npx.cmd",
        string gitExecutable = "git.exe")
    {
        _runner = runner;
        _nodeExecutable = ResolveExecutable(nodeExecutable);
        _gitExecutable = ResolveExecutable(gitExecutable);
        (_npmExecutable, _npmPrefix) = ResolveNpmTool(npmExecutable, "npm-cli.js");
        (_npxExecutable, _npxPrefix) = ResolveNpmTool(npxExecutable, "npx-cli.js");
    }

    public ProviderReadiness GetReadiness()
    {
        try
        {
            var node = RunRequired(_nodeExecutable, ["--version"], "Node.js");
            var nodeVersion = ParseNodeVersion(node.StandardOutput);
            if (nodeVersion < MinimumNodeVersion)
            {
                return Unavailable($"Node.js {nodeVersion} is unsupported; Node.js {MinimumNodeVersion} or newer is required.");
            }

            RunRequired(_npmExecutable, [.. _npmPrefix, "--version"], "npm");
            RunRequired(_npxExecutable, [.. _npxPrefix, "--version"], "npx");
            RunRequired(_gitExecutable, ["--version"], "Git");
            var provider = RunPinned(["--version"]);
            RequireExit(provider, "Pinned provider compatibility probe");
            if (!string.Equals(provider.StandardOutput.Trim(), Version, StringComparison.Ordinal))
            {
                return Unavailable($"Pinned {Package} returned unexpected version output '{provider.StandardOutput.Trim()}'.");
            }

            return new ProviderReadiness(
                true,
                Package,
                $"{Package} is ready (Node.js {nodeVersion}; npm, npx, Git, and registry network available). Source authentication is verified during inspection.",
                Version);
        }
        catch (Exception exception)
        {
            return Unavailable(exception.Message);
        }
    }

    public ProcessResult Inspect(string source)
        => RunPinned(["add", source, "--list"], timeout: TimeSpan.FromMinutes(5));

    public ProcessResult Install(string source, string skillName, IReadOnlyDictionary<string, string?>? environment = null)
        => RunPinned(
            ["add", source, "--global", "--yes", "--skill", skillName, .. HarnessArguments],
            TimeSpan.FromMinutes(5),
            environment);

    public ProcessResult Update(string skillName)
        => RunPinned(["update", skillName, "--global", "--yes"], TimeSpan.FromMinutes(5));

    public ProcessResult Uninstall(string skillName)
        => RunPinned(["remove", skillName, "--global", "--yes"], TimeSpan.FromMinutes(5));

    public IReadOnlyList<SkillsCliListedSkill> ListGlobal(IReadOnlyDictionary<string, string?>? environment = null)
    {
        var result = RunPinned(["list", "--global", "--json"], TimeSpan.FromMinutes(2), environment);
        RequireExit(result, "Global provider inventory");
        try
        {
            return JsonSerializer.Deserialize<List<SkillsCliListedSkill>>(
                       result.StandardOutput,
                       new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                   ?? throw new JsonException("The JSON result was null.");
        }
        catch (JsonException exception)
        {
            throw new ProviderFailure($"{Package} returned invalid global inventory JSON: {exception.Message}");
        }
    }

    public SkillsCliInspection ParseInspection(string source, ProcessResult result)
    {
        RequireExit(result, "Read-only source inspection");
        var output = StripAnsi(result.StandardOutput + Environment.NewLine + result.StandardError);
        var lines = output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n');
        var availableIndex = Array.FindIndex(lines, static line => line.Contains("Available Skills", StringComparison.Ordinal));
        if (availableIndex < 0)
        {
            throw new ProviderFailure($"{Package} inspection output did not contain the expected Available Skills section.");
        }

        var skills = new List<SkillsCliSourceSkill>();
        for (var index = availableIndex + 1; index < lines.Length; index++)
        {
            var line = RemoveClackPrefix(lines[index]);
            if (line.Contains("Use --skill", StringComparison.Ordinal))
            {
                break;
            }
            var indentation = LeadingSpaces(line);
            if (string.IsNullOrWhiteSpace(line) || indentation < 2)
            {
                continue;
            }

            var name = line.Trim();
            var description = string.Empty;
            if (index + 1 < lines.Length)
            {
                var next = RemoveClackPrefix(lines[index + 1]);
                if (!string.IsNullOrWhiteSpace(next) && LeadingSpaces(next) > indentation)
                {
                    description = next.Trim();
                    index++;
                }
            }
            if (name.Length > 0)
            {
                skills.Add(new SkillsCliSourceSkill(name, description));
            }
        }

        if (skills.Count == 0 || skills.Select(static skill => skill.Name).Distinct(StringComparer.Ordinal).Count() != skills.Count)
        {
            throw new ProviderFailure($"{Package} inspection output could not be reconciled to unique Source Skills.");
        }

        return new SkillsCliInspection(source, NormalizeSource(source), Version, skills);
    }

    public static string SanitizeName(string name)
    {
        var sanitized = Regex.Replace(name.ToLowerInvariant(), "[^a-z0-9._]+", "-", RegexOptions.CultureInvariant)
            .Trim('.', '-');
        return sanitized.Length > 255 ? sanitized[..255] : sanitized;
    }

    public static string NormalizeSource(string source) => source.Trim().Replace('\\', '/');

    public static void RequireExit(ProcessResult result, string operation)
    {
        if (!result.Succeeded)
        {
            throw new ProviderFailure($"{operation} failed with exit code {result.ExitCode}. {result.CombinedOutput.Trim()}");
        }
    }

    private ProcessResult RunPinned(
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? environment = null)
        => _runner.Run(_npxExecutable, [.. _npxPrefix, "--yes", Package, .. arguments], timeout, environment);

    private ProcessResult RunRequired(string executable, IReadOnlyList<string> arguments, string name)
    {
        try
        {
            var result = _runner.Run(executable, arguments);
            RequireExit(result, $"{name} prerequisite check");
            return result;
        }
        catch (Exception exception) when (exception is not ProviderFailure)
        {
            throw new ProviderFailure($"{name} prerequisite is unavailable: {exception.Message}");
        }
    }

    private static Version ParseNodeVersion(string output)
    {
        var value = output.Trim().TrimStart('v', 'V').Split('-', 2)[0];
        return System.Version.TryParse(value, out var version)
            ? version
            : throw new ProviderFailure($"Node.js returned unrecognized version output '{output.Trim()}'.");
    }

    private static ProviderReadiness Unavailable(string diagnostic)
        => new(false, Package, $"{Package} provider unavailable: {diagnostic}");

    private static string StripAnsi(string value)
        => Regex.Replace(value, "\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])", string.Empty, RegexOptions.CultureInvariant);

    private static string RemoveClackPrefix(string line)
    {
        var trimmedEnd = line.TrimEnd();
        var prefix = trimmedEnd.TrimStart();
        if (prefix.StartsWith("│", StringComparison.Ordinal) || prefix.StartsWith("|", StringComparison.Ordinal))
        {
            var markerIndex = trimmedEnd.IndexOf(prefix[0]);
            return trimmedEnd[(markerIndex + 1)..];
        }
        return trimmedEnd;
    }

    private static int LeadingSpaces(string value)
        => value.TakeWhile(static character => character == ' ').Count();

    private (string Executable, IReadOnlyList<string> Prefix) ResolveNpmTool(string executable, string scriptName)
    {
        var resolved = ResolveExecutable(executable);
        if (!resolved.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase))
        {
            return (resolved, Array.Empty<string>());
        }

        var script = Path.Combine(Path.GetDirectoryName(resolved)!, "node_modules", "npm", "bin", scriptName);
        return (_nodeExecutable, [script]);
    }

    private static string ResolveExecutable(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar))
        {
            return executable;
        }
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }
        return executable;
    }
}
