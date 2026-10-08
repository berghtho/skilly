using System.Text.Json;
using System.Security.Cryptography;
using System.Text;

if (args is ["--version"])
{
    Console.WriteLine("git version 99.0.0-fake");
    return 0;
}

var invocationPath = Environment.GetEnvironmentVariable("FAKE_GIT_INVOCATIONS");
if (!string.IsNullOrWhiteSpace(invocationPath))
{
    File.AppendAllText(invocationPath, JsonSerializer.Serialize(args) + Environment.NewLine);
}

var failPattern = Environment.GetEnvironmentVariable("FAKE_GIT_FAIL_PATTERN");
var joined = string.Join(' ', args);
if (!string.IsNullOrEmpty(failPattern) && joined.Contains(failPattern, StringComparison.Ordinal))
{
    Console.Error.WriteLine($"injected git failure for {joined}");
    return 18;
}

// Git recovery uses per-process credential and checkout configuration.
while (args.Length >= 2 && args[0] == "-c") args = args[2..];

if (args is ["init", "--quiet", var initPath])
{
    Directory.CreateDirectory(Path.Combine(initPath, ".git"));
    return 0;
}
if (args is ["-C", _, "remote", "add", "origin", _]) return 0;

if (args.Length >= 4 && args[0] == "-C" && args[2] == "fetch")
{
    File.WriteAllText(Path.Combine(args[1], ".git", "fake-head"),
        Environment.GetEnvironmentVariable("FAKE_GIT_SOURCE_REVISION") ?? "1234567890abcdef1234567890abcdef12345678");
    return 0;
}

if (args.Length >= 4 && args[0] == "-C" && args[2] == "log")
{
    Console.WriteLine("2026-01-02T03:04:05Z");
    return 0;
}

if (args.Length >= 4 && args[0] == "-C" && args[2] is "ls-tree" or "rev-parse"
    && (args[^1].Contains(':') || args[^1].EndsWith("^{tree}", StringComparison.Ordinal)))
{
    var fixtureRoot = Environment.GetEnvironmentVariable("FAKE_GH_FIXTURE_ROOT")!;
    var path = args[^1].Contains(':') ? args[^1].Split(':', 2)[1] : string.Empty;
    var source = Path.Combine(fixtureRoot, "files", path.Replace('/', Path.DirectorySeparatorChar));
    if (!Directory.Exists(source)) return 4;
    if (args[2] == "rev-parse") Console.WriteLine(HashTree(source));
    else
        foreach (var file in Directory.GetFiles(source, "*", SearchOption.AllDirectories).OrderBy(value => value, StringComparer.Ordinal))
            Console.Write($"100644 blob {HashObject("blob", File.ReadAllBytes(file))}\t{Path.GetRelativePath(source, file).Replace('\\', '/')}\0");
    return 0;
}

if (args.Length >= 4 && args[0] == "-C" && args[2] == "rev-parse")
{
    var headPath = Path.Combine(args[1], ".git", "fake-head");
    if (!File.Exists(headPath))
    {
        return 4;
    }
    Console.WriteLine(args[3] == "--abbrev-ref" ? "HEAD" : File.ReadAllText(headPath));
    return 0;
}

if (args.Length < 5 || args[0] != "-C")
{
    Console.Error.WriteLine("FakeGit requires git -C <checkout> ...");
    return 2;
}

var checkout = args[1];
var gitDirectory = Path.Combine(checkout, ".git");
Directory.CreateDirectory(gitDirectory);
if (args[2] == "sparse-checkout" && args[3] == "init")
{
    return 0;
}
if (args[2] == "sparse-checkout" && args[3] == "set")
{
    File.WriteAllText(Path.Combine(gitDirectory, "fake-sparse-path"), args[^1]);
    return 0;
}
if (args[2] == "checkout" && args[3] == "--detach")
{
    var fixtureRoot = Environment.GetEnvironmentVariable("FAKE_GH_FIXTURE_ROOT")
                      ?? throw new InvalidOperationException("FAKE_GH_FIXTURE_ROOT is required.");
    var sparsePath = File.ReadAllText(Path.Combine(gitDirectory, "fake-sparse-path"));
    var sourceRoot = Path.Combine(fixtureRoot, "files");
    var source = sparsePath == "."
        ? sourceRoot
        : Path.Combine(sourceRoot, sparsePath.Replace('/', Path.DirectorySeparatorChar));
    if (!Directory.Exists(source))
    {
        Console.Error.WriteLine($"sparse source not found: {sparsePath}");
        return 4;
    }
    foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
    {
        var repositoryRelative = Path.GetRelativePath(sourceRoot, file);
        var target = Path.Combine(checkout, repositoryRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(file, target, overwrite: true);
        if (Environment.GetEnvironmentVariable("FAKE_GIT_CORRUPT_CHECKOUT") == "1" && Path.GetFileName(target) == "SKILL.md")
            File.AppendAllText(target, "\nCorrupted checkout payload.\n");
    }
    File.WriteAllText(
        Path.Combine(gitDirectory, "fake-head"),
        Environment.GetEnvironmentVariable("FAKE_GIT_HEAD_OVERRIDE") ?? args[4]);
    return 0;
}

Console.Error.WriteLine($"unsupported fake git invocation: {joined}");
return 2;

static string HashObject(string type, byte[] bytes)
    => Convert.ToHexString(SHA1.HashData([.. Encoding.ASCII.GetBytes($"{type} {bytes.Length}\0"), .. bytes])).ToLowerInvariant();

static string HashTree(string folder)
{
    using var stream = new MemoryStream();
    foreach (var entry in new DirectoryInfo(folder).EnumerateFileSystemInfos()
        .OrderBy(entry => entry.Name + (entry is DirectoryInfo ? "/" : string.Empty), StringComparer.Ordinal))
    {
        var directory = entry is DirectoryInfo;
        var identity = directory ? HashTree(entry.FullName) : HashObject("blob", File.ReadAllBytes(entry.FullName));
        stream.Write(Encoding.ASCII.GetBytes(directory ? "40000 " : "100644 "));
        stream.Write(Encoding.UTF8.GetBytes(entry.Name));
        stream.WriteByte(0);
        stream.Write(Convert.FromHexString(identity));
    }
    return HashObject("tree", stream.ToArray());
}
