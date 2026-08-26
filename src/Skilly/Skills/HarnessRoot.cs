using System.IO;

namespace Skilly.Skills;

public enum Harness
{
    OpenCode,
    Codex,
    ClaudeCode,
    GitHubCopilot,
}

public enum RootKind
{
    CanonicalAgents,
    ClaudeSkills,
    CopilotSkills,
    OpenCodeConfigSkills,
    CodexLegacySkills,
}

public sealed record HarnessRoot(RootKind Kind, string FullPath)
{
    public bool IsCanonical => Kind == RootKind.CanonicalAgents;

    public static HarnessRoot Create(RootKind kind, string home)
        => new(kind, Path.Combine(home, RelativePath(kind)));

    public static string RelativePath(RootKind kind) => kind switch
    {
        RootKind.CanonicalAgents => Path.Combine(".agents", "skills"),
        RootKind.ClaudeSkills => Path.Combine(".claude", "skills"),
        RootKind.CopilotSkills => Path.Combine(".copilot", "skills"),
        RootKind.OpenCodeConfigSkills => Path.Combine(".config", "opencode", "skills"),
        RootKind.CodexLegacySkills => Path.Combine(".codex", "skills"),
        _ => throw new ArgumentOutOfRangeException(nameof(kind)),
    };

    public IReadOnlyList<Harness> DirectHarnesses() => Kind switch
    {
        RootKind.CanonicalAgents => [Harness.OpenCode, Harness.Codex, Harness.GitHubCopilot],
        RootKind.ClaudeSkills => [Harness.OpenCode, Harness.ClaudeCode],
        RootKind.CopilotSkills => [Harness.GitHubCopilot],
        RootKind.OpenCodeConfigSkills => [Harness.OpenCode],
        RootKind.CodexLegacySkills => [Harness.Codex],
        _ => [],
    };
}
