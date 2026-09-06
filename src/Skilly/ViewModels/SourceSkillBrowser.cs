using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Skilly.ViewModels;

public interface IBrowsableSourceSkill
{
    string SearchContent { get; }
    string PreviewText { get; }
    bool CanToggle { get; }
    bool IsSelected { get; set; }
}

/// <summary>Keeps install selection independent of search and preview focus.</summary>
public sealed class SourceSkillBrowser(IEnumerable<IBrowsableSourceSkill> skills) : INotifyPropertyChanged
{
    private readonly IReadOnlyList<IBrowsableSourceSkill> _all = skills.ToList();
    private string _searchText = string.Empty;
    private IBrowsableSourceSkill? _previewSkill;
    public event PropertyChangedEventHandler? PropertyChanged;
    public ObservableCollection<IBrowsableSourceSkill> VisibleSkills { get; } = new(skills);

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (_searchText == value) return;
            _searchText = value;
            VisibleSkills.Clear();
            foreach (var skill in _all.Where(skill => skill.SearchContent.Contains(value.Trim(), StringComparison.OrdinalIgnoreCase)))
                VisibleSkills.Add(skill);
            if (_previewSkill is not null && !VisibleSkills.Contains(_previewSkill)) PreviewSkill = null;
            Changed();
            Changed(nameof(ResultSummary));
        }
    }

    public string ResultSummary => $"{VisibleSkills.Count} of {_all.Count} shown; {_all.Count(skill => skill.IsSelected && !VisibleSkills.Contains(skill))} selected outside this search.";

    public IBrowsableSourceSkill? PreviewSkill
    {
        get => _previewSkill;
        set { _previewSkill = value; Changed(); Changed(nameof(PreviewText)); }
    }

    public string PreviewText => _previewSkill?.PreviewText ?? "Select a row to read its description and SKILL.md. Check its box to select it for installation.";

    public void SelectVisible()
    {
        foreach (var skill in VisibleSkills.Where(skill => skill.CanToggle)) skill.IsSelected = true;
        SelectionChanged();
    }

    public void SelectionChanged() => Changed(nameof(ResultSummary));

    private void Changed([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
