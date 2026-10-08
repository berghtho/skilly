using System.IO;
using System.Security.Cryptography;
using System.Text;
using Skilly.Infrastructure;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.Providers.GitHub;

internal sealed record GitSourceSnapshot(string Revision, GitHubPayload Payload, DateTimeOffset? RevisionDate);

// Independent of gh api output. Credentials stay in the existing GitHub CLI.
internal sealed class GitHubGitSource(ProcessRunner runner, string ghExecutable, string gitExecutable)
{
    public GitSourceSnapshot Read(ProvenanceInfo provenance, string? immutableRevision = null)
    {
        var checkout = Path.Combine(Path.GetTempPath(), "skilly-github-recovery-" + Guid.NewGuid().ToString("N"));
        var repositoryPath = GitHubChecker.RepositoryPath(provenance);
        var reference = immutableRevision ?? (provenance.TrackingRuleKind switch
        {
            TrackingRuleKind.Branch => "refs/heads/" + provenance.TrackingRule,
            TrackingRuleKind.Tag => "refs/tags/" + provenance.TrackingRule,
            TrackingRuleKind.Commit => provenance.TrackingRule,
            _ => throw new GhApiException("The recorded GitHub tracking rule is unsupported."),
        });
        if (!GitHubSourceReference.TryParse($"https://github.com/{provenance.Owner}/{provenance.Repository}", out _, out _))
            throw new GhApiException("The recorded GitHub repository is invalid.");
        try
        {
            Run(["init", "--quiet", checkout]);
            Run(["-C", checkout, "remote", "add", "origin", $"https://github.com/{provenance.Owner}/{provenance.Repository}.git"]);
            Run(["-C", checkout, "fetch", "--depth=1", "--filter=blob:none", "--no-tags",
                "--", "origin", reference]);
            var revision = Run(["-C", checkout, "rev-parse", "--verify", "FETCH_HEAD^{commit}"]).StandardOutput.Trim();
            RequireIdentity(revision);
            var expectedRevision = immutableRevision ?? (provenance.TrackingRuleKind == TrackingRuleKind.Commit ? provenance.TrackingRule : null);
            if (expectedRevision is not null && !string.Equals(revision, expectedRevision, StringComparison.OrdinalIgnoreCase))
                throw new GhApiException("Git recovery fetched a different commit than the reviewed replacement.");

            var treeSpec = revision + (repositoryPath.Length == 0 ? "^{tree}" : ":" + repositoryPath);
            var treeResult = TryRun(["-C", checkout, "rev-parse", "--verify", treeSpec]);
            if (!treeResult.Succeeded)
                throw new GhSourceUnavailableException($"The selected Source Skill '{provenance.SourceSkillPath}' is absent at {revision[..12]}. Use Update Library to choose its replacement, or Uninstall or Hide the local installation.");
            var treeIdentity = treeResult.StandardOutput.Trim();
            RequireIdentity(treeIdentity);
            var entries = Run(["-C", checkout, "ls-tree", "-r", "-z", treeSpec]).StandardOutput
                .Split('\0', StringSplitOptions.RemoveEmptyEntries)
                .Select(ParseEntry).ToList();
            if (!entries.Any(entry => entry.Path == "SKILL.md"))
                throw new GhSourceUnavailableException($"The selected Source Skill '{provenance.SourceSkillPath}' no longer contains SKILL.md at {revision[..12]}. Use Update Library to choose its replacement, or Uninstall or Hide the local installation.");
            if (entries.Any(entry => entry.Type != "blob" || entry.Mode is not ("100644" or "100755")))
                throw new GhApiException("The replacement contains a symbolic link or submodule; only real Skill files can be installed.");

            Run(["-C", checkout, "sparse-checkout", "init", "--cone"]);
            Run(["-C", checkout, "sparse-checkout", "set", "--", repositoryPath.Length == 0 ? "." : repositoryPath]);
            Run(["-C", checkout, "checkout", "--detach", revision]);
            var head = Run(["-C", checkout, "rev-parse", "HEAD"]).StandardOutput.Trim();
            var branch = Run(["-C", checkout, "rev-parse", "--abbrev-ref", "HEAD"]).StandardOutput.Trim();
            if (!string.Equals(head, revision, StringComparison.OrdinalIgnoreCase) || branch != "HEAD")
                throw new GhApiException("Git recovery did not check out the resolved immutable commit.");

            var files = entries.Select(entry =>
            {
                var path = repositoryPath.Length == 0 ? entry.Path : repositoryPath + "/" + entry.Path;
                var localPath = SafePath(checkout, path);
                var content = File.ReadAllBytes(localPath);
                if (!MatchesBlob(content, entry.Sha))
                {
                    // Windows checkout attributes may rewrite LF as CRLF.
                    if (!content.Contains((byte)0))
                        content = Encoding.UTF8.GetBytes(new UTF8Encoding(false, true).GetString(content).Replace("\r\n", "\n", StringComparison.Ordinal));
                    if (!MatchesBlob(content, entry.Sha))
                        throw new GhApiException($"Git recovery file '{path}' did not match its source blob identity.");
                }
                return (RelativePath: entry.Path, Content: content);
            }).ToList();
            var dateResult = TryRun(["-C", checkout, "log", "-1", "--format=%cI", revision, "--", repositoryPath.Length == 0 ? "." : repositoryPath]);
            DateTimeOffset? date = dateResult.Succeeded && DateTimeOffset.TryParse(dateResult.StandardOutput.Trim(), out var parsed) ? parsed : null;
            return new GitSourceSnapshot(revision, new GitHubPayload(files, PayloadHasher.HashFiles(files), treeIdentity), date);
        }
        finally
        {
            // Only this operation's generated temporary directory is removed.
            try { if (Directory.Exists(checkout)) DeleteCheckout(checkout); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static void DeleteCheckout(string checkout)
    {
        var path = Path.GetFullPath(checkout);
        var temporaryRoot = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (!string.Equals(Path.GetDirectoryName(path), temporaryRoot, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(path).StartsWith("skilly-github-recovery-", StringComparison.Ordinal))
            throw new IOException("Refusing cleanup outside the generated Git recovery directory.");
        DeleteContents(new DirectoryInfo(path));
    }

    private static void DeleteContents(DirectoryInfo directory)
    {
        foreach (var entry in directory.EnumerateFileSystemInfos())
        {
            if (entry is DirectoryInfo child && !entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                DeleteContents(child);
            else
            {
                // Git pack files are read-only on Windows. Never follow a reparse point.
                if (!entry.Attributes.HasFlag(FileAttributes.ReparsePoint) && entry.Attributes.HasFlag(FileAttributes.ReadOnly))
                    entry.Attributes &= ~FileAttributes.ReadOnly;
                entry.Delete();
            }
        }
        directory.Delete();
    }

    private ProcessResult Run(IReadOnlyList<string> arguments)
    {
        var result = TryRun(arguments);
        if (!result.Succeeded)
        {
            var diagnostic = SensitiveDataRedactor.Redact(result.StandardError).Trim();
            throw new GhApiException($"Git recovery failed with exit code {result.ExitCode}: {diagnostic[..Math.Min(400, diagnostic.Length)]}");
        }
        return result;
    }

    private ProcessResult TryRun(IReadOnlyList<string> arguments)
    {
        // Per-process configuration; never changes the user's Git settings or stores a token.
        var helperExecutable = "'" + ghExecutable.Replace('\\', '/').Replace("'", "'\\''", StringComparison.Ordinal) + "'";
        return runner.Run(gitExecutable,
            ["-c", "credential.helper=", "-c", "credential.helper=!" + helperExecutable + " auth git-credential",
             "-c", "core.autocrlf=false", "-c", "core.symlinks=false", .. arguments], TimeSpan.FromMinutes(2),
            new Dictionary<string, string?> { ["GIT_TERMINAL_PROMPT"] = "0", ["GCM_INTERACTIVE"] = "Never",
                ["GH_HOST"] = "github.com", ["GH_FORCE_TTY"] = null, ["NO_COLOR"] = "1",
                ["GIT_DIR"] = null, ["GIT_WORK_TREE"] = null, ["GIT_INDEX_FILE"] = null,
                ["GIT_OBJECT_DIRECTORY"] = null, ["GIT_ALTERNATE_OBJECT_DIRECTORIES"] = null, ["GIT_COMMON_DIR"] = null });
    }

    private static TreeEntry ParseEntry(string value)
    {
        var separator = value.IndexOf('\t');
        var metadata = separator > 0 ? value[..separator].Split(' ') : [];
        if (metadata.Length != 3 || separator == value.Length - 1)
            throw new GhApiException("Git recovery returned an incomplete tree entry.");
        RequireIdentity(metadata[2]);
        return new TreeEntry(value[(separator + 1)..], metadata[1], metadata[2], metadata[0]);
    }

    private static void RequireIdentity(string value)
    {
        if (value.Length != 40 || value.Any(character => !Uri.IsHexDigit(character)))
            throw new GhApiException("Git recovery did not return a full Git object identity.");
    }

    private static bool MatchesBlob(byte[] content, string identity)
        => string.Equals(Convert.ToHexString(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {content.Length}\0"), .. content])), identity, StringComparison.OrdinalIgnoreCase);

    private static string SafePath(string checkout, string path)
    {
        if (path.Split('/').Any(segment => segment is "" or "." or ".." or ".git" || segment.Contains('\\') || segment.Contains(':')))
            throw new GhApiException("The replacement contains an unsafe source path.");
        var root = Path.GetFullPath(checkout).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var fullPath = Path.GetFullPath(Path.Combine(checkout, path.Replace('/', Path.DirectorySeparatorChar)));
        if (!fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new GhApiException("The replacement source path escapes its temporary checkout.");
        for (var current = fullPath; !string.Equals(current + Path.DirectorySeparatorChar, root, StringComparison.OrdinalIgnoreCase); current = Path.GetDirectoryName(current)!)
            if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint))
                throw new GhApiException("Git recovery refuses reparse points in the replacement.");
        return fullPath;
    }
}
