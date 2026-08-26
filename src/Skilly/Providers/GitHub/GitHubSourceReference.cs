using System.IO;

namespace Skilly.Providers.GitHub;

public sealed record GitHubSourceReference(
    string Original,
    string Host,
    string Owner,
    string Repository,
    string? RequestedRef,
    string? RequestedPath)
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

        Uri? uri;
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out uri) || uri.Scheme is not ("http" or "https"))
        {
            error = $"'{trimmed}' is not an absolute http(s) URL. Skilly v1 accepts GitHub repository and tree URLs.";
            return false;
        }

        if (!string.Equals(uri.Host, "github.com", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Host '{uri.Host}' is not supported; Skilly v1 accepts github.com URLs only.";
            return false;
        }

        var segments = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length < 2 || segments[0].Equals("orgs", StringComparison.OrdinalIgnoreCase) || segments[1].Equals("repositories", StringComparison.OrdinalIgnoreCase))
        {
            error = "Expected a GitHub repository URL like https://github.com/<owner>/<repository>.";
            return false;
        }

        if (segments.Any(static segment => segment is ".." or "."))
        {
            error = "The source URL contains path traversal segments.";
            return false;
        }

        string? requestedRef = null;
        string? requestedPath = null;
        if (segments.Length >= 4 && segments[2].Equals("tree", StringComparison.OrdinalIgnoreCase))
        {
            requestedRef = Uri.UnescapeDataString(segments[3]);
            if (segments.Length > 4)
            {
                requestedPath = string.Join('/', segments.Skip(4).Select(Uri.UnescapeDataString));
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
            segments[0],
            segments[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? segments[1][..^4] : segments[1],
            requestedRef,
            string.IsNullOrEmpty(requestedPath) ? null : requestedPath);
        return true;
    }

    public string Normalized => RequestedPath is null
        ? $"{Host}/{Owner}/{Repository}"
        : $"{Host}/{Owner}/{Repository}/tree/{RequestedRef}/{RequestedPath}";

    public string NormalizedSource => $"{Host}/{Owner}/{Repository}".ToLowerInvariant();

    public override string ToString() => Normalized;
}
