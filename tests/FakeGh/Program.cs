using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

RecordInvocation(args);

if (args is ["--version"])
{
    Console.WriteLine("gh version 99.0.0-fake");
    return 0;
}

if (args is ["auth", "status", "--hostname", _])
{
    if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("FAKE_GH_AUTH_FAILURE")))
    {
        Console.Error.WriteLine("not logged in to the requested GitHub host");
        return 4;
    }
    Console.WriteLine("authenticated");
    return 0;
}

var fixtureRoot = Environment.GetEnvironmentVariable("FAKE_GH_FIXTURE_ROOT");
if (string.IsNullOrWhiteSpace(fixtureRoot))
{
    Console.Error.WriteLine("FAKE_GH_FIXTURE_ROOT is required.");
    return 3;
}

if (args is ["repo", "clone", _, var cloneTarget, "--", "--filter=blob:none", "--no-checkout"])
{
    if (ShouldFail("repo clone", out var cloneExit))
    {
        return cloneExit;
    }
    Directory.CreateDirectory(Path.Combine(cloneTarget, ".git"));
    return 0;
}

if (args.Length != 2 || args[0] != "api")
{
    Console.Error.WriteLine("FakeGh supports --version, auth status, api, and authenticated partial clone.");
    return 2;
}

var endpoint = args[1];
var unavailablePattern = Environment.GetEnvironmentVariable("FAKE_GH_CONTENT_UNAVAILABLE_PATTERN");
if (!string.IsNullOrEmpty(unavailablePattern) && endpoint.Contains(unavailablePattern, StringComparison.Ordinal))
{
    Console.Error.WriteLine("temporary selected-content API unavailability");
    return 19;
}
var notFoundPattern = Environment.GetEnvironmentVariable("FAKE_GH_NOT_FOUND_PATTERN");
if (!string.IsNullOrEmpty(notFoundPattern) && endpoint.Contains(notFoundPattern, StringComparison.Ordinal))
{
    Console.Error.WriteLine("gh: Not Found (HTTP 404)");
    return 1;
}
if (ShouldFail(endpoint, out var exitCode))
{
    return exitCode;
}

var falseSuccessPattern = Environment.GetEnvironmentVariable("FAKE_GH_FALSE_SUCCESS_PATTERN");
if (!string.IsNullOrEmpty(falseSuccessPattern) && endpoint.Contains(falseSuccessPattern, StringComparison.Ordinal))
{
    Console.WriteLine("{}");
    return 0;
}

if (endpoint.Contains("/contents", StringComparison.Ordinal))
{
    ObservePendingOperation(fixtureRoot);
    return WriteContents(fixtureRoot, endpoint);
}

if (endpoint.Contains("/git/trees/", StringComparison.Ordinal))
{
    return WriteTree(fixtureRoot, endpoint);
}

var fixtureName = endpoint switch
{
    var value when value.Contains("/git/matching-refs/heads/", StringComparison.Ordinal) => "heads.json",
    var value when value.Contains("/git/matching-refs/tags/", StringComparison.Ordinal) => "tags.json",
    var value when value.Contains("/commits?", StringComparison.Ordinal) => "skill-commit.json",
    var value when value.Contains("/commits/", StringComparison.Ordinal) => "commit.json",
    _ => "repository.json",
};
var fixturePath = Path.Combine(fixtureRoot, fixtureName);
if (!File.Exists(fixturePath))
{
    Console.Error.WriteLine($"fixture response not found: {fixtureName}");
    return 5;
}

Console.WriteLine(File.ReadAllText(fixturePath));
return 0;

