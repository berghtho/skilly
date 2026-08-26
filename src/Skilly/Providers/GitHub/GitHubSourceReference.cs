using System.IO;

namespace Skilly.Providers.GitHub;

public sealed record GitHubSourceReference(
    string Original,
    string Host,
    string Owner,
    string Repository,
    string? RequestedRef,
    string? RequestedPath,
    IReadOnlyList<string>? TreeSegments = null)
{
    public static bool TryParse(string input, out GitHubSourceReference reference, out string error)
    {
        reference = null!;
        error = string.Empty;

        var trimmed = input.Trim().TrimEnd('/');
        if (trimmed.Length == 0)
        {
            error = "The source reference is empty.";
            return false;
        }

        if (System.Text.RegularExpressions.Regex.IsMatch(
                trimmed,
                "(?:^|/)(?:%2e(?:%2e)?|\\.\\.?)(?:/|$)",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant))
        {
            error = "The source URL contains encoded path traversal segments.";
            return false;
        }

        Uri? uri;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri) || uri.Scheme != "https")
        {
            error = $"'{trimmed}' is not an absolute HTTPS URL. Skilly v1 accepts GitHub repository and tree URLs.";
            return false;
        }

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Host '{uri.Host}' is not supported; Skilly v1 accepts github.com URLs only.";
            return false;
        }

        if (uri.UserInfo.Length > 0 || uri.Query.Length > 0 || uri.Fragment.Length > 0)
        {
            error = "GitHub source URLs cannot contain user information, a query, or a fragment.";
            return false;
        }

        string[] segments;
        try
        {
            segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
        }
        catch (UriFormatException)
        {
            error = "The source URL contains invalid percent encoding.";
            return false;
        }

        if (segments.Length < 2 || segments[0].Equals("orgs", StringComparison.OrdinalIgnoreCase) || segments[1].Equals("repositories", StringComparison.OrdinalIgnoreCase))
        {
            error = "Expected a GitHub repository URL like https://github.com/<owner>/<repository>.";
            return false;
        }

        if (segments.Any(static segment => segment is ".." or "."
            || segment.Contains('/') || segment.Contains('\\') || segment.Any(char.IsControl)))
        {
            error = "The source URL contains path traversal segments.";
            return false;
        }

        string? requestedRef = null;
        string? requestedPath = null;
        IReadOnlyList<string>? treeSegments = null;
        if (segments.Length >= 4 && segments[2].Equals("tree", StringComparison.OrdinalIgnoreCase))
        {
            treeSegments = segments[3..];
            requestedRef = treeSegments[0];
            if (segments.Length > 4)
            {
                requestedPath = string.Join('/', treeSegments.Skip(1));
            }
        }
        else if (segments.Length > 2 && segments.Skip(2).Any(static segment => segment is "blob" or "tree"))
        {
            error = "Blob URLs are not supported; paste a repository or /tree/<ref>/<path> URL.";
            return false;
        }
        else if (segments.Length > 2)
        {
            error = "Extra path segments are only supported after /tree/<ref>/. Paste a repository or /tree/<ref>/<path> URL.";
            return false;
        }

        reference = new GitHubSourceReference(
            trimmed,
            "github.com",
            segments[0].ToLowerInvariant(),
            (segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1]).ToLowerInvariant(),
            requestedRef,
            string.IsNullOrEmpty(requestedPath) ? null : requestedPath,
            treeSegments);
        return true;
    }

    public GitHubSourceReference ResolveTreeBoundary(string requestedRef, string? requestedPath)
        => this with
        {
            RequestedRef = requestedRef,
            RequestedPath = string.IsNullOrWhiteSpace(requestedPath) ? null : requestedPath,
        };

    public string Normalized => RequestedPath is null
        ? $"{Host}/{Owner}/{Repository}"
        : $"{Host}/{Owner}/{Repository}/tree/{RequestedRef}/{RequestedPath}";

    public string NormalizedSource => $"{Host}/{Owner}/{Repository}".ToLowerInvariant();

    public override string ToString() => Normalized;
}
