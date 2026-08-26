using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using Skilly.Skills;
using Skilly.State;

namespace Skilly.ViewModels;

public sealed record FilterCount(string Name, int Count);

public record StatusUpdate(string Message, DateTimeOffset Timestamp);

public sealed class InventoryRow
{
    public InventoryRow(InventoryEntry entry)
    {
        Entry = entry;
        RootLabel = entry.RootKind switch
        {
            RootKind.CanonicalAgents => ".agents\\skills",
            RootKind.ClaudeSkills => ".claude\\skills",
            RootKind.CopilotSkills => ".copilot\\skills",
            RootKind.OpenCodeConfigSkills => ".config\\opencode\\skills",
            RootKind.CodexLegacySkills => ".codex\\skills (legacy)",
            _ => entry.RootKind.ToString(),
        };
        ExposuresSummary = $"{entry.Exposures.Values.Count(static value => value.State is ExposureState.Canonical or ExposureState.Direct or ExposureState.VerifiedJunction)} of 4 harnesses exposed";
        ExposureDisplay = new Dictionary<string, HarnessExposure>
        {
            ["OpenCode"] = entry.Exposures[Harness.OpenCode],
            ["Codex"] = entry.Exposures[Harness.Codex],
            ["ClaudeCode"] = entry.Exposures[Harness.ClaudeCode],
            ["GitHubCopilot"] = entry.Exposures[Harness.GitHubCopilot],
        };
    }

    public InventoryEntry Entry { get; }

    public IReadOnlyDictionary<string, HarnessExposure> ExposureDisplay { get; }

    public string Name => Entry.FolderName;

    public string DisplayName => Entry.DisplayName;

    public string RootLabel { get; }

    private ManagementRecord? Record => Entry.ManagementRecord ?? Entry.AdoptionEvidence?.ProposedRecord;

    private CheckSnapshot? Check => Record?.LatestCheck;

    public string Provenance => Record is null
        ? "Not recorded"
        : $"GitHub - {Record.Provenance.Owner}/{Record.Provenance.Repository}";

    public string Source => Record?.Provenance.OriginalReference ?? "Not recorded";

    public string SourceSkillPath => Record?.Provenance.SourceSkillPath ?? "Not recorded";

    public string TrackingRule => Record is null
        ? "Not recorded"
        : $"{Record.Provenance.TrackingRule} ({Record.Provenance.TrackingRuleKind})";

    public string Management => Entry.ManagementStatus switch
    {
        ManagementStatus.Managed => "Managed",
        ManagementStatus.VerifiedAdoptionAvailable => "Verified Adoption Available",
        ManagementStatus.Unmanaged => "Unmanaged",
        _ => Entry.ManagementStatus.ToString(),
    };

    public string Health => Entry.Health switch
    {
        InstallationHealth.Healthy => "Healthy",
        InstallationHealth.LocallyModified => "Locally Modified",
        InstallationHealth.Missing => "Missing",
        InstallationHealth.ExposureProblem => "Exposure Problem",
        InstallationHealth.InvalidMetadata => "Invalid Metadata",
        InstallationHealth.Collision => "Collision",
        _ => Entry.Health.ToString(),
    };

    public string UpdateStatus
    {
        get
        {
            if (Entry.Health == InstallationHealth.LocallyModified)
            {
                return "Not available (locally modified)";
            }

            var value = Check?.Status switch
            {
                State.UpdateStatus.Current => "Current",
                State.UpdateStatus.UpdateAvailable => "Update Available",
                State.UpdateStatus.Pinned => "Pinned",
                State.UpdateStatus.SourceUnavailable => "Source Unavailable",
                State.UpdateStatus.CheckFailed => "Check Failed",
                _ => "Not checked",
            };
            return Check?.IsStale == true ? value + " (stale - check failed)" : value;
        }
    }

    public string InstalledRevision => FormatRevision(
        Check?.InstalledRevision ?? Record?.InstalledRevision,
        Check?.InstalledRevisionDate);

    public string AvailableRevision => FormatRevision(Check?.AvailableRevision, Check?.AvailableRevisionDate);

    public string LastChecked => Check is null ? "Not checked" : Check.CheckedAt.ToString("yyyy-MM-dd HH:mm:ss zzz");

    public string CheckNotice => Check?.Failure ?? Check?.Warning ?? string.Empty;

    public bool HasCheckNotice => CheckNotice.Length > 0;

