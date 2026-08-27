using System.Diagnostics;
using System.Text;

namespace Skilly.Infrastructure;

public sealed record ProcessResult(int ExitCode, string StandardOutput, string StandardError)
{
    public bool Succeeded => ExitCode == 0;

    public string CombinedOutput => string.IsNullOrWhiteSpace(StandardError)
        ? StandardOutput
        : StandardOutput + Environment.NewLine + StandardError;
}

public sealed class ProcessRunner(
    RollingLog log,
    IReadOnlyDictionary<string, string?>? environment = null)
{
    public ProcessResult Run(
        string fileName,
        IReadOnlyList<string> arguments,
        TimeSpan? timeout = null,
        IReadOnlyDictionary<string, string?>? additionalEnvironment = null)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach (var variable in environment)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        if (additionalEnvironment is not null)
        {
            foreach (var variable in additionalEnvironment)
            {
                startInfo.Environment[variable.Key] = variable.Value;
            }
        }

        log.Info($"Process start: {fileName} {Describe(arguments)}");
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Failed to start '{fileName}'.");
        }

        var stdoutTask = process.StandardOutput.ReadToEndAsync();
        var stderrTask = process.StandardError.ReadToEndAsync();
        var effectiveTimeout = timeout ?? TimeSpan.FromSeconds(60);
        if (!process.WaitForExit((int)effectiveTimeout.TotalMilliseconds))
        {
            try
            {
                process.Kill(entireProcessTree: true);
                process.WaitForExit(5000);
            }
            catch (Exception exception)
            {
                log.Error($"Process '{fileName}' exceeded timeout and could not be killed cleanly.", exception);
            }

            throw new TimeoutException($"'{fileName}' did not finish within {effectiveTimeout.TotalSeconds:F0}s.");
        }

        var result = new ProcessResult(process.ExitCode, stdoutTask.Result, stderrTask.Result);
        log.Info($"Process exit: {fileName} code={result.ExitCode}");
        return result;
    }

    private static string Describe(IReadOnlyList<string> arguments)
    {
        var redactNext = false;
        return string.Join(" ", arguments.Select(argument =>
        {
            var sensitiveOption = IsSensitiveOption(argument);
            var displayed = redactNext
                ? "<redacted>"
                : sensitiveOption && argument.Contains('=')
                    ? argument.Split('=', 2)[0] + "=<redacted>"
                    : SensitiveDataRedactor.Redact(argument);
            redactNext = sensitiveOption && !argument.Contains('=');
            if (displayed.Length == 0 || displayed.Any(char.IsWhiteSpace))
            {
                return "\"" + displayed.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            }

            return displayed;
        }));
    }

    private static bool IsSensitiveOption(string argument)
    {
        var option = argument.Split('=', 2)[0];
        return option.Equals("--password", StringComparison.OrdinalIgnoreCase)
               || option.Equals("--token", StringComparison.OrdinalIgnoreCase)
               || option.Equals("--secret", StringComparison.OrdinalIgnoreCase)
               || option.Equals("--api-key", StringComparison.OrdinalIgnoreCase)
               || option.Equals("--access-token", StringComparison.OrdinalIgnoreCase);
    }
}
