using System.Diagnostics;
using System.IO;
using System.Text.Json;
using Skilly.Skills;
using Skilly.ViewModels;

namespace Skilly.App.Tests;

public sealed class InventoryFixture : IDisposable
{
    public InventoryFixture()
    {
        Home = Path.Combine(Path.GetTempPath(), "skilly-inventory-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Home);
    }

    public string Home { get; }

    public string Root(string relative) => Path.Combine(Home, relative.Replace('/', Path.DirectorySeparatorChar));

    public string WriteSkill(string relativeDir, string folderName, string declaredName, string description)
    {
        var dir = Path.Combine(Root(relativeDir), folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(
            Path.Combine(dir, "SKILL.md"),
            $"---\nname: {declaredName}\ndescription: {description}\n---\n\n# {declaredName}\n");
        return dir;
    }

    public void WriteRawFile(string relativeDir, string folderName, string content)
    {
        var dir = Path.Combine(Root(relativeDir), folderName);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "SKILL.md"), content);
    }

    public void CreateJunction(string linkRelative, string targetAbsolute)
    {
        var link = Root(linkRelative);
        Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        var psi = new ProcessStartInfo("cmd.exe")
        {
            Arguments = $"/c mklink /J \"{link}\" \"{targetAbsolute}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi)!;
        process.WaitForExit(10000);
        Assert.Equal(0, process.ExitCode);
    }

    public void Dispose() => PackagedAppFixture.TryDeleteDirectory(Home);
}

public sealed class InventoryScannerTests : IDisposable
{
    private readonly InventoryFixture _fixture = new();