    public bool CanUpdate => Entry.Health == InstallationHealth.Healthy
                             && Check?.Status == State.UpdateStatus.UpdateAvailable
                             && !Check.IsStale
                               && Check.Failure is null;

    public bool CanAdopt => Entry.ManagementStatus == ManagementStatus.VerifiedAdoptionAvailable
                            && Entry.Health == InstallationHealth.Healthy
                            && Entry.AdoptionEvidence is not null;

    public bool CanManagedReinstall => Entry.ManagementStatus == ManagementStatus.Managed
                                         && Entry.Health is InstallationHealth.LocallyModified or InstallationHealth.ExposureProblem;

    public bool CanUninstall => Entry.ManagementStatus == ManagementStatus.Managed
                                && Entry.Health == InstallationHealth.Healthy;

    public bool CanRemoveLocalFolder => Entry.ManagementStatus == ManagementStatus.Unmanaged
                                        && Entry.Kind == EntryKind.RealFolder;

    public string ActionState => CanAdopt
            ? "Direct Adoption is available. It records verified Provenance and preserves Skill content."
            : CanUpdate
            ? "A verified direct update is available."
            : Entry.Health == InstallationHealth.LocallyModified
            ? "Normal update is blocked because this Skill Installation is Locally Modified. Managed Reinstall is available."
            : Check?.IsStale == true
                ? "Refresh checks successfully before updating."
                : "No direct update is available.";

    public string ExposuresSummary { get; }

    public bool MatchesSearch(string searchText)
    {
        if (string.IsNullOrWhiteSpace(searchText))
        {
            return true;
        }

        var entry = Entry;
        return entry.FolderName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
               || entry.LocalPath.Contains(searchText, StringComparison.OrdinalIgnoreCase)
               || entry.DisplayName.Contains(searchText, StringComparison.OrdinalIgnoreCase)
               || (entry.Metadata.Description?.Contains(searchText, StringComparison.OrdinalIgnoreCase) ?? false);
    }

    private static string FormatRevision(string? revision, DateTimeOffset? date)
    {
        if (string.IsNullOrWhiteSpace(revision))
        {
            return "Not available";
        }

        var shortRevision = revision.Length > 12 ? revision[..12] : revision;
        return date is null ? shortRevision : $"{shortRevision} - {date:yyyy-MM-dd HH:mm:ss zzz}";
    }
}

public enum InventorySortColumn
{
    Name,
    Root,
    Provenance,
    Management,
    Health,
    UpdateStatus,
    Exposures,
}

public sealed class MainViewModel : INotifyPropertyChanged
{
    private static readonly IReadOnlyList<string> FilterNames =
    [
        "All Skills",
        "Updates",
        "Attention",
        "Unmanaged",
        "Healthy",
    ];

    private string _sourceText = string.Empty;
    private string _searchText = string.Empty;
    private FilterCount _selectedFilter;
    private InventoryRow? _selectedRow;
    private InventorySortColumn _sortColumn = InventorySortColumn.Name;
    private bool _sortDescending;
    private IReadOnlyList<FilterCount> _filters;
    private bool _recoveryRequired;
    private string _recoveryDiagnostic = string.Empty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public MainViewModel()
    {
        _filters = BuildFilters([]);
        _selectedFilter = Filters[0];
        InspectSourceCommand = new RelayCommand(_ => SetStatus("Source inspection is not available yet. Nothing changed."));
        Status = new StatusUpdate("Ready. Nothing changed.", DateTimeOffset.Now);
    }

    public IReadOnlyList<FilterCount> Filters
    {
        get => _filters;
        private set
        {
            if (SetProperty(ref _filters, value))
            {
                if (!value.Any(filter => filter.Name == SelectedFilter.Name))
                {
                    _selectedFilter = value[0];
                    OnPropertyChanged(nameof(SelectedFilter));
                }
            }
        }
    }

    public ObservableCollection<InventoryRow> Rows { get; } = [];

    public FilterCount SelectedFilter
    {
        get => _selectedFilter;
        set
        {
            if (value is not null && SetProperty(ref _selectedFilter, value))
            {
                ApplyView();
            }
        }
    }

