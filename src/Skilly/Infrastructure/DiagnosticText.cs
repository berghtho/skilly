using System.Text.RegularExpressions;

namespace Skilly.Infrastructure;

internal static partial class DiagnosticText
{
    public static string Clean(string value)
        => SensitiveDataRedactor.Redact(ControlCharacters().Replace(TerminalEscapes().Replace(value, string.Empty), string.Empty));

    public static string SingleLine(string value, int limit = 600)
    {
        var text = Whitespace().Replace(Clean(value), " ").Trim();
        return text.Length > limit ? text[..limit] + "…" : text;
    }

    public static string CliFailure(ProcessResult result)
    {
        var lines = Clean(result.CombinedOutput).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var reason = lines.FirstOrDefault(line => FailureWords().IsMatch(line)) ?? lines.LastOrDefault() ?? "(no output)";
        return SingleLine(reason, 300);
    }

    [GeneratedRegex("\\x1B(?:\\[[0-?]*[ -/]*[@-~]|\\][^\\x07\\x1B]*(?:\\x07|\\x1B\\\\)|[ -/]*[@-~])", RegexOptions.CultureInvariant)]
    private static partial Regex TerminalEscapes();

    [GeneratedRegex("[\\x00-\\x08\\x0B-\\x1F\\x7F]", RegexOptions.CultureInvariant)]
    private static partial Regex ControlCharacters();

    [GeneratedRegex(@"\s+", RegexOptions.CultureInvariant)]
    private static partial Regex Whitespace();

    [GeneratedRegex(@"\b(error|failed|fatal|no matching skills|not found|cannot|unable|denied)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex FailureWords();
}