    [Fact]
    public void Classifies_canonical_unmanaged_healthy_and_verified_claude_junction_exposure()
    {
        var alphaDir = _fixture.WriteSkill(".agents/skills", "alpha", "alpha", "Alpha skill.");
        _fixture.CreateJunction(".claude/skills/alpha", alphaDir);

        var snapshot = new InventoryScanner().Scan(_fixture.Home);

        var alpha = Assert.Single(snapshot.Entries, entry => entry.FolderName == "alpha");
        Assert.Equal(EntryKind.RealFolder, alpha.Kind);
        Assert.StartsWith(alphaDir, alpha.LocalPath, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(ManagementStatus.Unmanaged, alpha.ManagementStatus);
        Assert.Equal(InstallationHealth.Healthy, alpha.Health);
        Assert.Equal("alpha", alpha.Metadata.DeclaredName);

        Assert.Equal(ExposureState.Canonical, alpha.Exposures[Harness.OpenCode].State);
        Assert.Equal(ExposureState.Canonical, alpha.Exposures[Harness.Codex].State);
        Assert.Equal(ExposureState.Canonical, alpha.Exposures[Harness.GitHubCopilot].State);
        Assert.Equal(ExposureState.VerifiedJunction, alpha.Exposures[Harness.ClaudeCode].State);

        Assert.DoesNotContain(snapshot.Entries, entry => entry.Kind == EntryKind.LinkEntry && entry.FolderName == "alpha");
    }

    [Fact]
    public void Marks_same_named_real_folders_in_two_roots_as_duplicates()
    {
        _fixture.WriteSkill(".agents/skills", "beta", "beta", "Canonical beta.");
        _fixture.WriteSkill(".claude/skills", "beta", "beta", "Claude-side beta copy.");

        var snapshot = new InventoryScanner().Scan(_fixture.Home);

        var betas = snapshot.Entries.Where(entry => entry.FolderName == "beta").ToList();
        Assert.Equal(2, betas.Count);
        Assert.All(betas, beta => Assert.Equal(InstallationHealth.Collision, beta.Health));
        Assert.Contains("more than one global root", betas[0].HealthDetail);
    }

    [Fact]
    public void Keeps_malformed_metadata_visible_as_invalid_without_repairing_it()
    {
        _fixture.WriteRawFile(".agents/skills", "gamma", "---\nname: Gamma Display\n---\n\n# Gamma\n");

        var snapshot = new InventoryScanner().Scan(_fixture.Home);

        var gamma = Assert.Single(snapshot.Entries, entry => entry.FolderName == "gamma");
        Assert.Equal(InstallationHealth.InvalidMetadata, gamma.Health);
        Assert.Contains("description", gamma.HealthDetail, StringComparison.OrdinalIgnoreCase);
        var raw = File.ReadAllText(Path.Combine(gamma.LocalPath, "SKILL.md"));
        Assert.Contains("Gamma Display", raw);
    }

    [Fact]
    public void Reports_broken_link_as_exposure_problem_without_following_it()
    {
        _fixture.CreateJunction(".claude/skills/delta", Path.Combine(_fixture.Home, "nowhere"));

        var snapshot = new InventoryScanner().Scan(_fixture.Home);

        var delta = Assert.Single(snapshot.Entries, entry => entry.FolderName == "delta");
        Assert.Equal(EntryKind.LinkEntry, delta.Kind);
        Assert.Equal(InstallationHealth.Collision, delta.Health);
        Assert.Null(delta.LinkTargetPath);
        Assert.Equal(ExposureState.BrokenLink, delta.Exposures[Harness.ClaudeCode].State);
    }

    [Fact]
    public void Wrong_target_Claude_junction_is_a_Collision_not_a_verified_Harness_Exposure()
    {
        var alphaDir = _fixture.WriteSkill(".agents/skills", "alpha", "alpha", "Alpha skill.");
        var betaDir = _fixture.WriteSkill(".agents/skills", "beta", "beta", "Beta skill.");
        _fixture.CreateJunction(".claude/skills/alpha", betaDir);

        var state = new State.SkillyState
        {
            Records =
            [
                new State.ManagementRecord
                {
                    InstallationId = "alpha-id",
                    CanonicalPath = alphaDir,
                    Provenance = new State.ProvenanceInfo
                    {
                        SourceProvider = "github",
                        OriginalReference = "https://github.com/acme/library",
                        NormalizedSource = "github.com/acme/library",
                        Host = "github.com",
                        Owner = "acme",
                        Repository = "library",
                        SourceSkillPath = "alpha",
                        TrackingRule = "main",
                        ResolvedCommit = "1234",
                        SelectedContentIdentity = "fixture-tree-alpha",
                        ProviderVersion = "test",
                    },
                    IntendedClaudeJunctionPath = _fixture.Root(".claude/skills/alpha"),
                    InstalledRevision = "1234",
                    InstalledPayloadHash = PayloadHasher.HashFolder(alphaDir),
                    InstalledFileCount = 1,
                    ProviderEvidence = "test",
                },
            ],
        };

        var snapshot = new InventoryScanner().Scan(_fixture.Home, state);

        var alpha = Assert.Single(snapshot.Entries, entry =>
            entry.FolderName == "alpha" && entry.RootKind == RootKind.CanonicalAgents);
        Assert.Equal(InstallationHealth.Collision, alpha.Health);
        Assert.Equal(ExposureState.SeparateCopy, alpha.Exposures[Harness.ClaudeCode].State);
    }

    [Fact]
    public void Classifies_each_noncanonical_root_with_only_its_direct_harnesses()
    {
        _fixture.WriteSkill(".copilot/skills", "zeta", "zeta", "Copilot-only skill.");
        _fixture.WriteSkill(".config/opencode/skills", "eta", "eta", "OpenCode-only skill.");
        _fixture.WriteSkill(".codex/skills", "theta", "theta", "Legacy Codex skill.");

        var snapshot = new InventoryScanner().Scan(_fixture.Home);

        var zeta = Assert.Single(snapshot.Entries, entry => entry.FolderName == "zeta");
        Assert.Equal(InstallationHealth.Healthy, zeta.Health);
        Assert.Equal(ExposureState.Direct, zeta.Exposures[Harness.GitHubCopilot].State);
        Assert.Equal(ExposureState.None, zeta.Exposures[Harness.OpenCode].State);
        Assert.Equal(ExposureState.None, zeta.Exposures[Harness.Codex].State);
        Assert.Equal(ExposureState.None, zeta.Exposures[Harness.ClaudeCode].State);

        var eta = Assert.Single(snapshot.Entries, entry => entry.FolderName == "eta");
        Assert.Equal(ExposureState.Direct, eta.Exposures[Harness.OpenCode].State);
        Assert.Equal(ExposureState.None, eta.Exposures[Harness.GitHubCopilot].State);

        var theta = Assert.Single(snapshot.Entries, entry => entry.FolderName == "theta");
        Assert.Equal(ExposureState.Direct, theta.Exposures[Harness.Codex].State);
        Assert.Contains("deprecated", theta.HealthDetail);
    }

    [Fact]
    public void Never_scans_project_directories_under_the_home()
    {
        _fixture.WriteSkill("my-project/.agents/skills", "decoy", "decoy", "Project skill that must be ignored.");

        var snapshot = new InventoryScanner().Scan(_fixture.Home);

        Assert.DoesNotContain(snapshot.Entries, entry => entry.FolderName == "decoy");
        Assert.Empty(snapshot.Entries);
    }

    [Fact]
    public void Missing_claude_entry_reports_missing_junction_on_canonical_row()
    {
        _fixture.WriteSkill(".agents/skills", "omega", "omega", "Omega skill.");

        var snapshot = new InventoryScanner().Scan(_fixture.Home);

        var omega = Assert.Single(snapshot.Entries, entry => entry.FolderName == "omega");
        Assert.Equal(ExposureState.MissingJunction, omega.Exposures[Harness.ClaudeCode].State);
        Assert.Equal(InstallationHealth.Healthy, omega.Health);
    }

    [Fact]
    public void Separate_real_folder_in_claude_root_is_not_treated_as_junction_of_missing_canonical()
    {
        _fixture.WriteSkill(".claude/skills", "epsilon", "epsilon", "Claude-native skill.");

        var snapshot = new InventoryScanner().Scan(_fixture.Home);

        var epsilon = Assert.Single(snapshot.Entries, entry => entry.FolderName == "epsilon");
        Assert.Equal(EntryKind.RealFolder, epsilon.Kind);
        Assert.Equal(InstallationHealth.Healthy, epsilon.Health);
        Assert.Equal(ExposureState.Direct, epsilon.Exposures[Harness.ClaudeCode].State);
        Assert.Equal(ExposureState.Direct, epsilon.Exposures[Harness.OpenCode].State);
        Assert.Equal(ExposureState.None, epsilon.Exposures[Harness.GitHubCopilot].State);
        Assert.Contains("VS Code", epsilon.Exposures[Harness.GitHubCopilot].Detail);
    }

    [Fact]
    public void Snapshot_counts_reflect_health_classification()
    {
        var alphaDir = _fixture.WriteSkill(".agents/skills", "alpha", "alpha", "Healthy.");
        _fixture.CreateJunction(".claude/skills/alpha", alphaDir);
        _fixture.WriteRawFile(".agents/skills", "gamma", "---\nname: Gamma Display\n---\n");

        var snapshot = new InventoryScanner().Scan(_fixture.Home);

        Assert.Equal(2, snapshot.Entries.Count);
        Assert.Equal(1, snapshot.HealthyCount);
        Assert.Equal(1, snapshot.AttentionCount);
        Assert.Equal(2, snapshot.UnmanagedCount);
    }

    [Fact]
    public void Skills_lock_offers_verified_Adoption_for_exact_canonical_skill()
    {
        var skillPath = _fixture.WriteSkill(".agents/skills", "alpha", "alpha", "Alpha skill.");
        var lockPath = _fixture.Root(".agents/.skill-lock.json");
        Directory.CreateDirectory(Path.GetDirectoryName(lockPath)!);
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new
        {
            version = 3,
            skills = new Dictionary<string, object>
            {
                ["alpha"] = new
                {
                    source = "acme/library",
                    sourceType = "github",
                    sourceUrl = "https://github.com/acme/library.git",
                    skillPath = "skills/alpha/SKILL.md",
                    skillFolderHash = "f8608cc25b81e3855fdf8e94605e6f2570af916a",
                },
            },
        }));