    public InventoryRow? SelectedRow
    {
        get => _selectedRow;
        set
        {
            if (SetProperty(ref _selectedRow, value))
            {
                OnPropertyChanged(nameof(DetailsHeader));
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(HasNoSelection));
            }
        }
    }

    public string SourceText
    {
        get => _sourceText;
        set => SetProperty(ref _sourceText, value);
    }

    public string SearchText
    {
        get => _searchText;
        set
        {
            if (SetProperty(ref _searchText, value))
            {
                ApplyView();
            }
        }
    }

    public StatusUpdate Status { get; private set; }

    public bool RecoveryRequired => _recoveryRequired;

    public bool MutationsAllowed => !_recoveryRequired;

    public string RecoveryDiagnostic => _recoveryDiagnostic;

    public bool HasSkills => Rows.Count > 0;

    public bool HasNoSkills => Rows.Count == 0;

    public bool HasSelection => SelectedRow is not null;

    public bool HasNoSelection => SelectedRow is null;

    public string DetailsHeader => HasSelection ? "Skill details" : "Skill details — select a Skill";

    public ICommand InspectSourceCommand { get; }

    public void Announce(string message) => SetStatus(message);

    public void EnterRecoveryRequired(string diagnostic)
    {
        _recoveryRequired = true;
        _recoveryDiagnostic = diagnostic;
        OnPropertyChanged(nameof(RecoveryRequired));
        OnPropertyChanged(nameof(MutationsAllowed));
        OnPropertyChanged(nameof(RecoveryDiagnostic));
        SetStatus(diagnostic);
    }

    public void LoadInventory(InventorySnapshot snapshot)
    {
        var selectedPath = SelectedRow?.Entry.LocalPath;
        _allRows = [.. snapshot.Entries.Select(entry => new InventoryRow(entry))];
        Filters = BuildFilters(_allRows);
        ApplyView();
        if (selectedPath is not null)
        {
            SelectedRow = _allRows.FirstOrDefault(row =>
                string.Equals(row.Entry.LocalPath, selectedPath, StringComparison.OrdinalIgnoreCase));
        }
        SetStatus(
            $"Inventory refreshed: {snapshot.Entries.Count} installation(s), "
            + $"{snapshot.AttentionCount} need attention. Read-only scan; nothing changed.");
    }

    private List<InventoryRow> _allRows = [];

    public void SortBy(InventorySortColumn column)
    {
        if (_sortColumn == column)
        {
            _sortDescending = !_sortDescending;
        }
        else
        {
            _sortColumn = column;
            _sortDescending = false;
        }

        ApplyView();
    }

    private void ApplyView()
    {
        var filtered = _allRows.Where(row => MatchesFilter(row) && row.MatchesSearch(SearchText));
        var sorted = _sortDescending ? filtered.OrderByDescending(KeyFor(_sortColumn)) : filtered.OrderBy(KeyFor(_sortColumn));
        var materialized = sorted.ToList();

        var previouslySelected = SelectedRow;
        Rows.Clear();
        foreach (var row in materialized)
        {
            Rows.Add(row);
        }

        if (previouslySelected is not null && !materialized.Contains(previouslySelected))
        {
            SelectedRow = null;
        }

        OnPropertyChanged(nameof(HasSkills));
        OnPropertyChanged(nameof(HasNoSkills));
    }

    private bool MatchesFilter(InventoryRow row)
    {
        return SelectedFilter.Name switch
        {
            "Updates" => row.CanUpdate,
            "Attention" => row.Entry.NeedsAttention,
            "Unmanaged" => row.Entry.ManagementStatus == ManagementStatus.Unmanaged,
            "Healthy" => row.Entry.Health == InstallationHealth.Healthy,
            _ => true,
        };
    }

    private static Func<InventoryRow, object> KeyFor(InventorySortColumn column) => column switch
    {
        InventorySortColumn.Root => static row => row.RootLabel,
        InventorySortColumn.Provenance => static row => row.Provenance,
        InventorySortColumn.Management => static row => row.Management,
        InventorySortColumn.Health => static row => row.Health,
        InventorySortColumn.UpdateStatus => static row => row.UpdateStatus,
        InventorySortColumn.Exposures => static row => row.ExposuresSummary,
        _ => static row => row.Name,
    };

    private static IReadOnlyList<FilterCount> BuildFilters(IReadOnlyList<InventoryRow> allRows)
    {
        List<FilterCount> filters =
        [
            new("All Skills", allRows.Count),
            new("Updates", allRows.Count(static row => row.CanUpdate)),
            new("Attention", allRows.Count(static row => row.Entry.NeedsAttention)),
            new("Unmanaged", allRows.Count(static row => row.Entry.ManagementStatus == ManagementStatus.Unmanaged)),
            new("Healthy", allRows.Count(static row => row.Entry.Health == InstallationHealth.Healthy)),
        ];
        return filters;
    }

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
