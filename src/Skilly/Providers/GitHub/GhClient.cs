using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using Skilly.Infrastructure;

namespace Skilly.Providers.GitHub;

public sealed record RepositoryFacts(
    [property: JsonPropertyName("default_branch")] string DefaultBranch,
    [property: JsonPropertyName("visibility")] string? Visibility = null);

public sealed record ResolvedCommit(string Sha);

public enum GitHubReferenceKind
{
    Branch,
    Tag,
}

public sealed record TreeEntry(string Path, string Type, string Sha, string Mode);

public sealed class TreeSnapshot(string sha, IReadOnlyList<TreeEntry> entries)
{
    public string Sha { get; } = sha;

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

public sealed class GhInvalidResponseException : GhApiException
{
    public GhInvalidResponseException(string message) : base(message)
    {
    }

    public GhInvalidResponseException(string message, Exception inner) : base(message, inner)
    {
    }
}

public sealed class GhClient(
    ProcessRunner runner,
    string ghExecutable = "gh",
    string gitExecutable = "git")
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

    public void EnsureAuthenticated(string host = "github.com")
    {
        var status = runner.Run(
            ghExecutable,
            ["auth", "status", "--hostname", host],
            TimeSpan.FromSeconds(15));
        if (!status.Succeeded)
        {
            throw new GhApiException(
                $"The active GitHub CLI identity is not authenticated for '{host}' (exit code {status.ExitCode}). Run `gh auth login` outside Skilly.");
        }
    }

    public string GetGitVersion()
    {
        var version = runner.Run(gitExecutable, ["--version"], TimeSpan.FromSeconds(15));
        if (!version.Succeeded || string.IsNullOrWhiteSpace(version.StandardOutput))
        {
            throw new GhApiException($"`git --version` failed with exit code {version.ExitCode}.");
        }
        return version.StandardOutput.Trim();
    }

    public RepositoryFacts GetRepository(string owner, string repository)
    {
        var json = Api($"repos/{owner}/{repository}");
        var parsed = TryDeserialize<RepositoryFacts>(json, "repository facts");
        return new RepositoryFacts(
            parsed.DefaultBranch ?? throw new GhApiException("The repository response did not include a default branch."),
            parsed.Visibility);
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

    public TreeSnapshot GetTreeBelowPath(string owner, string repository, string commitSha, string? requestedPath)
    {
        var normalizedPath = requestedPath?.Trim('/') ?? string.Empty;
        var treeSha = normalizedPath.Length == 0
            ? commitSha
            : ResolveDirectoryIdentity(owner, repository, commitSha, normalizedPath);
        return GetTree(owner, repository, treeSha);
    }

    public TreeSnapshot GetTree(string owner, string repository, string sha, bool recursive = true)
    {
        var endpoint = $"repos/{owner}/{repository}/git/trees/{Uri.EscapeDataString(sha)}"
                       + (recursive ? "?recursive=1" : string.Empty);
        var json = Api(endpoint);
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

        var entries = new List<TreeEntry>();
        foreach (var entry in treeElement.EnumerateArray())
        {
            if (!entry.TryGetProperty("path", out var pathElement)
                || !entry.TryGetProperty("type", out var typeElement)
                || !entry.TryGetProperty("sha", out var entryShaElement)
                || !entry.TryGetProperty("mode", out var modeElement)
                || string.IsNullOrWhiteSpace(pathElement.GetString())
                || string.IsNullOrWhiteSpace(typeElement.GetString())
                || string.IsNullOrWhiteSpace(entryShaElement.GetString())
                || string.IsNullOrWhiteSpace(modeElement.GetString()))
            {
                throw new GhApiException("The tree response contained an incomplete entry; partial discovery is never accepted.");
            }
            entries.Add(new TreeEntry(
                pathElement.GetString()!,
                typeElement.GetString()!,
                entryShaElement.GetString()!,
                modeElement.GetString()!));
        }

        var returnedSha = document.RootElement.TryGetProperty("sha", out var shaElement)
            ? shaElement.GetString()
            : null;
        return new TreeSnapshot(returnedSha ?? sha, entries);
    }

    public IReadOnlyList<(string RelativePath, byte[] Content)> FetchFolder(
        string owner,
        string repository,
        string commitSha,
        string repositoryPath,
        IReadOnlyList<string> filePaths,
        CancellationToken cancellationToken = default,
        IReadOnlyDictionary<string, string>? expectedBlobIdentities = null)
    {
        try
        {
            return filePaths.Select(path =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                var content = GetFileContent(owner, repository, path, commitSha);
                VerifyBlobIdentity(path, content, expectedBlobIdentities);
                return (RelativePath(repositoryPath, path), content);
            }).ToList();
        }
        catch (GhApiException apiException) when (apiException is not GhInvalidResponseException)
        {
            return FetchFolderWithSparseCheckout(
                owner,
                repository,
                commitSha,
                repositoryPath,
                filePaths,
                apiException,
                cancellationToken,
                expectedBlobIdentities);
        }
    }

