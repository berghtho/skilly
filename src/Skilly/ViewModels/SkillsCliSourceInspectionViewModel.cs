using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Skilly.Providers.SkillsCli;

namespace Skilly.ViewModels;

public sealed class SelectableSkillsCliSourceSkill : INotifyPropertyChanged
{
    private bool _isSelected;

    public SelectableSkillsCliSourceSkill(SkillsCliSourceSkill skill) => Skill = skill;

    public event PropertyChangedEventHandler? PropertyChanged;

    public SkillsCliSourceSkill Skill { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value || !Skill.MetadataValid) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Alias => Skill.DeclaredName;

    public string Installability => Skill.MetadataValid ? "Installable" : $"Invalid provider identity: {Skill.MetadataError}";
}

public sealed class SkillsCliSourceInspectionViewModel : INotifyPropertyChanged
{
    private string _status;
    private string _exactSelection = string.Empty;
    private bool _isBusy;
    private readonly bool _mutationsAllowed;

    public SkillsCliSourceInspectionViewModel(SkillsCliInspection inspection, bool mutationsAllowed = true)
    {
        Inspection = inspection;
        _mutationsAllowed = mutationsAllowed;
        Skills = [.. inspection.Skills.Select(static skill => new SelectableSkillsCliSourceSkill(skill))];
        foreach (var item in Skills)
        {
            item.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(CanInstall));
            };
        }
        _status = $"Read-only {SkillsCliClient.Package} inspection found {Skills.Count} Source Skill(s). Nothing changed.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    public SkillsCliInspection Inspection { get; }
    public ObservableCollection<SelectableSkillsCliSourceSkill> Skills { get; }
    public string Source => Inspection.NormalizedSource;
    public string Heading => Inspection.NormalizedSource;
    public string DiscoveryLine => $"{Skills.Count} Source Skill(s) discovered — read-only scan; nothing is installed until you confirm.";
    public string TrackingRule => Inspection.RequestedTrackingRule;
    public string Commit => SkillsCliClient.Package;
    public int SelectedCount => Skills.Count(static item => item.IsSelected);
    public bool CanInstall => _mutationsAllowed && !_isBusy && SelectedCount > 0;
    public bool CanSelect => !_isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value) return;
            _isBusy = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CanInstall));
            OnPropertyChanged(nameof(CanSelect));
        }
    }

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            OnPropertyChanged();
        }
    }

    public string ExactSelection
    {
        get => _exactSelection;
        set
        {
            if (_exactSelection == value) return;
            _exactSelection = value;
            OnPropertyChanged();
        }
    }

    public void SelectAll(bool selected)
    {
        foreach (var item in Skills.Where(static item => item.Skill.MetadataValid)) item.IsSelected = selected;
    }

    public bool SelectExact()
    {
        var candidate = ExactSelection.Trim();
        var matches = Skills.Where(item => item.Skill.MatchesAlias(candidate)).ToList();
        if (matches.Count != 1)
        {
            Status = matches.Count == 0
                ? $"No exact provider Source Skill name matches '{candidate}'. Nothing changed."
                : $"'{candidate}' is ambiguous; select one exact Source Skill.";
            return false;
        }
        matches[0].IsSelected = true;
        Status = $"Selected '{matches[0].Skill.SkillPath}' by exact provider identity.";
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
