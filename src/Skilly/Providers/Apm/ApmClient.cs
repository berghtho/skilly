using System.IO;
using System.Text.RegularExpressions;
using Skilly.Infrastructure;

namespace Skilly.Providers.Apm;

public sealed class ApmClient(ProcessRunner runner, string executable = "apm.exe")
{
    public const string Provider = "Microsoft microsoft/apm apm-cli";
    public const string ProviderId = "apm";
    public static readonly Version MinimumVersion = new(0, 28, 0);
    private readonly string _executable = ResolveExecutable(executable);

    public ProviderReadiness GetReadiness()
    {
        try
        {
            var result = Run(["--version"]);
            RequireExit(result, "APM compatibility probe");
            var version = ParseBrandedVersion(result.CombinedOutput);
            if (version < MinimumVersion)
            {
                return Unavailable($"apm-cli {version} is unsupported; {MinimumVersion} or newer is required.");
            }
            return new ProviderReadiness(true, Provider, $"{Provider} {version} is ready. Skilly will not install or update APM.", version.ToString());
        }
        catch (Exception exception)
        {
            return Unavailable(exception.Message);
        }
    }

    public string RequireSupportedVersion()
    {
        var result = Run(["--version"]);
        RequireExit(result, "APM compatibility probe");
        var version = ParseBrandedVersion(result.CombinedOutput);
        if (version < MinimumVersion) throw new ProviderFailure($"apm-cli {version} is unsupported; {MinimumVersion} or newer is required.");
        return version.ToString();
    }

    public ProcessResult Install(string source, IReadOnlyList<string> skills, IReadOnlyDictionary<string, string?>? environment = null)
    {
        ValidateCredentialFreeReference(source);
        var arguments = new List<string> { "install", "--global", source, "--target", "copilot" };
        foreach (var skill in skills) { arguments.Add("--skill"); arguments.Add(skill); }
        return Run(arguments, TimeSpan.FromMinutes(10), environment);
    }

    public ProcessResult Outdated() => Run(["outdated", "--global"], TimeSpan.FromMinutes(5));

    public ProcessResult Update(string package)
        => Run(["update", "--global", package, "--yes", "--target", "copilot"], TimeSpan.FromMinutes(10));

    public ProcessResult Uninstall(string package)
        => Run(["uninstall", "--global", package], TimeSpan.FromMinutes(10));

    public IReadOnlyList<ApmOutdatedRow> ParseOutdated(ProcessResult result)
    {
        RequireExit(result, "Read-only APM outdated Check");
        var output = StripAnsi(result.CombinedOutput);
        var rows = new List<ApmOutdatedRow>();
        foreach (var line in output.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Contains("All dependencies are up-to-date", StringComparison.Ordinal)
                || trimmed.Contains("No remote dependencies to check", StringComparison.Ordinal)) continue;
            if (trimmed.Contains('│'))
            {
                var columns = trimmed.Split('│', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
                if (columns.Length >= 4 && columns[3] is "up-to-date" or "outdated" or "unknown")
                {
                    rows.Add(new ApmOutdatedRow(columns[0], columns[1], columns[2], columns[3]));
                    continue;
                }
            }
            var match = Regex.Match(trimmed, @"^(?<package>\S+)\s+(?<current>\S+)\s+(?<latest>\S+(?:\s+\([^)]*\))?)\s+(?<status>up-to-date|outdated|unknown)(?:\s+.*)?$", RegexOptions.CultureInvariant);
            if (match.Success)
            {
                rows.Add(new ApmOutdatedRow(match.Groups["package"].Value, match.Groups["current"].Value, match.Groups["latest"].Value, match.Groups["status"].Value));
            }
        }
        if (rows.Count == 0 && !output.Contains("All dependencies are up-to-date", StringComparison.Ordinal)
            && !output.Contains("No remote dependencies to check", StringComparison.Ordinal))
        {
            throw new ProviderFailure($"APM outdated returned successful but unrecognized output; Check failed closed. {SensitiveDataRedactor.Redact(output).Trim()}");
        }
        return rows;
    }

    public static void RequireExit(ProcessResult result, string operation)
    {
        if (!result.Succeeded) throw new ProviderFailure($"{operation} failed with exit code {result.ExitCode}. {SensitiveDataRedactor.Redact(result.CombinedOutput).Trim()}");
    }

    private ProcessResult Run(IReadOnlyList<string> arguments, TimeSpan? timeout = null, IReadOnlyDictionary<string, string?>? environment = null)
    {
        try { return runner.Run(_executable, arguments, timeout, environment); }
        catch (Exception exception) when (exception is not ProviderFailure)
        {
            throw new ProviderFailure($"The Microsoft apm executable is unavailable: {exception.Message}");
        }
    }

    private static Version ParseBrandedVersion(string output)
    {
        var clean = StripAnsi(output).Trim();
        var match = Regex.Match(clean, @"^Agent Package Manager \(APM\) CLI version (?<version>\d+\.\d+\.\d+)(?:\s+\([0-9a-fA-F]+\))?$", RegexOptions.CultureInvariant);
        return match.Success && Version.TryParse(match.Groups["version"].Value, out var version)
            ? version
            : throw new ProviderFailure($"The executable is not recognized as Microsoft microsoft/apm apm-cli; branded version output was '{SensitiveDataRedactor.Redact(clean)}'.");
    }

    private static void ValidateCredentialFreeReference(string source)
    {
        if (Uri.TryCreate(source, UriKind.Absolute, out var uri) && (!string.IsNullOrEmpty(uri.UserInfo) || !string.IsNullOrEmpty(uri.Query)))
        {
            throw new ProviderFailure("APM source references must not embed credentials or query secrets; use APM's existing credential integrations.");
        }
    }

    private static ProviderReadiness Unavailable(string diagnostic)
        => new(false, Provider, $"{Provider} provider unavailable: {diagnostic}");

    private static string StripAnsi(string value)
        => Regex.Replace(value, "\\x1B(?:[@-Z\\\\-_]|\\[[0-?]*[ -/]*[@-~])", string.Empty, RegexOptions.CultureInvariant);

    private static string ResolveExecutable(string executable)
    {
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar)) return executable;
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var candidate = Path.Combine(directory, executable);
            if (File.Exists(candidate)) return candidate;
        }
        return executable;
    }
}
