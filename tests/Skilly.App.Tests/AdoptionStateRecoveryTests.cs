using System.IO;
using System.Text.Json;
using Skilly.Infrastructure;
using Skilly.Providers.GitHub;
using Skilly.Skills;
using Skilly.State;
using Skilly.ViewModels;

namespace Skilly.App.Tests;

public sealed class AdoptionStateRecoveryTests
{
    [Fact]
    public void Exact_existing_installation_offers_direct_Adoption_and_preserves_content()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        CopyDirectory(Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha"), fixture.CanonicalPath("alpha"));
        var beforeHash = PayloadHasher.HashFolder(fixture.CanonicalPath("alpha"));
        var beforeSkillMd = File.ReadAllBytes(Path.Combine(fixture.CanonicalPath("alpha"), "SKILL.md"));

        var unmanaged = new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load());
        Assert.Equal(ManagementStatus.Unmanaged, Assert.Single(unmanaged.Entries).ManagementStatus);
        Assert.Empty(fixture.StateStore.Load().Records);

        var discovery = fixture.Provider.DiscoverAdoptions(inspection, unmanaged).ValueOrThrow();
        var evidence = Assert.Single(discovery.Evidence);
        var verified = Assert.Single(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load(), discovery.Evidence).Entries);
        var row = new InventoryRow(verified);
        Assert.Equal(ManagementStatus.VerifiedAdoptionAvailable, verified.ManagementStatus);
        Assert.Equal("Verified Adoption Available", row.Management);
        Assert.True(row.CanAdopt);
        Assert.Contains("preserves Skill content", row.ActionState);
        Assert.Equal(inspection.Reference.NormalizedSource, evidence.ProposedRecord.Provenance.NormalizedSource);
        Assert.Equal("alpha", evidence.ProposedRecord.Provenance.SourceSkillPath);
        Assert.Equal(GitHubProviderFixture.CommitSha, evidence.ProposedRecord.Provenance.ResolvedCommit);

        fixture.Provider.Adopt(evidence).ValueOrThrow();

        Assert.Equal(beforeHash, PayloadHasher.HashFolder(fixture.CanonicalPath("alpha")));
        Assert.Equal(beforeSkillMd, File.ReadAllBytes(Path.Combine(fixture.CanonicalPath("alpha"), "SKILL.md")));
        Assert.True(Junction.IsJunctionTo(fixture.ClaudePath("alpha"), fixture.CanonicalPath("alpha")));
        var state = fixture.StateStore.Load();
        var record = Assert.Single(state.Records);
        Assert.Equal(OperationOutcome.Adopted, record.LastOperationOutcome);
        Assert.Equal(beforeHash, record.InstalledPayloadHash);
        Assert.Null(state.PendingOperation);
        Assert.Equal(ManagementStatus.Managed, Assert.Single(new InventoryScanner().Scan(fixture.Home, state).Entries).ManagementStatus);
    }

    [Fact]
    public void Existing_skills_lock_offers_and_executes_provider_Adoption_without_rewriting_content()
    {
        using var fixture = new GitHubProviderFixture();
        var canonical = fixture.CanonicalPath("alpha");
        Directory.CreateDirectory(canonical);
        File.WriteAllText(Path.Combine(canonical, "SKILL.md"), "---\nname: alpha\ndescription: Alpha skill.\n---\n\n# alpha\n");
        var lockPath = Path.Combine(fixture.Home, ".agents", ".skill-lock.json");
        File.WriteAllText(lockPath, JsonSerializer.Serialize(new
        {
            version = 3,
            skills = new Dictionary<string, object>
            {
                ["alpha"] = new
                {
                    source = "acme/library", sourceType = "github", sourceUrl = "https://github.com/acme/library.git",
                    skillPath = "skills/alpha/SKILL.md", skillFolderHash = "f8608cc25b81e3855fdf8e94605e6f2570af916a",
                },
            },
        }));
        var before = File.ReadAllBytes(Path.Combine(canonical, "SKILL.md"));
        var entry = Assert.Single(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()).Entries);
        Assert.Equal(ManagementStatus.VerifiedAdoptionAvailable, entry.ManagementStatus);

        fixture.Provider.AdoptVerifiedProviderEvidence(
            entry.AdoptionEvidence!,
            () => Assert.Single(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()).Entries).AdoptionEvidence).ValueOrThrow();

        Assert.Equal(before, File.ReadAllBytes(Path.Combine(canonical, "SKILL.md")));
        Assert.True(Junction.IsJunctionTo(fixture.ClaudePath("alpha"), canonical));
        var record = Assert.Single(fixture.StateStore.Load().Records);
        Assert.Equal("skills", record.Provenance.SourceProvider);
        Assert.Equal(OperationOutcome.Adopted, record.LastOperationOutcome);
    }

    [Fact]
    public void Provider_Adoption_rejects_a_changed_skills_lock_at_the_mutation_boundary()
    {
        using var fixture = new GitHubProviderFixture();
        var canonical = fixture.CanonicalPath("alpha");
        Directory.CreateDirectory(canonical);
        File.WriteAllText(Path.Combine(canonical, "SKILL.md"), "---\nname: alpha\ndescription: Alpha skill.\n---\n\n# alpha\n");
        var lockPath = Path.Combine(fixture.Home, ".agents", ".skill-lock.json");
        File.WriteAllText(lockPath, SkillsLock("https://github.com/acme/library.git"));
        var evidence = Assert.Single(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()).Entries).AdoptionEvidence!;
        File.WriteAllText(lockPath, SkillsLock("https://github.com/other/library.git"));

        var result = fixture.Provider.AdoptVerifiedProviderEvidence(
            evidence,
            () => Assert.Single(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()).Entries).AdoptionEvidence);

        Assert.False(result.Succeeded);
        Assert.Contains("provider lock", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(fixture.StateStore.Load().Records);
        Assert.False(Directory.Exists(fixture.ClaudePath("alpha")));
    }

    [Fact]
    public void Existing_APM_lock_executes_provider_Adoption_without_rewriting_content()
    {
        using var fixture = new GitHubProviderFixture();
        var canonical = fixture.CanonicalPath("alpha");
        Directory.CreateDirectory(canonical);
        var skillMd = Path.Combine(canonical, "SKILL.md");
        File.WriteAllText(skillMd, "---\nname: alpha\ndescription: Alpha skill.\n---\n\n# alpha\n");
        var before = File.ReadAllBytes(skillMd);
        var fileHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(before)).ToLowerInvariant();
        var apmRoot = Path.Combine(fixture.Home, ".apm");
        Directory.CreateDirectory(apmRoot);
        File.WriteAllText(Path.Combine(apmRoot, "apm.yml"), "name: global\ndependencies:\n  apm:\n    - git: acme/library\n");
        File.WriteAllText(Path.Combine(apmRoot, "apm.lock.yaml"), ApmLock(fileHash));
        var evidence = Assert.Single(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()).Entries).AdoptionEvidence!;

        fixture.Provider.AdoptVerifiedProviderEvidence(
            evidence,
            () => Assert.Single(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()).Entries).AdoptionEvidence).ValueOrThrow();

        Assert.Equal(before, File.ReadAllBytes(skillMd));
        Assert.True(Junction.IsJunctionTo(fixture.ClaudePath("alpha"), canonical));
        var record = Assert.Single(fixture.StateStore.Load().Records);
        Assert.Equal("apm", record.Provenance.SourceProvider);
        Assert.Equal(OperationOutcome.Adopted, record.LastOperationOutcome);
    }

    [Fact]
    public void Adoption_rechecks_evidence_and_local_drift_fails_without_authority_or_content_rewrite()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        CopyDirectory(Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha"), fixture.CanonicalPath("alpha"));
        var inventory = new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load());
        var evidence = Assert.Single(fixture.Provider.DiscoverAdoptions(inspection, inventory).ValueOrThrow().Evidence);
        var skillMd = Path.Combine(fixture.CanonicalPath("alpha"), "SKILL.md");
        File.AppendAllText(skillMd, "\nLocal drift after verification.\n");
        var drifted = File.ReadAllText(skillMd);

        var result = fixture.Provider.Adopt(evidence);

        Assert.False(result.Succeeded);
        Assert.Contains("changed after verification", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(drifted, File.ReadAllText(skillMd));
        Assert.False(Directory.Exists(fixture.ClaudePath("alpha")));
        Assert.Empty(fixture.StateStore.Load().Records);
        Assert.Equal(ManagementStatus.Unmanaged, Assert.Single(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()).Entries).ManagementStatus);
    }

    [Fact]
    public void Tampered_normalized_source_path_revision_or_provider_evidence_never_offers_Adoption()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        CopyDirectory(Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha"), fixture.CanonicalPath("alpha"));
        var inventory = new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load());
        var original = Assert.Single(fixture.Provider.DiscoverAdoptions(inspection, inventory).ValueOrThrow().Evidence);

        AssertRejected(record => record.Provenance.NormalizedSource = "github.com/other/library");
        AssertRejected(record => record.Provenance.SourceSkillPath = "beta");
        AssertRejected(record => record.Provenance.ResolvedCommit = GitHubProviderFixture.LaterCommitSha);
        AssertRejected(record => record.Provenance.SelectedContentIdentity = "wrong-tree");
        AssertRejected(record => record.ProviderEvidence = "unverified");

        void AssertRejected(Action<ManagementRecord> tamper)
        {
            var record = Clone(original.ProposedRecord);
            tamper(record);
            var evidence = new AdoptionEvidence(
                record,
                original.ExpectedPayloadHash,
                original.ExpectedFileCount,
                original.ExpectedContentIdentity);
            var entry = Assert.Single(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load(), [evidence]).Entries);
            Assert.Equal(ManagementStatus.Unmanaged, entry.ManagementStatus);
            Assert.Null(entry.AdoptionEvidence);
        }
    }

    [Fact]
    public void Failed_Adoption_authority_commit_removes_created_junction_and_leaves_content_Unmanaged()
    {
        var armed = false;
        var rejected = false;
        using var fixture = new GitHubProviderFixture(beforeStateSave: state =>
        {
            if (armed && !rejected && state.PendingOperation is null
                && state.Records.Any(record => record.LastOperationOutcome == OperationOutcome.Adopted))
            {
                rejected = true;
                throw new IOException("injected Adoption authority commit failure");
            }
        });
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        CopyDirectory(Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha"), fixture.CanonicalPath("alpha"));
        var hash = PayloadHasher.HashFolder(fixture.CanonicalPath("alpha"));
        var inventory = new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load());
        var evidence = Assert.Single(fixture.Provider.DiscoverAdoptions(inspection, inventory).ValueOrThrow().Evidence);
        armed = true;

        var result = fixture.Provider.Adopt(evidence);

        Assert.False(result.Succeeded);
        Assert.Contains("safely restored", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(hash, PayloadHasher.HashFolder(fixture.CanonicalPath("alpha")));
        Assert.False(Directory.Exists(fixture.ClaudePath("alpha")));
        var state = fixture.StateStore.Load();
        Assert.Empty(state.Records);
        Assert.Null(state.PendingOperation);
        Assert.Equal(ManagementStatus.Unmanaged, Assert.Single(new InventoryScanner().Scan(fixture.Home, state).Entries).ManagementStatus);
    }

    [Fact]
    public void Legacy_ambiguous_nonmatching_and_unverifiable_installations_remain_Unmanaged()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        var source = Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha");
        CopyDirectory(source, fixture.CanonicalPath("alpha"));
        File.AppendAllText(Path.Combine(fixture.CanonicalPath("alpha"), "SKILL.md"), "\nLocal difference.\n");
        CopyDirectory(source, Path.Combine(fixture.Home, ".codex", "skills", "alpha-legacy"));
        var initial = new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load());

        var nonmatching = fixture.Provider.DiscoverAdoptions(inspection, initial).ValueOrThrow();
        Assert.Empty(nonmatching.Evidence);
        Assert.All(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load(), nonmatching.Evidence).Entries,
            entry => Assert.Equal(ManagementStatus.Unmanaged, entry.ManagementStatus));

        Directory.Delete(fixture.CanonicalPath("alpha"), recursive: true);
        CopyDirectory(source, fixture.CanonicalPath("alpha"));
        var duplicate = inspection.Skills[0] with { SkillPath = "nested/alpha", RepositoryPath = "skills/alpha" };
        var ambiguousInspection = inspection with { Skills = [inspection.Skills[0], duplicate] };
        var ambiguous = fixture.Provider.DiscoverAdoptions(
            ambiguousInspection,
            new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load())).ValueOrThrow();
        Assert.Empty(ambiguous.Evidence);
        Assert.Contains(ambiguous.Diagnostics, diagnostic => diagnostic.Contains("ambiguous", StringComparison.OrdinalIgnoreCase));

        fixture.FailRequestsContaining("scripts/run.ps1");
        var unverifiable = fixture.Provider.DiscoverAdoptions(
            inspection,
            new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load())).ValueOrThrow();
        Assert.Empty(unverifiable.Evidence);
        Assert.Contains(unverifiable.Diagnostics, diagnostic => diagnostic.Contains("could not be verified", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Missing_state_rebuilds_inventory_without_records_then_exact_evidence_requires_explicit_Adoption()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var installedHash = PayloadHasher.HashFolder(fixture.CanonicalPath("alpha"));
        File.Delete(fixture.StatePath);
        if (File.Exists(fixture.StatePath + ".bak"))
        {
            File.Delete(fixture.StatePath + ".bak");
        }

        var missingState = fixture.StateStore.Load();
        var inventory = new InventoryScanner().Scan(fixture.Home, missingState);

        Assert.Empty(missingState.Records);
        Assert.Equal(ManagementStatus.Unmanaged, Assert.Single(inventory.Entries).ManagementStatus);
        Assert.False(File.Exists(fixture.StatePath));

        var discovery = fixture.Provider.DiscoverAdoptions(inspection, inventory).ValueOrThrow();
        var verified = Assert.Single(new InventoryScanner().Scan(fixture.Home, missingState, discovery.Evidence).Entries);
        Assert.Equal(ManagementStatus.VerifiedAdoptionAvailable, verified.ManagementStatus);
        Assert.Empty(fixture.StateStore.Load().Records);

        fixture.Provider.Adopt(verified.AdoptionEvidence!).ValueOrThrow();
        Assert.Single(fixture.StateStore.Load().Records);
        Assert.Equal(installedHash, PayloadHasher.HashFolder(fixture.CanonicalPath("alpha")));
    }

    [Fact]
    public void Corrupt_primary_recovers_from_backup_and_repairs_primary_without_resetting_authority()
    {
        using var temp = new TemporaryStateStore();
        var state = new SkillyState { LastOperationNote = "backup authority" };
        temp.Store.Save(state);
        state.LastOperationNote = "newer primary";
        temp.Store.Save(state);
        File.WriteAllText(temp.Path, "not json");

        var recovered = temp.Store.Load();

        Assert.Equal("backup authority", recovered.LastOperationNote);
        Assert.False(temp.Store.RecoveryRequired);
        Assert.Equal(SkillyPaths.StateSchemaVersion, JsonDocument.Parse(File.ReadAllText(temp.Path)).RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.True(File.Exists(temp.Path + ".bak"));
        Assert.False(File.Exists(temp.Path + ".tmp"));
    }

    [Fact]
    public void Unsupported_newer_primary_is_read_only_and_never_falls_back_to_older_backup()
    {
        using var temp = new TemporaryStateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.Path)!);
        File.WriteAllText(temp.Path, "{\"schemaVersion\":999,\"records\":[]}");
        File.WriteAllText(temp.Path + ".bak", $"{{\"schemaVersion\":{SkillyPaths.StateSchemaVersion},\"records\":[]}}");

        var exception = Assert.Throws<RecoveryRequiredException>(() => temp.Store.Load());

        Assert.Contains("newer", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.True(temp.Store.RecoveryRequired);
        Assert.Contains("999", File.ReadAllText(temp.Path));
        Assert.Throws<RecoveryRequiredException>(() => temp.Store.Save(new SkillyState()));
    }

    [Fact]
    public void Forward_migration_backs_up_prior_state_before_atomic_schema_replacement()
    {
        using var temp = new TemporaryStateStore();
        Directory.CreateDirectory(Path.GetDirectoryName(temp.Path)!);
        const string schemaOne = "{\"schemaVersion\":1,\"records\":[],\"lastOperationNote\":\"legacy\"}";
        File.WriteAllText(temp.Path, schemaOne);

        var migrated = temp.Store.Load();

        Assert.Equal(SkillyPaths.StateSchemaVersion, migrated.SchemaVersion);
        Assert.Equal("legacy", migrated.LastOperationNote);
        Assert.Equal(1, JsonDocument.Parse(File.ReadAllText(temp.Path + ".bak")).RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(SkillyPaths.StateSchemaVersion, JsonDocument.Parse(File.ReadAllText(temp.Path)).RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.False(File.Exists(temp.Path + ".tmp"));
        Assert.False(File.Exists(temp.Path + ".bak.bak"));
    }

    [Fact]
    public void Schema_two_records_migrate_selected_content_identity_without_losing_authority()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var node = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(fixture.StatePath))!.AsObject();
        node["schemaVersion"] = 2;
        var recordNode = node["records"]!.AsArray()[0]!.AsObject();
        recordNode["provenance"]!.AsObject().Remove("selectedContentIdentity");
        File.WriteAllText(fixture.StatePath, node.ToJsonString());

        var migrated = fixture.StateStore.Load();

        var record = Assert.Single(migrated.Records);
        Assert.Equal("payload-sha256:" + record.InstalledPayloadHash, record.Provenance.SelectedContentIdentity);
        Assert.Equal(SkillyPaths.StateSchemaVersion, migrated.SchemaVersion);
        Assert.True(File.Exists(fixture.StatePath + ".bak"));
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            var target = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target);
        }
    }

    private static ManagementRecord Clone(ManagementRecord record)
    {
        var json = JsonSerializer.Serialize(record);
        return JsonSerializer.Deserialize<ManagementRecord>(json)!;
    }

    private static string SkillsLock(string sourceUrl) => JsonSerializer.Serialize(new
    {
        version = 3,
        skills = new Dictionary<string, object>
        {
            ["alpha"] = new
            {
                source = "acme/library", sourceType = "github", sourceUrl,
                skillPath = "skills/alpha/SKILL.md", skillFolderHash = "f8608cc25b81e3855fdf8e94605e6f2570af916a",
            },
        },
    });

    private static string ApmLock(string fileHash) => $$"""
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
              .agents/skills/alpha/SKILL.md: sha256:{{fileHash}}
        """;

    private sealed class TemporaryStateStore : IDisposable
    {
        private readonly string _root = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "skilly-state-" + Guid.NewGuid().ToString("N"));

        public TemporaryStateStore()
        {
            Path = System.IO.Path.Combine(_root, "state.json");
            Store = new StateStore(new RollingLog(System.IO.Path.Combine(_root, "logs")), Path);
        }

        public string Path { get; }

        public StateStore Store { get; }

        public void Dispose() => PackagedAppFixture.TryDeleteDirectory(_root);
    }
}
