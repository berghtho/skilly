using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skilly.Infrastructure;

namespace Skilly.Providers.GitHub;

public sealed record RepositoryFacts(
    [property: JsonPropertyName("default_branch")] string DefaultBranch);

public sealed record ResolvedCommit(string Sha);

public enum GitHubReferenceKind
{
    Branch,
    Tag,
}

public sealed record TreeEntry(string Path, string Type);

public sealed class TreeSnapshot(IReadOnlyList<TreeEntry> entries)
{
    public IReadOnlyList<TreeEntry> Entries { get; } = entries;
}

public class GhApiException : Exception
{
    public GhApiException(string message) : base(message)
    {
    }

    public GhApiException(string message, Exception inner) : base(message, inner)
    {
    }
}

public sealed class GhSourceUnavailableException : GhApiException
{
    public GhSourceUnavailableException(string message) : base(message)
    {
    }
}

public sealed class GhClient(ProcessRunner runner, string ghExecutable = "gh")
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    public string GetVersion()
    {
        var version = runner.Run(ghExecutable, ["--version"], TimeSpan.FromSeconds(15));
        if (!version.Succeeded)
        {
            throw new GhApiException($"`gh --version` failed with exit code {version.ExitCode}: {Summarize(version.CombinedOutput)}");
        }

        var firstLine = version.StandardOutput
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        if (string.IsNullOrWhiteSpace(firstLine))
        {
            throw new GhApiException("`gh --version` returned no version information.");
        }

        return firstLine;
    }

    public RepositoryFacts GetRepository(string owner, string repository)
    {
        var json = Api($"repos/{owner}/{repository}");
        var parsed = TryDeserialize<RepositoryFacts>(json, "repository facts");
        return new RepositoryFacts(parsed.DefaultBranch ?? throw new GhApiException("The repository response did not include a default branch."));
    }

    public ResolvedCommit ResolveCommit(string owner, string repository, string reference)
    {
        var json = Api($"repos/{owner}/{repository}/commits/{Uri.EscapeDataString(reference)}");
        var parsed = TryDeserialize<ResolvedCommit>(json, "commit resolution");
        return string.IsNullOrEmpty(parsed.Sha)
            ? throw new GhApiException("The commit response did not include a SHA.")
            : new ResolvedCommit(parsed.Sha);
    }

    public bool ReferenceExists(string owner, string repository, GitHubReferenceKind kind, string reference)
    {
        var category = kind == GitHubReferenceKind.Branch ? "heads" : "tags";
        var json = Api($"repos/{owner}/{repository}/git/matching-refs/{category}/{Uri.EscapeDataString(reference)}");
        var document = TryParse(json, $"matching {category}");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new GhApiException($"The matching {category} response was not an array.");
        }

        var expected = $"refs/{category}/{reference}";
        return document.RootElement.EnumerateArray().Any(element =>
            element.TryGetProperty("ref", out var refElement)
            && string.Equals(refElement.GetString(), expected, StringComparison.Ordinal));
    }

    public DateTimeOffset? GetSkillRevisionDate(
        string owner,
        string repository,
        string revision,
        string repositoryPath)
    {
        var endpoint = $"repos/{owner}/{repository}/commits?sha={Uri.EscapeDataString(revision)}"
                       + $"&path={Uri.EscapeDataString(repositoryPath)}&per_page=1";
        var document = TryParse(Api(endpoint), "path-scoped commit history");
        if (document.RootElement.ValueKind != JsonValueKind.Array)
        {
            throw new GhApiException("The path-scoped commit history response was not an array.");
        }

        var first = document.RootElement.EnumerateArray().FirstOrDefault();
        if (first.ValueKind == JsonValueKind.Undefined)
        {
            return null;
        }

        if (!first.TryGetProperty("commit", out var commit)
            || !commit.TryGetProperty("committer", out var committer)
            || !committer.TryGetProperty("date", out var date)
            || !date.TryGetDateTimeOffset(out var parsed))
        {
            throw new GhApiException("The path-scoped commit history did not include a committer date.");
        }

        return parsed;
    }

    public TreeSnapshot GetTree(string owner, string repository, string sha)
    {
        var json = Api($"repos/{owner}/{repository}/git/trees/{sha}?recursive=1");
        var document = TryParse(json, "tree listing");
        if (!document.RootElement.TryGetProperty("truncated", out var truncatedElement))
        {
            throw new GhApiException("The tree response did not include the 'truncated' flag.");
        }

        if (truncatedElement.GetBoolean())
        {
            throw new GhApiException(
                "GitHub reported the tree response as truncated. A partial discovery is never accepted; retry later or narrow the requested path.");
        }

        if (!document.RootElement.TryGetProperty("tree", out var treeElement) || treeElement.ValueKind != JsonValueKind.Array)
        {
            throw new GhApiException("The tree response did not include a 'tree' array.");
        }

        var entries = treeElement.EnumerateArray()
            .Where(entry => entry.TryGetProperty("path", out _) && entry.TryGetProperty("type", out _))
            .Select(entry => new TreeEntry(
                entry.GetProperty("path").GetString()!,
                entry.GetProperty("type").GetString()!))
            .ToList();

        return new TreeSnapshot(entries);
    }

    public byte[] GetFileContent(string owner, string repository, string path, string sha)
    {
        var endpoint = $"repos/{owner}/{repository}/contents/{path}?ref={Uri.EscapeDataString(sha)}";
        var json = Api(endpoint);
        var document = TryParse(json, $"content of '{path}'");
        if (document.RootElement.ValueKind == JsonValueKind.Object
            && document.RootElement.TryGetProperty("content", out var contentElement)
            && document.RootElement.TryGetProperty("encoding", out var encodingElement)
            && string.Equals(encodingElement.GetString(), "base64", StringComparison.OrdinalIgnoreCase))
        {
            var base64 = contentElement.GetString();
            if (!string.IsNullOrEmpty(base64))
            {
                try
                {
                    return Convert.FromBase64String(base64.Replace("\n", string.Empty, StringComparison.Ordinal));
                }
                catch (FormatException exception)
                {
                    throw new GhApiException($"The content payload for '{path}' was not valid base64.", exception);
                }
            }
        }

        throw new GhApiException(
            $"'{path}' could not be fetched through the contents API. Files above 1 MB are unsupported by this path in Skilly v1.");
    }

    private string Api(string endpoint)
    {
        var result = runner.Run(ghExecutable, ["api", endpoint], TimeSpan.FromSeconds(90));
        if (!result.Succeeded)
        {
            if (result.CombinedOutput.Contains("HTTP 404", StringComparison.OrdinalIgnoreCase))
            {
                throw new GhSourceUnavailableException(
                    $"GitHub source '{endpoint}' is unavailable: {Summarize(result.CombinedOutput)}");
            }

            throw new GhApiException(
                $"`gh api {endpoint}` failed with exit code {result.ExitCode}: {Summarize(result.CombinedOutput)}");
        }

        return result.StandardOutput;
    }

    private static JsonDocument TryParse(string json, string what)
    {
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new GhApiException($"The {what} response was not valid JSON.", exception);
        }
    }

    private static T TryDeserialize<T>(string json, string what)
    {
        try
        {
            return JsonSerializer.Deserialize<T>(json, JsonOptions)
                   ?? throw new GhApiException($"The {what} response deserialized to null.");
        }
        catch (JsonException exception)
        {
            throw new GhApiException($"The {what} response was not valid JSON.", exception);
        }
    }

    private static string Summarize(string output)
    {
        var trimmed = output.Trim();
        if (trimmed.Length <= 400)
        {
            return trimmed.Length == 0 ? "(no output)" : trimmed;
        }

        return trimmed[..400] + "…";
    }
}
