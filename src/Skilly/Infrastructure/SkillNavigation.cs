using System.Text.RegularExpressions;

namespace Skilly.Infrastructure;

public static class SkillNavigation
{
    // Source references are provider data, never shell commands or arbitrary URI schemes.
    public static string? SourceUrl(string source)
    {
        source = source.Trim();
        if (source.Any(char.IsControl)) return null;
        if (Regex.IsMatch(source, @"^[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+$"))
            source = "https://github.com/" + source;
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps || uri.UserInfo.Length > 0
            || uri.Query.Length > 0 || uri.Fragment.Length > 0)
            return null;
        return uri.AbsoluteUri;
    }
}
