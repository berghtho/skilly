using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Skilly.Infrastructure;
using Skilly.Providers.GitHub;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.App.Tests;

public sealed class SkillSetArchiveTests
{
    [Fact]
    public void Complete_set_round_trips_bytes_offline_and_never_imports_management_authority()
    {
        using var source = new SetFixture();
        using var receiver = new SetFixture();
        source.WriteSkill("alpha"); source.WriteSkill("beta");
        source.Write("alpha", "scripts/run.ps1", Encoding.UTF8.GetBytes("Write-Output 'hello'\r\n"));
        source.Write("alpha", "assets/picture.bin", [0, 255, 128, 42]);
        source.Write("beta", "references/日本語.md", Encoding.UTF8.GetBytes("# Reference\n"));
        source.Export();
        using (var zip = ZipFile.OpenRead(source.Archive))
        using (var reader = new StreamReader(zip.GetEntry("skill-set.json")!.Open()))
        {
            var manifest = reader.ReadToEnd();
            Assert.DoesNotContain(source.Home, manifest);
            Assert.DoesNotContain("provenance", manifest, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("state.json", manifest);
        }
        using var preview = receiver.Service.Open(source.Archive);
        Assert.Equal("Team Skills", preview.Name);
        Assert.Equal(2, preview.Skills.Count);
        Assert.All(preview.Skills, item => Assert.Null(item.Conflict));
        Assert.Equal(2, receiver.Service.Import(preview, ["alpha", "beta"]));
        foreach (var name in new[] { "alpha", "beta" })
            Assert.Equal(PayloadHasher.HashFolder(source.Canonical(name)), PayloadHasher.HashFolder(receiver.Canonical(name)));
        var state = receiver.Store.Load();
        Assert.Empty(state.Records); Assert.Null(state.PendingOperation);
        var inventory = new InventoryScanner().Scan(receiver.Home, state);
        Assert.Equal(2, inventory.Entries.Count);
        Assert.All(inventory.Entries, entry =>
        {
            Assert.Equal(ManagementStatus.Unmanaged, entry.ManagementStatus);
            Assert.Equal(InstallationHealth.Healthy, entry.Health);
            Assert.All(entry.Exposures.Values, exposure => Assert.Contains(exposure.State, new[] { ExposureState.Canonical, ExposureState.VerifiedJunction }));
        });
        using var again = receiver.Service.Open(source.Archive);
        Assert.All(again.Skills, item => Assert.NotNull(item.Conflict));
    }

    [Fact]
    public void Export_selection_contains_only_selected_payloads_and_import_can_select_a_subset()
    {
        using var source = new SetFixture(); using var receiver = new SetFixture();
        source.WriteSkill("alpha"); source.WriteSkill("beta"); source.WriteSkill("gamma");
        source.Service.Export(source.Archive, "Subset", source.Entries().Where(entry => entry.FolderName != "gamma").ToList());
        using var preview = receiver.Service.Open(source.Archive);
        Assert.Equal(2, preview.Skills.Count);
        Assert.Equal(1, receiver.Service.Import(preview, ["beta"]));
        Assert.False(Directory.Exists(receiver.Canonical("alpha")));
        Assert.True(Directory.Exists(receiver.Canonical("beta")));
        Assert.False(Directory.Exists(receiver.Canonical("gamma")));
    }

    [Theory]
    [InlineData("../escape")]
    [InlineData("/absolute")]
    [InlineData("C:/escape")]
    [InlineData("skills/alpha/../../escape")]
    [InlineData("skills\\alpha\\escape")]
    [InlineData("skills/alpha/x:stream")]
    [InlineData("skills/alpha/CON.txt")]
    [InlineData("skills/alpha/NUL")]
    [InlineData("skills/alpha/COM1.py")]
    [InlineData("skills/alpha/trailing.")]
    [InlineData("skills/alpha/trailing ")]
    [InlineData("skills/alpha//empty")]
    public void Unsafe_archive_paths_are_rejected_before_installation(string path)
    {
        using var source = new SetFixture(); using var receiver = new SetFixture();
        source.WriteSkill("alpha"); source.Export();
        AddEntry(source.Archive, path, "bad");
        Assert.Throws<InvalidDataException>(() => receiver.Service.Open(source.Archive));
        Assert.False(Directory.Exists(receiver.Canonical("alpha")));
        Assert.Empty(receiver.Store.Load().Records);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("checksum")]
    [InlineData("version")]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("link")]
    public void Invalid_archives_leave_no_payload_or_staging_files(string kind)
    {
        using var source = new SetFixture(); using var receiver = new SetFixture();
        source.WriteSkill("alpha"); source.Export();
        switch (kind)
        {
            case "unknown": AddEntry(source.Archive, "extra.txt", "unlisted"); break;
            case "duplicate": AddEntry(source.Archive, "SKILLS/ALPHA/SKILL.MD", "duplicate"); break;
            case "checksum": EditManifest(source.Archive, node => node["skills"]![0]!["files"]![0]!["sha256"] = new string('0', 64)); break;
            case "version": EditManifest(source.Archive, node => node["version"] = 999); break;
            case "null": EditManifest(source.Archive, node => node["skills"] = null); break;
            case "missing":
                using (var zip = ZipFile.Open(source.Archive, ZipArchiveMode.Update)) zip.GetEntry("skills/alpha/SKILL.md")!.Delete();
                break;
            case "link":
                using (var zip = ZipFile.Open(source.Archive, ZipArchiveMode.Update)) zip.GetEntry("skills/alpha/SKILL.md")!.ExternalAttributes = unchecked((int)0xA1FF0000);
                break;
        }
        Assert.Throws<InvalidDataException>(() => receiver.Service.Open(source.Archive));
        Assert.False(Directory.Exists(receiver.Canonical("alpha")));
        var stageRoot = Path.Combine(receiver.Home, ".agents", ".skilly-transfers");
        Assert.True(!Directory.Exists(stageRoot) || !Directory.EnumerateFileSystemEntries(stageRoot).Any());
    }