    public byte[] GetFileContent(string owner, string repository, string path, string sha)
    {
        var endpoint = $"repos/{owner}/{repository}/contents/{EncodePath(path)}?ref={Uri.EscapeDataString(sha)}";
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
                    throw new GhInvalidResponseException($"The content payload for '{path}' was not valid base64.", exception);
                }
            }

            if ((document.RootElement.TryGetProperty("size", out var sizeElement)
                 && sizeElement.TryGetInt64(out var size)
                 && size > 1024 * 1024)
                || (document.RootElement.TryGetProperty("encoding", out var unavailableEncoding)
                    && string.Equals(unavailableEncoding.GetString(), "none", StringComparison.OrdinalIgnoreCase)))
            {
                throw new GhApiException($"The contents API did not inline '{path}'; authenticated sparse checkout is required.");
            }
        }

        throw new GhInvalidResponseException(
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

    private string ResolveDirectoryIdentity(string owner, string repository, string commitSha, string requestedPath)
    {
        var treeSha = commitSha;
        foreach (var segment in requestedPath.Split('/'))
        {
            var tree = GetTree(owner, repository, treeSha, recursive: false);
            var directory = tree.Entries.SingleOrDefault(entry =>
                string.Equals(entry.Path, segment, StringComparison.Ordinal)
                && string.Equals(entry.Type, "tree", StringComparison.Ordinal));
            if (directory is null)
            {
                throw new GhSourceUnavailableException(
                    $"The requested GitHub directory '{requestedPath}' does not exist at commit {commitSha[..Math.Min(12, commitSha.Length)]}.");
            }
            treeSha = directory.Sha;
        }
        return treeSha;
    }

    private IReadOnlyList<(string RelativePath, byte[] Content)> FetchFolderWithSparseCheckout(
        string owner,
        string repository,
        string commitSha,
        string repositoryPath,
        IReadOnlyList<string> filePaths,
        Exception apiException,
        CancellationToken cancellationToken,
        IReadOnlyDictionary<string, string>? expectedBlobIdentities)
    {
        var checkout = Path.Combine(Path.GetTempPath(), "skilly-github-sparse-" + Guid.NewGuid().ToString("N"));
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            RunOrThrow(
                ghExecutable,
                ["repo", "clone", $"{owner}/{repository}", checkout, "--", "--filter=blob:none", "--no-checkout"],
                "authenticated partial clone");
            RunOrThrow(gitExecutable, ["-C", checkout, "sparse-checkout", "init", "--cone"], "sparse-checkout initialization");
            RunOrThrow(
                gitExecutable,
                ["-C", checkout, "sparse-checkout", "set", "--", repositoryPath.Length == 0 ? "." : repositoryPath],
                "sparse path selection");
            RunOrThrow(gitExecutable, ["-C", checkout, "checkout", "--detach", commitSha], "detached immutable checkout");
            var head = RunOrThrow(gitExecutable, ["-C", checkout, "rev-parse", "HEAD"], "checkout revision verification");
            var branch = RunOrThrow(gitExecutable, ["-C", checkout, "rev-parse", "--abbrev-ref", "HEAD"], "detached-head verification");
            if (!string.Equals(head.StandardOutput.Trim(), commitSha, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(branch.StandardOutput.Trim(), "HEAD", StringComparison.Ordinal))
            {
                throw new GhApiException("Sparse checkout did not detach at the already resolved immutable commit.");
            }

            var files = new List<(string RelativePath, byte[] Content)>();
            foreach (var path in filePaths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var fullPath = SafeCheckoutPath(checkout, path);
                if (!File.Exists(fullPath))
                {
                    throw new GhApiException($"Sparse checkout did not contain validated source file '{path}'.");
                }
                var content = File.ReadAllBytes(fullPath);
                VerifyBlobIdentity(path, content, expectedBlobIdentities);
                files.Add((RelativePath(repositoryPath, path), content));
            }
            return files;
        }
        catch (Exception sparseException) when (sparseException is not OperationCanceledException)
        {
            throw new GhApiException(
                $"Selected-folder API acquisition failed and authenticated sparse checkout fallback also failed. API: {apiException.Message} Fallback: {sparseException.Message}",
                sparseException);
        }
        finally
        {
            try
            {
                if (Directory.Exists(checkout))
                {
                    Directory.Delete(checkout, recursive: true);
                }
            }
            catch
            {
            }
        }
    }

    private ProcessResult RunOrThrow(string executable, IReadOnlyList<string> arguments, string operation)
    {
        var result = runner.Run(executable, arguments, TimeSpan.FromMinutes(3));
        if (!result.Succeeded)
        {
            throw new GhApiException($"GitHub {operation} failed with exit code {result.ExitCode}: {Summarize(result.CombinedOutput)}");
        }
        return result;
    }

    private static string RelativePath(string repositoryPath, string path)
        => repositoryPath.Length == 0 ? path : path[(repositoryPath.Length + 1)..];

    private static string SafeCheckoutPath(string checkout, string repositoryPath)
    {
        var root = Path.GetFullPath(checkout).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var candidate = Path.GetFullPath(Path.Combine(checkout, repositoryPath.Replace('/', Path.DirectorySeparatorChar)));
        if (!candidate.StartsWith(root, StringComparison.OrdinalIgnoreCase))
        {
            throw new GhApiException($"Source path '{repositoryPath}' escapes the sparse checkout.");
        }
        return candidate;
    }

    private static string EncodePath(string path)
        => string.Join('/', path.Split('/').Select(Uri.EscapeDataString));

    private static void VerifyBlobIdentity(
        string path,
        byte[] content,
        IReadOnlyDictionary<string, string>? expectedBlobIdentities)
    {
        if (expectedBlobIdentities is null
            || !expectedBlobIdentities.TryGetValue(path, out var expected)
            || expected.Length != 40
            || expected.Any(static character => !Uri.IsHexDigit(character)))
        {
            return;
        }

        var prefix = System.Text.Encoding.ASCII.GetBytes($"blob {content.Length}\0");
        var input = new byte[prefix.Length + content.Length];
        Buffer.BlockCopy(prefix, 0, input, 0, prefix.Length);
        Buffer.BlockCopy(content, 0, input, prefix.Length, content.Length);
        var actual = Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(input)).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
        {
            throw new GhApiException($"Content for validated Git blob '{path}' did not match its Git identity.");
        }
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
