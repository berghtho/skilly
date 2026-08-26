using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;

namespace Skilly.ViewModels;

public sealed record FilterCount(string Name, int Count, bool IsChecked);

public sealed record SkillRow(
    string Name,
    string Provenance,
    string ManagementStatus,
    string InstallationHealth,
    string UpdateStatus,
    string Exposures);

public sealed record StatusUpdate(string Message, DateTimeOffset Timestamp);

public sealed class MainViewModel : INotifyPropertyChanged
{
    private string _sourceText = string.Empty;
    private FilterCount _selectedFilter;
    private SkillRow? _selectedSkill;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel()
    {
        Filters =
        [
            new FilterCount("All Skills", 0, true),
            new FilterCount("Updates", 0, false),
            new FilterCount("Attention", 0, false),
            new FilterCount("Unmanaged", 0, false),
            new FilterCount("Healthy", 0, false),
        ];
        _selectedFilter = Filters[0];
        Skills.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSkills));
            OnPropertyChanged(nameof(HasNoSkills));
        };
        InspectSourceCommand = new RelayCommand(_ => SetStatus("Source inspection is not available yet. Nothing changed."));
        RefreshChecksCommand = new RelayCommand(_ => SetStatus("Update checks are not available yet. Nothing changed."));
        Status = new StatusUpdate("Ready. Nothing changed.", DateTimeOffset.Now);
    }

    public IReadOnlyList<FilterCount> Filters { get; }

    public ObservableCollection<SkillRow> Skills { get; } = [];

    public FilterCount SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (SetProperty(ref _selectedFilter, value))
            {
                OnPropertyChanged(nameof(HasSkills));
            }
        }
    }

    public SkillRow? SelectedSkill
    {
        get => _selectedSkill;
        set
        {
            if (SetProperty(ref _selectedSkill, value))
            {
                OnPropertyChanged(nameof(DetailsHeader));
                OnPropertyChanged(nameof(HasSelectedSkill));
            }
        }
    }

    public string SourceText
    {
        get => _sourceText;
        set => SetProperty(ref _sourceText, value);
    }

    public StatusUpdate Status { get; private set; }

    public bool HasSkills => Skills.Count > 0;

    public bool HasNoSkills => Skills.Count == 0;

    public bool HasSelectedSkill => SelectedSkill is not null;

    public bool HasNoSelectedSkill => SelectedSkill is null;

    public string DetailsHeader => HasSelectedSkill ? "Skill details" : "Skill details — select a Skill";

    public ICommand InspectSourceCommand { get; }

    public ICommand RefreshChecksCommand { get; }

    public void Announce(string message) => SetStatus(message);

    private void SetStatus(string message)
    {
        Status = new StatusUpdate(message, DateTimeOffset.Now);
        OnPropertyChanged(nameof(Status));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        OnPropertyChanged(propertyName);
        return true;
    }
}

public sealed class RelayCommand(Action<object?> execute) : ICommand
{
    public event EventHandler? CanExecuteChanged
    {
        add { }
        remove { }
    }

    public bool CanExecute(object? parameter) => true;

    public void Execute(object? parameter) => execute(parameter);
}
