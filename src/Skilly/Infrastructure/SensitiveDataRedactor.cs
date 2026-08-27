using System.Text.RegularExpressions;

namespace Skilly.Infrastructure;

internal static partial class SensitiveDataRedactor
{
    private const string Redacted = "<redacted>";

    public static string Redact(string value)
    {
        if (string.IsNullOrEmpty(value))
        {
            return value;
        }

        var redacted = UrlUserInfo().Replace(value, "${scheme}" + Redacted + "@");
        redacted = SensitiveQueryValue().Replace(redacted, "${prefix}" + Redacted);
        redacted = GitHubToken().Replace(redacted, Redacted);
        redacted = CommonApiToken().Replace(redacted, Redacted);
        return redacted;
    }

    [GeneratedRegex(@"(?<scheme>https?://)[^\s/@:]+(?::[^\s/@]*)?@", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex UrlUserInfo();

    [GeneratedRegex(@"(?<prefix>[?&](?:access_token|api[_-]?key|auth|password|secret|signature|token)=)[^&#\s]*", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SensitiveQueryValue();

    [GeneratedRegex(@"\b(?:gh[opsu]_[A-Za-z0-9_]{20,}|github_pat_[A-Za-z0-9_]{20,})\b", RegexOptions.CultureInvariant)]
    private static partial Regex GitHubToken();

    [GeneratedRegex(@"\bsk-[A-Za-z0-9_-]{20,}\b", RegexOptions.CultureInvariant)]
    private static partial Regex CommonApiToken();
}
