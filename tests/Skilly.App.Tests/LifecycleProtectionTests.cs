using System.IO;
using Skilly.Infrastructure;
using Skilly.Providers.GitHub;
using Skilly.Skills;
using Skilly.State;
using Skilly.ViewModels;

namespace Skilly.App.Tests;

public sealed class LifecycleProtectionTests
{
    [Fact]
    public void Managed_Reinstall_replaces_Locally_Modified_content_without_merge_and_cleans_recovery_data()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        File.WriteAllText(Path.Combine(record.CanonicalPath, "local-only.txt"), "must not merge");
        fixture.SetCommit(GitHubProviderFixture.LaterCommitSha);
        File.AppendAllText(
            Path.Combine(fixture.FixtureRoot, "files", "skills", "alpha", "SKILL.md"),
            "\nClean replacement.\n");

        var inventory = new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load());
        var row = new InventoryRow(Assert.Single(inventory.Entries));
        Assert.Equal("Locally Modified", row.Health);
        Assert.False(row.CanUpdate);
        Assert.True(row.CanManagedReinstall);

        var plan = fixture.Provider.PlanManagedReinstall(record).ValueOrThrow();
        Assert.Equal(record.CanonicalPath, plan.ExactPath);
        Assert.Equal(GitHubProviderFixture.LaterCommitSha, plan.Revision);

        fixture.Provider.ManagedReinstall(plan).ValueOrThrow();

        Assert.False(File.Exists(Path.Combine(record.CanonicalPath, "local-only.txt")));
        Assert.True(Junction.IsJunctionTo(record.IntendedClaudeJunctionPath!, record.CanonicalPath));
        var persisted = Assert.Single(fixture.StateStore.Load().Records);
        Assert.Equal(OperationOutcome.Reinstalled, persisted.LastOperationOutcome);
        Assert.Equal(plan.Revision, persisted.InstalledRevision);
        Assert.Equal(plan.PayloadHash, PayloadHasher.HashFolder(persisted.CanonicalPath));
        Assert.Null(fixture.StateStore.Load().PendingOperation);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "recovery")));
    }

    [Fact]
    public void Provider_false_success_during_Managed_Reinstall_is_rejected_without_overwrite()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        var skillMd = Path.Combine(record.CanonicalPath, "SKILL.md");
        File.AppendAllText(skillMd, "\nLocal edit.\n");
        var before = File.ReadAllText(skillMd);
        fixture.ReturnFalseSuccessFor("/contents/skills/alpha/SKILL.md");

        var plan = fixture.Provider.PlanManagedReinstall(record);

        Assert.False(plan.Succeeded);
        Assert.Contains("content", plan.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(before, File.ReadAllText(skillMd));
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    [Fact]
    public void Healthy_Managed_uninstall_removes_content_and_exposure_before_authority()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        var row = new InventoryRow(Assert.Single(new InventoryScanner().Scan(fixture.Home, fixture.StateStore.Load()).Entries));
        Assert.True(row.CanUninstall);

        fixture.Provider.Uninstall(record).ValueOrThrow();

        Assert.False(Directory.Exists(record.CanonicalPath));
        Assert.False(Directory.Exists(record.IntendedClaudeJunctionPath));
        var state = fixture.StateStore.Load();
        Assert.Empty(state.Records);
        Assert.Null(state.PendingOperation);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "recovery")));
    }

    [Fact]
    public void Failed_uninstall_state_commit_restores_content_exposure_and_authority_when_safe()
    {
        var armed = false;
        var rejected = false;
        using var fixture = new GitHubProviderFixture(beforeStateSave: state =>
        {
            if (armed && !rejected && state.PendingOperation is null && state.LastOperationNote?.Contains("uninstalled", StringComparison.Ordinal) == true)
            {
                rejected = true;
                throw new IOException("injected authority commit failure");
            }
        });
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        armed = true;

        var result = fixture.Provider.Uninstall(record);

        Assert.False(result.Succeeded);
        Assert.Contains("safely restored", result.Diagnostics, StringComparison.OrdinalIgnoreCase);
        Assert.True(Directory.Exists(record.CanonicalPath));
        Assert.True(Junction.IsJunctionTo(record.IntendedClaudeJunctionPath!, record.CanonicalPath));
        Assert.Single(fixture.StateStore.Load().Records);
        Assert.Null(fixture.StateStore.Load().PendingOperation);
    }

    [Fact]
    public void Remove_Local_Folder_removes_only_exact_Unmanaged_Installation_with_temporary_snapshot()
    {
        using var fixture = new GitHubProviderFixture();
        var unmanaged = fixture.CanonicalPath("unmanaged");
        Directory.CreateDirectory(unmanaged);
        File.WriteAllText(Path.Combine(unmanaged, "SKILL.md"), "---\nname: unmanaged\ndescription: Local only.\n---\n");
        var sibling = fixture.CanonicalPath("keep");
        Directory.CreateDirectory(sibling);
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
        var row = new InventoryRow(Assert.Single(
            new InventoryScanner().Scan(fixture.Home).Entries,
            entry => entry.LocalPath == unmanaged));
        Assert.True(row.CanRemoveLocalFolder);

        fixture.Provider.RemoveLocalFolder(unmanaged).ValueOrThrow();

        Assert.False(Directory.Exists(unmanaged));
        Assert.Equal("keep", File.ReadAllText(Path.Combine(sibling, "keep.txt")));
        Assert.Null(fixture.StateStore.Load().PendingOperation);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "recovery")));
    }

    [Fact]
    public void Cancellation_retains_pending_snapshot_and_restart_safely_restores_without_retry()
    {
        using var fixture = new GitHubProviderFixture();
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        File.AppendAllText(Path.Combine(record.CanonicalPath, "SKILL.md"), "\nLocal edit.\n");
        var startingHash = PayloadHasher.HashFolder(record.CanonicalPath);
        var plan = fixture.Provider.PlanManagedReinstall(record).ValueOrThrow();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var interrupted = fixture.Provider.ManagedReinstall(plan, cancellation.Token);

        Assert.False(interrupted.Succeeded);
        var pending = fixture.StateStore.Load().PendingOperation;
        Assert.NotNull(pending);
        Assert.True(pending.CancellationRequested);
        Assert.True(Directory.Exists(pending.RecoveryDirectory));

        var recovered = fixture.Lifecycle.RecoverPendingOperation();

        Assert.Equal(RecoveryDisposition.Restored, recovered.Disposition);
        Assert.Equal(startingHash, PayloadHasher.HashFolder(record.CanonicalPath));
        Assert.Null(fixture.StateStore.Load().PendingOperation);
        Assert.False(Directory.Exists(Path.Combine(Path.GetDirectoryName(fixture.StatePath)!, "recovery")));
    }

    [Fact]
    public void Ambiguous_failed_restore_retains_journal_and_enters_read_only_Recovery_Required()
    {
        var armed = false;
        var injected = false;
        using var fixture = new GitHubProviderFixture(beforeStateSave: state =>
        {
            if (!armed || injected || state.PendingOperation?.Phase != PendingOperationPhase.Verified)
            {
                return;
            }

            injected = true;
            var record = Assert.Single(state.Records);
            File.WriteAllText(Path.Combine(record.CanonicalPath, "ambiguous.txt"), "unknown concurrent content");
            throw new IOException("injected state commit failure with ambiguous content");
        });
        var inspection = fixture.Provider.Inspect(fixture.Reference).ValueOrThrow();
        fixture.Provider.Install(inspection, [inspection.Skills[0]]).ValueOrThrow();
        var record = Assert.Single(fixture.StateStore.Load().Records);
        File.AppendAllText(Path.Combine(record.CanonicalPath, "SKILL.md"), "\nLocal edit.\n");
        var plan = fixture.Provider.PlanManagedReinstall(record).ValueOrThrow();
        armed = true;

        var result = fixture.Provider.ManagedReinstall(plan);

        Assert.False(result.Succeeded);
        Assert.Contains("Recovery Required", result.Diagnostics);
        Assert.True(fixture.StateStore.RecoveryRequired);
        Assert.NotNull(fixture.StateStore.Load().PendingOperation);
        Assert.True(File.Exists(Path.Combine(record.CanonicalPath, "ambiguous.txt")));
    }

    [Fact]
    public void Corrupt_primary_and_backup_authority_enters_Recovery_Required_instead_of_resetting()
    {
        using var fixture = new GitHubProviderFixture();
        Directory.CreateDirectory(Path.GetDirectoryName(fixture.StatePath)!);
        File.WriteAllText(fixture.StatePath, "not json");
        File.WriteAllText(fixture.StatePath + ".bak", "also not json");

        Assert.Throws<RecoveryRequiredException>(() => fixture.StateStore.Load());
        Assert.True(fixture.StateStore.RecoveryRequired);
        Assert.Contains("could not be loaded", fixture.StateStore.RecoveryDiagnostic, StringComparison.OrdinalIgnoreCase);
    }
}
