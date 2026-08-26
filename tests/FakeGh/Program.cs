using System.Text.Json;

if (args is ["--version"])
{
    Console.WriteLine("gh version 99.0.0-fake");
    return 0;
}

if (args.Length != 2 || args[0] != "api")
{
    Console.Error.WriteLine("FakeGh supports only --version and api <endpoint>.");
    return 2;
}

var endpoint = args[1];
var notFoundPattern = Environment.GetEnvironmentVariable("FAKE_GH_NOT_FOUND_PATTERN");
if (!string.IsNullOrEmpty(notFoundPattern) && endpoint.Contains(notFoundPattern, StringComparison.Ordinal))
{
    Console.Error.WriteLine("gh: Not Found (HTTP 404)");
    return 1;
}

var failPattern = Environment.GetEnvironmentVariable("FAKE_GH_FAIL_PATTERN");
if (!string.IsNullOrEmpty(failPattern) && endpoint.Contains(failPattern, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"injected failure for {endpoint}");
    return 17;
}

var falseSuccessPattern = Environment.GetEnvironmentVariable("FAKE_GH_FALSE_SUCCESS_PATTERN");
if (!string.IsNullOrEmpty(falseSuccessPattern) && endpoint.Contains(falseSuccessPattern, StringComparison.Ordinal))
{
    Console.WriteLine("{}");
    return 0;
}

var fixtureRoot = Environment.GetEnvironmentVariable("FAKE_GH_FIXTURE_ROOT");
if (string.IsNullOrWhiteSpace(fixtureRoot))
{
    Console.Error.WriteLine("FAKE_GH_FIXTURE_ROOT is required.");
    return 3;
}

if (endpoint.Contains("/contents/", StringComparison.Ordinal))
{
    var pendingFlag = Path.Combine(fixtureRoot, "require-pending.flag");
    if (File.Exists(pendingFlag))
    {
        var statePath = Environment.GetEnvironmentVariable("FAKE_GH_STATE_PATH");
        if (string.IsNullOrEmpty(statePath) || !File.Exists(statePath))
        {
            Console.Error.WriteLine("pending-operation state file was absent before content acquisition");
            return 21;
        }

        using var state = JsonDocument.Parse(File.ReadAllText(statePath));
        if (!state.RootElement.TryGetProperty("pendingOperation", out var pending)
            || pending.ValueKind == JsonValueKind.Null)
        {
            Console.Error.WriteLine("pendingOperation was not recorded before content acquisition");
            return 22;
        }

        if (!pending.TryGetProperty("startingHashes", out var startingHashes)
            || startingHashes.ValueKind != JsonValueKind.Array
            || startingHashes.GetArrayLength() == 0)
        {
            Console.Error.WriteLine("pendingOperation did not include starting hashes");
            return 23;
        }

        var observedPath = Path.Combine(fixtureRoot, "observed-pending.json");
        if (!File.Exists(observedPath))
        {
            File.Copy(statePath, observedPath);
        }
    }

    var contentStart = endpoint.IndexOf("/contents/", StringComparison.Ordinal) + "/contents/".Length;
    var queryStart = endpoint.IndexOf('?', contentStart);
    var encodedPath = queryStart < 0 ? endpoint[contentStart..] : endpoint[contentStart..queryStart];
    var relativePath = Uri.UnescapeDataString(encodedPath).Replace('/', Path.DirectorySeparatorChar);
    var filesRoot = Path.GetFullPath(Path.Combine(fixtureRoot, "files"));
    var filePath = Path.GetFullPath(Path.Combine(filesRoot, relativePath));
    if (!filePath.StartsWith(filesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
        || !File.Exists(filePath))
    {
        Console.Error.WriteLine($"fixture content not found: {relativePath}");
        return 4;
    }

    Console.WriteLine(JsonSerializer.Serialize(new
    {
        encoding = "base64",
        content = Convert.ToBase64String(File.ReadAllBytes(filePath)),
    }));
    return 0;
}

var fixtureName = endpoint switch
{
    var value when value.Contains("/git/matching-refs/heads/", StringComparison.Ordinal) => "heads.json",
    var value when value.Contains("/git/matching-refs/tags/", StringComparison.Ordinal) => "tags.json",
    var value when value.Contains("/commits?", StringComparison.Ordinal) => "skill-commit.json",
    var value when value.Contains("/commits/", StringComparison.Ordinal) => "commit.json",
    var value when value.Contains("/git/trees/", StringComparison.Ordinal) => "tree.json",
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