    [Theory]
    [InlineData(RootKind.CanonicalAgents)]
    [InlineData(RootKind.ClaudeSkills)]
    [InlineData(RootKind.CodexLegacySkills)]
    [InlineData(RootKind.CopilotSkills)]
    [InlineData(RootKind.OpenCodeConfigSkills)]
    public void Existing_paths_in_any_discovery_root_are_skipped_and_preserved(RootKind kind)
    {
        using var source = new SetFixture(); using var receiver = new SetFixture();
        source.WriteSkill("alpha"); source.Export();
        var occupied = Path.Combine(HarnessRoot.Create(kind, receiver.Home).FullPath, "alpha");
        Directory.CreateDirectory(Path.GetDirectoryName(occupied)!); File.WriteAllText(occupied, "keep me");
        using var preview = receiver.Service.Open(source.Archive);
        Assert.NotNull(Assert.Single(preview.Skills).Conflict);
        Assert.Throws<IOException>(() => receiver.Service.Import(preview, ["alpha"]));
        Assert.Equal("keep me", File.ReadAllText(occupied)); Assert.Null(receiver.Store.Load().PendingOperation);
    }

    [Fact]
    public void Conflict_created_after_preview_is_rechecked_before_any_installation()
    {
        using var source = new SetFixture(); using var receiver = new SetFixture();
        source.WriteSkill("alpha"); source.WriteSkill("beta"); source.Export();
        using var preview = receiver.Service.Open(source.Archive);
        receiver.WriteSkill("beta");
        var hash = PayloadHasher.HashFolder(receiver.Canonical("beta"));
        Assert.Throws<IOException>(() => receiver.Service.Import(preview, ["alpha", "beta"]));
        Assert.False(Directory.Exists(receiver.Canonical("alpha")));
        Assert.Equal(hash, PayloadHasher.HashFolder(receiver.Canonical("beta")));
    }

    [Fact]
    public void Preview_is_an_immutable_snapshot_of_archive_and_drift_in_staging_is_rejected()
    {
        using var source = new SetFixture(); using var receiver = new SetFixture();
        source.WriteSkill("alpha"); source.Export();
        using var preview = receiver.Service.Open(source.Archive);
        File.Delete(source.Archive);
        var staged = Directory.GetFiles(Path.Combine(receiver.Home, ".agents", ".skilly-transfers"), "SKILL.md", SearchOption.AllDirectories).Single();
        File.AppendAllText(staged, "changed");
        Assert.Throws<InvalidDataException>(() => receiver.Service.Import(preview, ["alpha"]));
        Assert.False(Directory.Exists(receiver.Canonical("alpha"))); Assert.Null(receiver.Store.Load().PendingOperation);
    }