static int WriteContents(string fixtureRoot, string endpoint)
{
    var marker = "/contents";
    var contentStart = endpoint.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
    var queryStart = endpoint.IndexOf('?', contentStart);
    var encodedPath = queryStart < 0 ? endpoint[contentStart..] : endpoint[contentStart..queryStart];
    var relativePath = Uri.UnescapeDataString(encodedPath.TrimStart('/')).Replace('/', Path.DirectorySeparatorChar);
    var filesRoot = Path.GetFullPath(Path.Combine(fixtureRoot, "files"));
    var target = Path.GetFullPath(Path.Combine(filesRoot, relativePath));
    if (!IsBelow(filesRoot, target))
    {
        Console.Error.WriteLine("fixture content path escaped its root");
        return 4;
    }

    if (File.Exists(target))
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            encoding = "base64",
            content = Convert.ToBase64String(File.ReadAllBytes(target)),
        }));
        return 0;
    }
    if (!Directory.Exists(target))
    {
        Console.Error.WriteLine($"fixture content not found: {relativePath}");
        return 4;
    }

    var (_, excludedPath) = ReadTreeControl(fixtureRoot);
    var entries = Directory.EnumerateFileSystemEntries(target)
        .OrderBy(static value => value, StringComparer.Ordinal)
        .Where(path =>
        {
            var relative = Path.GetRelativePath(filesRoot, path).Replace('\\', '/');
            return excludedPath is null
                   || !(string.Equals(relative, excludedPath, StringComparison.Ordinal)
                        || relative.StartsWith(excludedPath + "/", StringComparison.Ordinal));
        })
        .Select(path =>
        {
            var relative = Path.GetRelativePath(filesRoot, path).Replace('\\', '/');
            var directory = Directory.Exists(path);
            return new
            {
                name = Path.GetFileName(path),
                path = relative,
                type = directory ? "dir" : "file",
                sha = directory ? TreeIdentity(filesRoot, relative) : FileIdentity(path),
            };
        });
    Console.WriteLine(JsonSerializer.Serialize(entries));
    return 0;
}

static int WriteTree(string fixtureRoot, string endpoint)
{
    var marker = "/git/trees/";
    var start = endpoint.IndexOf(marker, StringComparison.Ordinal) + marker.Length;
    var query = endpoint.IndexOf('?', start);
    var encodedSha = query < 0 ? endpoint[start..] : endpoint[start..query];
    var sha = Uri.UnescapeDataString(encodedSha);
    var filesRoot = Path.GetFullPath(Path.Combine(fixtureRoot, "files"));
    var relativeRoot = sha.StartsWith("tree_", StringComparison.Ordinal) ? DecodeTreePath(sha) : string.Empty;
    var treeRoot = Path.GetFullPath(Path.Combine(filesRoot, relativeRoot.Replace('/', Path.DirectorySeparatorChar)));
    if (!IsBelow(filesRoot, treeRoot) || !Directory.Exists(treeRoot))
    {
        Console.Error.WriteLine($"fixture tree not found: {sha}");
        return 4;
    }

    var (truncated, excludedPath) = ReadTreeControl(fixtureRoot);
    var recursive = endpoint.Contains("recursive=1", StringComparison.Ordinal);
    var entries = Directory.EnumerateFileSystemEntries(
            treeRoot,
            "*",
            recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly)
        .Where(path => !path.Contains(Path.DirectorySeparatorChar + ".git" + Path.DirectorySeparatorChar, StringComparison.Ordinal))
        .Select(path => new
        {
            FullPath = path,
            Relative = Path.GetRelativePath(treeRoot, path).Replace('\\', '/'),
        })
        .Where(value =>
        {
            var repositoryRelative = Path.GetRelativePath(filesRoot, value.FullPath).Replace('\\', '/');
            return excludedPath is null
                   || !(string.Equals(repositoryRelative, excludedPath, StringComparison.Ordinal)
                        || repositoryRelative.StartsWith(excludedPath + "/", StringComparison.Ordinal));
        })
        .OrderBy(static value => value.Relative, StringComparer.Ordinal)
        .Select(value =>
        {
            var directory = Directory.Exists(value.FullPath);
            return new
            {
                path = value.Relative,
                type = directory ? "tree" : "blob",
                sha = directory
                    ? TreeIdentity(filesRoot, Path.GetRelativePath(filesRoot, value.FullPath).Replace('\\', '/'))
                    : FileIdentity(value.FullPath),
                mode = directory ? "040000" : "100644",
            };
        });
    var returnedIdentity = Environment.GetEnvironmentVariable("FAKE_GH_TREE_IDENTITY_OVERRIDE")
                           ?? TreeIdentity(filesRoot, relativeRoot);
    Console.WriteLine(JsonSerializer.Serialize(new { sha = returnedIdentity, truncated, tree = entries }));
    return 0;
}