        Assert.Equal("f8608cc25b81e3855fdf8e94605e6f2570af916a", GitTreeHasher.HashFolder(skillPath));

        var row = new InventoryRow(Assert.Single(new InventoryScanner().Scan(_fixture.Home, new State.SkillyState()).Entries));

        Assert.Equal("Verified Adoption Available", row.Management);
        Assert.True(row.CanAdopt);
        Assert.Equal("skills@1.5.23 - acme/library", row.Provenance);
        Assert.Equal("https://github.com/acme/library.git", row.Source);
        Assert.Equal("skills/alpha", row.SourceSkillPath);
    }

    [Fact]
    public void APM_lock_offers_verified_Adoption_for_exact_canonical_skill()
    {
        var skillPath = _fixture.WriteSkill(".agents/skills", "alpha", "alpha", "Alpha skill.");
        var fileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(Path.Combine(skillPath, "SKILL.md")))).ToLowerInvariant();
        var apmRoot = _fixture.Root(".apm");
        Directory.CreateDirectory(apmRoot);
        File.WriteAllText(Path.Combine(apmRoot, "apm.yml"), "name: global\ndependencies:\n  apm:\n    - git: acme/library\n");
        File.WriteAllText(Path.Combine(apmRoot, "apm.lock.yaml"), """
            lockfile_version: '1'
            apm_version: 0.28.0
            dependencies:
              - repo_url: acme/library
                resolved_ref: main
                resolved_commit: 0123456789abcdef0123456789abcdef01234567
                package_type: skill_bundle
                deployed_files:
                  - .agents/skills/alpha
                  - .agents/skills/alpha/SKILL.md
                deployed_file_hashes:
                  .agents/skills/alpha/SKILL.md: sha256:HASH
            """);
        File.WriteAllText(Path.Combine(apmRoot, "apm.lock.yaml"), File.ReadAllText(Path.Combine(apmRoot, "apm.lock.yaml")).Replace("HASH", fileHash, StringComparison.Ordinal));

        var row = new InventoryRow(Assert.Single(new InventoryScanner().Scan(_fixture.Home, new State.SkillyState()).Entries));

        Assert.Equal("Verified Adoption Available", row.Management);
        Assert.True(row.CanAdopt);
        Assert.Equal("Microsoft APM 0.28.0 - acme/library", row.Provenance);
        Assert.Equal("acme/library", row.Source);
        Assert.Equal("alpha", row.SourceSkillPath);
    }

    public void Dispose() => _fixture.Dispose();
}
