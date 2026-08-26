using System.Text.Json;

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
    }
    File.WriteAllText(
        Path.Combine(gitDirectory, "fake-head"),
        Environment.GetEnvironmentVariable("FAKE_GIT_HEAD_OVERRIDE") ?? args[4]);
    return 0;
}

Console.Error.WriteLine($"unsupported fake git invocation: {joined}");
return 2;