static (bool Truncated, string? ExcludedPath) ReadTreeControl(string fixtureRoot)
{
    var path = Path.Combine(fixtureRoot, "tree-control.json");
    if (!File.Exists(path))
    {
        return (false, null);
    }
    using var document = JsonDocument.Parse(File.ReadAllText(path));
    var truncated = document.RootElement.TryGetProperty("truncated", out var value) && value.GetBoolean();
    var excluded = document.RootElement.TryGetProperty("excludedPath", out var excludedValue)
        ? excludedValue.GetString()
        : null;
    return (truncated, excluded);
}

static string TreeIdentity(string filesRoot, string relativePath)
{
    var normalized = relativePath.Trim('/');
    var directory = Path.Combine(filesRoot, normalized.Replace('/', Path.DirectorySeparatorChar));
    using var sha1 = SHA1.Create();
    var material = string.Join("\n", Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
        .OrderBy(static path => path, StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/') + ":" + FileIdentity(path)));
    var digest = Convert.ToHexString(sha1.ComputeHash(Encoding.UTF8.GetBytes(material))).ToLowerInvariant();
    var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(normalized))
        .TrimEnd('=').Replace('+', '-').Replace('/', '_');
    return $"tree_{encoded}.{digest}";
}

static string DecodeTreePath(string identity)
{
    var encoded = identity["tree_".Length..identity.IndexOf('.')].Replace('-', '+').Replace('_', '/');
    encoded = encoded.PadRight((encoded.Length + 3) / 4 * 4, '=');
    return Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
}

static string FileIdentity(string path)
{
    var content = File.ReadAllBytes(path);
    var prefix = Encoding.ASCII.GetBytes($"blob {content.Length}\0");
    var input = new byte[prefix.Length + content.Length];
    Buffer.BlockCopy(prefix, 0, input, 0, prefix.Length);
    Buffer.BlockCopy(content, 0, input, prefix.Length, content.Length);
    return Convert.ToHexString(SHA1.HashData(input)).ToLowerInvariant();
}

static bool IsBelow(string root, string candidate)
    => string.Equals(root, candidate, StringComparison.OrdinalIgnoreCase)
       || candidate.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

static bool ShouldFail(string value, out int exitCode)
{
    var failPattern = Environment.GetEnvironmentVariable("FAKE_GH_FAIL_PATTERN");
    if (!string.IsNullOrEmpty(failPattern) && value.Contains(failPattern, StringComparison.Ordinal))
    {
        Console.Error.WriteLine($"injected failure for {value}");
        exitCode = 17;
        return true;
    }
    exitCode = 0;
    return false;
}

static void ObservePendingOperation(string fixtureRoot)
{
    var pendingFlag = Path.Combine(fixtureRoot, "require-pending.flag");
    if (!File.Exists(pendingFlag))
    {
        return;
    }
    var statePath = Environment.GetEnvironmentVariable("FAKE_GH_STATE_PATH");
    if (string.IsNullOrEmpty(statePath) || !File.Exists(statePath))
    {
        throw new InvalidOperationException("pending-operation state file was absent before content acquisition");
    }
    using var state = JsonDocument.Parse(File.ReadAllText(statePath));
    if (!state.RootElement.TryGetProperty("pendingOperation", out var pending)
        || !pending.TryGetProperty("startingHashes", out var hashes)
        || hashes.GetArrayLength() == 0)
    {
        throw new InvalidOperationException("pendingOperation was not recorded before content acquisition");
    }
    var observedPath = Path.Combine(fixtureRoot, "observed-pending.json");
    if (!File.Exists(observedPath))
    {
        File.Copy(statePath, observedPath);
    }
}

static void RecordInvocation(string[] arguments)
{
    var path = Environment.GetEnvironmentVariable("FAKE_GH_INVOCATIONS");
    if (!string.IsNullOrWhiteSpace(path))
    {
        File.AppendAllText(path, JsonSerializer.Serialize(arguments) + Environment.NewLine);
    }
}
