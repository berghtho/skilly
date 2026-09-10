using System.IO;
using System.Windows;
using Microsoft.Win32;
using Skilly.Skills;
using Skilly.ViewModels;

namespace Skilly;

public partial class MainWindow
{
    private async void OnExportSkillSet(object sender, RoutedEventArgs e)
    {
        if (_skillSets is null || DataContext is not MainViewModel vm || !vm.CanExportSet || !await _maintenanceGate.WaitAsync(0)) return;
        vm.MaintenanceBusy = true;
        try
        {
            var entries = vm.AllEntries.Where(entry => entry.Kind == EntryKind.RealFolder && entry.Health != InstallationHealth.Missing).ToList();
            var selectedPaths = vm.SelectedRows.Select(row => row.Entry.LocalPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var choices = entries.Select(entry => new SkillSetChoice(entry.LocalPath, entry.FolderName, entry.Metadata.Description ?? "",
                entry.LocalPath, entry.Metadata.Status == MetadataReadStatus.Valid ? null : entry.Metadata.Error ?? "Invalid SKILL.md",
                selectedPaths.Count == 0 || selectedPaths.Contains(entry.LocalPath))).ToList();
            var dialog = new SkillSetWindow("My Skill set", choices, importing: false) { Owner = this };
            if (dialog.ShowDialog() != true) return;
            var save = new SaveFileDialog
            {
                Title = "Export Skill set", Filter = "Skilly Skill set (*.skilly.zip)|*.skilly.zip",
                DefaultExt = ".skilly.zip", AddExtension = true, FileName = "skills.skilly.zip", OverwritePrompt = true,
            };
            if (save.ShowDialog(this) != true) return;
            var selected = entries.Where(entry => dialog.SelectedIds.Contains(entry.LocalPath)).ToList();
            var setName = dialog.SetName;
            var archivePath = save.FileName;
            vm.Announce($"Exporting {selected.Count} Skill(s)…");
            await Task.Run(() => _skillSets.Export(archivePath, setName, selected));
            vm.Announce($"Exported {selected.Count} Skill(s) to {save.FileName}.");
        }
        catch (Exception exception)
        {
            _log.Error("Skill set export failed.", exception);
            vm.Announce($"Export failed: {exception.Message}");
            if (!_closing) MessageBox.Show(this, exception.Message, "Export failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { vm.MaintenanceBusy = false; _maintenanceGate.Release(); }
    }

    private async void OnImportSkillSet(object sender, RoutedEventArgs e)
    {
        if (_skillSets is null || DataContext is not MainViewModel vm || !vm.CanShareSets || !await _maintenanceGate.WaitAsync(0)) return;
        vm.MaintenanceBusy = true;
        try
        {
            var open = new OpenFileDialog
            {
                Title = "Import Skill set", Filter = "Skilly Skill set (*.skilly.zip;*.zip)|*.skilly.zip;*.zip", CheckFileExists = true,
            };
            if (open.ShowDialog(this) != true) return;
            vm.Announce("Validating Skill set files…");
            using var preview = await Task.Run(() => _skillSets.Open(open.FileName));
            if (_closing) return;
            var choices = preview.Skills.Select(skill => new SkillSetChoice(skill.FolderName, skill.Name, skill.Description,
                $"{skill.FolderName} · {skill.FileCount} files · {skill.Bytes / 1024d:N1} KB", skill.Conflict, true)).ToList();
            var dialog = new SkillSetWindow(preview.Name, choices, importing: true) { Owner = this };
            if (dialog.ShowDialog() != true) { vm.Announce("Import cancelled. No Skills installed."); return; }
            await ImportReviewedSkillSet(preview, dialog.SelectedIds);
        }
        catch (Exception exception)
        {
            _log.Error("Skill set import failed.", exception);
            ApplyRecoveryMode(vm);
            vm.Announce($"Import failed: {exception.Message}");
            if (!_closing) MessageBox.Show(this, exception.Message, "Import failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { vm.MaintenanceBusy = false; _maintenanceGate.Release(); }
    }

    internal async Task ImportReviewedSkillSet(SkillSetPreview preview, IReadOnlyList<string> selected)
    {
        var vm = (MainViewModel)DataContext;
        BeginMutation();
        try
        {
            vm.Announce($"Importing {selected.Count} Skill(s)…");
            var count = await Task.Run(() => _skillSets!.Import(preview, selected, _mutationCancellation!.Token));
            vm.LoadInventory(RefreshInventory());
            vm.Announce($"Imported {count} Skill(s) as Unmanaged; available to all four Harnesses.");
        }
        finally { EndMutation(); }
    }
}
