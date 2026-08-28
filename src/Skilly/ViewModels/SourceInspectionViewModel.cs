using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Skilly.Providers.GitHub;

namespace Skilly.ViewModels;

public sealed class SelectableSourceSkill : INotifyPropertyChanged
{
    private bool _isSelected;

    public SelectableSourceSkill(SourceSkill skill)
    {
        Skill = skill;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SourceSkill Skill { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value || !Skill.MetadataValid)
            {
                return;
            }

            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    public string Alias => Skill.DeclaredName is null || Skill.DeclaredName == Skill.FolderName
        ? Skill.FolderName
        : $"{Skill.FolderName} (declared: {Skill.DeclaredName})";

    public string Installability => Skill.MetadataValid ? "Installable" : $"Invalid metadata: {Skill.MetadataError}";
}

public sealed class SourceInspectionViewModel : INotifyPropertyChanged
{
    private string _status;
    private string _exactSelection = string.Empty;
    private bool _isBusy;
    private readonly bool _mutationsAllowed;

    public SourceInspectionViewModel(SourceInspection inspection, bool mutationsAllowed = true)
    {
        _mutationsAllowed = mutationsAllowed;
        Inspection = inspection;
        Skills = [.. inspection.Skills.Select(static skill => new SelectableSourceSkill(skill))];
        foreach (var item in Skills)
        {
            item.PropertyChanged += (_, _) =>
            {
                OnPropertyChanged(nameof(SelectedCount));
                OnPropertyChanged(nameof(CanInstall));
            };
        }

        _status = $"Read-only inspection found {Skills.Count} Source Skill(s). Nothing changed.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public SourceInspection Inspection { get; }

    public ObservableCollection<SelectableSourceSkill> Skills { get; }

    public string Source => Inspection.Reference.Normalized;

    public string Heading => $"{Inspection.Reference.Owner}/{Inspection.Reference.Repository}";

    public string DiscoveryLine => $"{Skills.Count} Source Skill(s) discovered — read-only scan; nothing is installed until you confirm.";

    public string TrackingRule => Inspection.RequestedTrackingRule;

    public string Commit => Inspection.Commit.Sha[..Math.Min(12, Inspection.Commit.Sha.Length)];

    public int SelectedCount => Skills.Count(static item => item.IsSelected);

    public bool CanInstall => _mutationsAllowed && !_isBusy && SelectedCount > 0;

    public bool CanSelect => !_isBusy;

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (_isBusy == value)
            {
                return;
            }

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
            if (_status == value)
            {
                return;
            }

            _status = value;
            OnPropertyChanged();
        }
    }

    public string ExactSelection
    {
        get => _exactSelection;
        set
        {
            if (_exactSelection == value)
            {
                return;
            }

            _exactSelection = value;
            OnPropertyChanged();
        }
    }

    public void SelectAll(bool selected)
    {
        foreach (var item in Skills.Where(static item => item.Skill.MetadataValid))
        {
            item.IsSelected = selected;
        }
    }

    public bool SelectExact()
    {
        var candidate = ExactSelection.Trim();
        var matches = Skills.Where(item => item.Skill.MatchesAlias(candidate)).ToList();
        if (matches.Count == 0)
        {
            Status = $"No exact Source Skill path or declared-name alias matches '{candidate}'. Nothing changed.";
            return false;
        }

        if (matches.Count > 1)
        {
            Status = $"'{candidate}' is ambiguous across {matches.Count} Source Skills. Select the exact relative path instead.";
            return false;
        }

        var match = matches[0];

        if (!match.Skill.MetadataValid)
        {
            Status = $"'{candidate}' identifies a Source Skill with invalid metadata and cannot be selected.";
            return false;
        }

        match.IsSelected = true;
        Status = $"Selected '{match.Skill.SkillPath}' by exact path or declared-name alias.";
        return true;
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