    [Fact]
    public void Export_refuses_links_and_preserves_an_existing_archive_on_failure()
    {
        using var source = new SetFixture();
        source.WriteSkill("alpha"); source.Export();
        var bytes = File.ReadAllBytes(source.Archive);
        var outside = Path.Combine(source.Home, "private"); Directory.CreateDirectory(outside);
        File.WriteAllText(Path.Combine(outside, "secret.txt"), "must not export");
        var link = Path.Combine(source.Canonical("alpha"), "linked"); Junction.Create(link, outside);
        try { Assert.Throws<InvalidDataException>(() => source.Export()); Assert.Equal(bytes, File.ReadAllBytes(source.Archive)); }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public void Export_rejects_oversized_files_before_reading_them()
    {
        using var source = new SetFixture(); source.WriteSkill("alpha");
        using (var sparse = File.Create(Path.Combine(source.Canonical("alpha"), "large.bin"))) sparse.SetLength(64L * 1024 * 1024 + 1);
        Assert.Throws<InvalidDataException>(() => source.Export());
        Assert.False(File.Exists(source.Archive));
    }

    [Fact]
    public void Cancellation_and_disposed_preview_never_install_payloads()
    {
        using var source = new SetFixture(); using var receiver = new SetFixture();
        source.WriteSkill("alpha"); source.Export();
        var preview = receiver.Service.Open(source.Archive);
        Assert.Throws<OperationCanceledException>(() => receiver.Service.Import(preview, ["alpha"], new CancellationToken(true)));
        Assert.False(Directory.Exists(receiver.Canonical("alpha")));
        Assert.Null(receiver.Store.Load().PendingOperation);
        preview.Dispose();
        Assert.Throws<ObjectDisposedException>(() => receiver.Service.Import(preview, ["alpha"]));
    }

    [Fact]
    public void Commit_failure_rolls_back_all_folders_and_exposures_without_authority()
    {
        using var source = new SetFixture(); using var receiver = new SetFixture();
        source.WriteSkill("alpha"); source.WriteSkill("beta"); source.Export();
        using var preview = receiver.Service.Open(source.Archive);
        var failed = false;
        var store = new StateStore(receiver.Log, receiver.Store.FilePath, state =>
        {
            if (state.PendingOperation is null && !failed) { failed = true; throw new IOException("injected commit failure"); }
        });
        Assert.Throws<IOException>(() => new SkillSetArchive(store, receiver.Home).Import(preview, ["alpha", "beta"]));
        Assert.True(failed);
        foreach (var name in new[] { "alpha", "beta" })
        { Assert.False(Directory.Exists(receiver.Canonical(name))); Assert.False(Directory.Exists(receiver.Claude(name))); }
        Assert.Empty(receiver.Store.Load().Records); Assert.Null(receiver.Store.Load().PendingOperation);
    }

    [Fact]
    public void Concurrent_foreign_destination_is_never_deleted_on_import_failure()
    {
        using var source = new SetFixture(); using var receiver = new SetFixture();
        source.WriteSkill("alpha"); source.WriteSkill("beta"); source.Export();
        using var preview = receiver.Service.Open(source.Archive);
        var store = new StateStore(receiver.Log, receiver.Store.FilePath, state =>
        {
            if (state.PendingOperation is not null) receiver.WriteSkill("beta");
        });
        Assert.Throws<IOException>(() => new SkillSetArchive(store, receiver.Home).Import(preview, ["alpha", "beta"]));
        Assert.True(File.Exists(Path.Combine(receiver.Canonical("beta"), "SKILL.md")));
        Assert.False(Directory.Exists(receiver.Canonical("alpha")));
    }

    [Theory]
    [InlineData(false, false, RecoveryDisposition.Restored)]
    [InlineData(true, false, RecoveryDisposition.Completed)]
    [InlineData(false, true, RecoveryDisposition.RecoveryRequired)]
    public void Restart_reconciles_import_without_replaying_or_trusting_modified_files(bool verified, bool changed, RecoveryDisposition expected)
    {
        using var receiver = new SetFixture(); receiver.WriteSkill("alpha");
        Junction.Create(receiver.Claude("alpha"), receiver.Canonical("alpha"));
        var state = receiver.Store.Load();
        state.PendingOperation = new PendingOperation
        {
            OperationId = Guid.NewGuid().ToString("N"), OperationType = MutationType.ImportSkillSet, StartedAt = DateTimeOffset.Now,
            SkillSetTargets = [new SkillSetImportTarget("alpha", GitTreeHasher.HashFolder(receiver.Canonical("alpha")))],
            CreatedSkillSetFolders = ["alpha"],
            CreatedSkillSetExposures = ["alpha"],
            StartingPaths = [receiver.Canonical("alpha"), receiver.Claude("alpha")],
            StartingPathStates = [PathState.Missing, PathState.Missing],
            Phase = verified ? PendingOperationPhase.Verified : PendingOperationPhase.Journaled,
        };
        receiver.Store.Save(state);
        if (changed) File.AppendAllText(Path.Combine(receiver.Canonical("alpha"), "SKILL.md"), "user edit");
        Assert.Equal(expected, receiver.Service.RecoverPendingImport().Disposition);
        Assert.Empty(receiver.Store.Load().Records);
        Assert.Equal(verified || changed, Directory.Exists(receiver.Canonical("alpha")));
        Assert.Equal(changed, receiver.Store.Load().PendingOperation is not null);
    }

    [Fact]
    public void Recovery_preserves_an_identical_foreign_folder_without_durable_creation_evidence()
    {
        using var receiver = new SetFixture(); receiver.WriteSkill("alpha");
        var state = receiver.Store.Load();
        state.PendingOperation = PendingImport(receiver, "alpha");
        state.PendingOperation.CreatedSkillSetFolders.Clear();
        receiver.Store.Save(state);
        Assert.Equal(RecoveryDisposition.RecoveryRequired, receiver.Service.RecoverPendingImport().Disposition);
        Assert.True(File.Exists(Path.Combine(receiver.Canonical("alpha"), "SKILL.md")));
    }

    [Fact]
    public void Invalid_recovery_temporary_path_prevents_all_deletion()
    {
        using var receiver = new SetFixture(); receiver.WriteSkill("alpha");
        var state = receiver.Store.Load(); state.PendingOperation = PendingImport(receiver, "alpha");
        state.PendingOperation.TemporaryPaths = [receiver.Home]; receiver.Store.Save(state);
        Assert.Equal(RecoveryDisposition.RecoveryRequired, receiver.Service.RecoverPendingImport().Disposition);
        Assert.True(File.Exists(Path.Combine(receiver.Canonical("alpha"), "SKILL.md")));
    }

    [Fact]
    public void Corrupt_primary_during_import_retains_a_pending_journal_in_backup()
    {
        using var source = new SetFixture(); using var receiver = new SetFixture();
        source.WriteSkill("alpha"); source.Export();
        using var preview = receiver.Service.Open(source.Archive);
        var observed = false;
        var store = new StateStore(receiver.Log, receiver.Store.FilePath, state =>
        {
            if (state.PendingOperation?.CreatedSkillSetFolders.Count == 1 && !observed)
            {
                observed = true;
                File.WriteAllText(receiver.Store.FilePath, "corrupt primary");
                var restartedStore = new StateStore(receiver.Log, receiver.Store.FilePath);
                Assert.Equal(MutationType.ImportSkillSet, restartedStore.Load().PendingOperation?.OperationType);
                Assert.Equal(RecoveryDisposition.RecoveryRequired, new SkillSetArchive(restartedStore, receiver.Home).RecoverPendingImport().Disposition);
                Assert.True(Directory.Exists(receiver.Canonical("alpha")));
                throw new IOException("simulated interruption before creation record persisted");
            }
        });
        Assert.Throws<IOException>(() => new SkillSetArchive(store, receiver.Home).Import(preview, ["alpha"]));
        Assert.True(observed);
    }

    private static PendingOperation PendingImport(SetFixture fixture, string name) => new()
    {
        OperationId = Guid.NewGuid().ToString("N"), OperationType = MutationType.ImportSkillSet, StartedAt = DateTimeOffset.Now,
        SkillSetTargets = [new SkillSetImportTarget(name, GitTreeHasher.HashFolder(fixture.Canonical(name)))],
        CreatedSkillSetFolders = [name], StartingPaths = [fixture.Canonical(name), fixture.Claude(name)],
        StartingPathStates = [PathState.Missing, PathState.Missing], Phase = PendingOperationPhase.MutationStarted,
    };

    [Fact]
    public void Failed_junction_setup_removes_only_its_own_empty_reservation()
    {
        using var fixture = new SetFixture();
        var link = Path.Combine(fixture.Home, "exposure");
        Assert.Throws<ArgumentException>(() => Junction.Create(link, "", requireNew: true));
        Assert.False(Directory.Exists(link));
    }

    [Fact]
    public void New_junction_refuses_a_preexisting_empty_directory()
    {
        using var fixture = new SetFixture(); fixture.WriteSkill("alpha");
        var link = Path.Combine(fixture.Home, "exposure"); Directory.CreateDirectory(link);
        Assert.Throws<System.ComponentModel.Win32Exception>(() => Junction.Create(link, fixture.Canonical("alpha"), requireNew: true));
        Assert.True(Directory.Exists(link));
        Assert.False(File.GetAttributes(link).HasFlag(FileAttributes.ReparsePoint));
    }

    [Fact]
    public void A_matching_junction_created_by_another_process_is_preserved_on_failure()
    {
        using var source = new SetFixture(); using var receiver = new SetFixture();
        source.WriteSkill("alpha"); source.Export();
        using var preview = receiver.Service.Open(source.Archive);
        var injected = false;
        var store = new StateStore(receiver.Log, receiver.Store.FilePath, state =>
        {
            if (state.PendingOperation?.CreatedSkillSetFolders.Count == 1 && !injected)
            {
                injected = true;
                Junction.Create(receiver.Claude("alpha"), receiver.Canonical("alpha"));
            }
        });
        Assert.Throws<RecoveryRequiredException>(() => new SkillSetArchive(store, receiver.Home).Import(preview, ["alpha"]));
        Assert.True(Junction.IsJunctionTo(receiver.Claude("alpha"), receiver.Canonical("alpha")));
        Assert.Equal(RecoveryDisposition.RecoveryRequired, receiver.Service.RecoverPendingImport().Disposition);
        Assert.True(Junction.IsJunctionTo(receiver.Claude("alpha"), receiver.Canonical("alpha")));
    }

    private static void AddEntry(string archive, string path, string text)
    {
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
        using var writer = new StreamWriter(zip.CreateEntry(path).Open()); writer.Write(text);
    }

    private static void EditManifest(string archive, Action<JsonObject> edit)
    {
        using var zip = ZipFile.Open(archive, ZipArchiveMode.Update);
        var entry = zip.GetEntry("skill-set.json")!;
        JsonObject node;
        using (var reader = new StreamReader(entry.Open())) node = JsonNode.Parse(reader.ReadToEnd())!.AsObject();
        edit(node); entry.Delete();
        using var writer = new StreamWriter(zip.CreateEntry("skill-set.json").Open()); writer.Write(node.ToJsonString());
    }

    private sealed class SetFixture : IDisposable
    {
        public string Home { get; } = Path.Combine(Path.GetTempPath(), "skilly-sets-" + Guid.NewGuid().ToString("N"));
        public RollingLog Log { get; }
        public StateStore Store { get; }
        public SkillSetArchive Service { get; }
        public string Archive => Path.Combine(Home, "team.skilly.zip");
        public SetFixture()
        {
            Directory.CreateDirectory(Home);
            Log = new RollingLog(Path.Combine(PackagedAppFixture.FindRepoRoot(), "artifacts", "skill-sets", "logs", Path.GetFileName(Home)));
            Store = new StateStore(Log, Path.Combine(Home, "state.json"));
            Service = new SkillSetArchive(Store, Home);
        }
        public string Canonical(string name) => Path.Combine(HarnessRoot.Create(RootKind.CanonicalAgents, Home).FullPath, name);
        public string Claude(string name) => Path.Combine(HarnessRoot.Create(RootKind.ClaudeSkills, Home).FullPath, name);
        public void WriteSkill(string name) => Write(name, "SKILL.md", Encoding.UTF8.GetBytes($"---\nname: {name}\ndescription: Test Skill\n---\n# {name}\n"));
        public void Write(string name, string relative, byte[] bytes)
        {
            var path = Path.Combine(Canonical(name), relative.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllBytes(path, bytes);
        }
        public IReadOnlyList<InventoryEntry> Entries() => new InventoryScanner().Scan(Home).Entries;
        public void Export() => Service.Export(Archive, "Team Skills", Entries());
        public void Dispose()
        {
            var claudeRoot = HarnessRoot.Create(RootKind.ClaudeSkills, Home).FullPath;
            if (Directory.Exists(claudeRoot))
                foreach (var entry in Directory.EnumerateDirectories(claudeRoot))
                    if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) Directory.Delete(entry);
            PackagedAppFixture.TryDeleteDirectory(Home);
        }
    }
}
