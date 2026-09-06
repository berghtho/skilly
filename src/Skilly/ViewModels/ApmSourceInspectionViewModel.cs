using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Skilly.Providers.Apm;

namespace Skilly.ViewModels;

public sealed class SelectableApmSourceSkill : INotifyPropertyChanged, IBrowsableSourceSkill
{
    private bool _isSelected;
    public SelectableApmSourceSkill(ApmSourceSkill skill, bool destinationExists = false) { Skill = skill; IsInstalled = destinationExists; }
    public event PropertyChangedEventHandler? PropertyChanged;
    public ApmSourceSkill Skill { get; }
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value || !CanToggle) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
        }
    }
    public bool IsChecked { get => IsInstalled || IsSelected; set => IsSelected = value; }
    public bool CanToggle => Skill.MetadataValid && !IsInstalled;
    public bool IsInstalled { get; }
    public string SearchContent => $"{Skill.SkillPath}\n{Alias}\n{Skill.Description}";
    public string PreviewText => $"{Skill.SkillPath}\n{Skill.Description}\n\n{Skill.SkillMarkdown ?? "SKILL.md content is unavailable for this inspection."}";
    public string Alias => Skill.DeclaredName;
    public string Installability => IsInstalled ? "Already present locally" : Skill.MetadataValid ? "Installable" : $"Invalid APM identity: {Skill.MetadataError}";
}

public sealed class ApmSourceInspectionViewModel : INotifyPropertyChanged
{
    private string _status;
    private string _exactSelection = string.Empty;
    private bool _isBusy;
    private readonly bool _mutationsAllowed;

    public ApmSourceInspectionViewModel(ApmInspection inspection, bool mutationsAllowed = true, ISet<string>? occupiedFolders = null)
    {
        Inspection = inspection;
        _mutationsAllowed = mutationsAllowed;
        Skills = [.. inspection.Skills.Select(skill => new SelectableApmSourceSkill(skill, occupiedFolders?.Contains(skill.FolderName) == true))];
        Browser = new SourceSkillBrowser(Skills);
        foreach (var item in Skills) item.PropertyChanged += (_, _) => { OnPropertyChanged(nameof(SelectedCount)); OnPropertyChanged(nameof(CanInstall)); Browser.SelectionChanged(); };
        _status = $"Read-only APM inspection found {Skills.Count} Source Skill(s) in an isolated home. User state was not changed.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public ApmInspection Inspection { get; }
    public ObservableCollection<SelectableApmSourceSkill> Skills { get; }
    public SourceSkillBrowser Browser { get; }
    public string Source => Inspection.NormalizedSource;
    public string Heading => Inspection.NormalizedSource;
    public string DiscoveryLine => $"{Skills.Count} Source Skill(s) discovered — read-only scan; nothing is installed until you confirm.";
    public string TrackingRule => Inspection.RequestedTrackingRule;
    public string Commit => $"apm-cli {Inspection.ProviderVersion}";
    public int SelectedCount => Skills.Count(item => item.IsSelected);
    public bool CanInstall => _mutationsAllowed && !_isBusy && SelectedCount > 0;
    public bool CanSelect => !_isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { if (_isBusy == value) return; _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanInstall)); OnPropertyChanged(nameof(CanSelect)); }
    }
    public string Status
    {
        get => _status;
        set { if (_status == value) return; _status = value; OnPropertyChanged(); }
    }
    public string ExactSelection
    {
        get => _exactSelection;
        set { if (_exactSelection == value) return; _exactSelection = value; OnPropertyChanged(); }
    }
    public void SelectAll(bool selected)
    {
        if (selected) { Browser.SelectVisible(); return; }
        foreach (var item in Skills.Where(item => item.CanToggle)) item.IsSelected = selected;
    }
    public bool SelectExact()
    {
        var candidate = ExactSelection.Trim();
        var matches = Skills.Where(item => item.Skill.MatchesAlias(candidate)).ToList();
        if (matches.Count != 1)
        {
            Status = matches.Count == 0 ? $"No exact APM Source Skill name matches '{candidate}'. Nothing changed." : $"'{candidate}' is ambiguous; select one exact Source Skill.";
            return false;
        }
        if (!matches[0].CanToggle)
        {
            Status = $"'{candidate}' cannot be selected: {matches[0].Installability}. Inspect the local installation in the Workbench if present.";
            return false;
        }
        matches[0].IsSelected = true;
        Status = $"Selected '{matches[0].Skill.SkillPath}' by exact APM identity.";
        return true;
    }
    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
