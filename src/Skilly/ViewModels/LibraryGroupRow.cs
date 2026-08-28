using System.ComponentModel;

namespace Skilly.ViewModels;

public sealed class LibraryGroupRow : INotifyPropertyChanged
{
    private readonly Action<LibraryGroupRow>? _expansionChanged;
    private bool _isExpanded;

    public LibraryGroupRow(
        string? key,
        IReadOnlyList<InventoryRow> members,
        bool isExpanded,
        Action<LibraryGroupRow>? expansionChanged = null)
    {
        Key = key;
        Members = members;
        _isExpanded = isExpanded;
        _expansionChanged = expansionChanged;
        ProviderLabel = key is null ? string.Empty : members[0].LibraryProviderLabel;
        Label = key is null ? "No recorded Skill Library" : members[0].LibraryLabel;
        UpdatableCount = members.Count(static member =>
            member.CanUpdate && member.Entry.ManagementRecord is not null);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string? Key { get; }

    public IReadOnlyList<InventoryRow> Members { get; }

    public string ProviderLabel { get; }

    public bool HasProviderLabel => ProviderLabel.Length > 0;

    public string Label { get; }

    public int UpdatableCount { get; }

    public bool CanUpdateLibrary => Key is not null && UpdatableCount > 0;

    public string CountLabel => UpdatableCount == 0
        ? $"{Members.Count} Skill(s)"
        : $"{Members.Count} Skill(s) · {UpdatableCount} update(s)";

    public string AutomationName => $"Skill Library {Label}";

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (_isExpanded == value)
            {
                return;
            }

            _isExpanded = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsExpanded)));
            _expansionChanged?.Invoke(this);
        }
    }
}
