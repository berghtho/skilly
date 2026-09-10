using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;

namespace Skilly;

public sealed record SkillSetChoice(string Id, string Name, string Description, string Detail, string? Blocker, bool Selected);

public sealed class SkillSetWindow : Window
{
    private readonly List<(SkillSetChoice Choice, CheckBox Check)> _choices = [];
    private readonly TextBox _name;
    private readonly TextBlock _summary;
    private readonly Button _apply;
    private readonly bool _importing;
    public string SetName => _name.Text.Trim();
    public IReadOnlyList<string> SelectedIds => _choices.Where(row => row.Check.IsChecked == true && row.Choice.Blocker is null).Select(row => row.Choice.Id).ToList();

    public SkillSetWindow(string name, IReadOnlyList<SkillSetChoice> choices, bool importing)
    {
        _importing = importing;
        Title = importing ? "Import Skill set" : "Export Skill set";
        Width = 800; Height = 660; MinWidth = 580; MinHeight = 430;
        var root = WorkbenchWindow.Shell(this, importing ? "IMPORT SKILL SET" : "EXPORT SKILL SET", importing ? name : "Share your Skills");
        WorkbenchWindow.Intro(root, importing
            ? "Review the Skills below. Existing names are skipped. Imported Skills are Unmanaged and available to all four Harnesses. Source updates require verified Adoption. Only import files you trust."
            : "Share complete Skill folders, including scripts and assets. All files inside the selected folders are included. Check for private files before sharing. Account settings and management records are not included.");
        var footer = new StackPanel { Margin = new Thickness(20, 10, 20, 18) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        _summary = WorkbenchWindow.Text("");
        AutomationProperties.SetAutomationId(_summary, "Skilly.SetSummary");
        footer.Children.Add(_summary);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 10, 0, 0) };
        var cancel = WorkbenchWindow.Button(this, "_Cancel"); cancel.IsCancel = true;
        cancel.Click += (_, _) => Close();
        _apply = WorkbenchWindow.Button(this, importing ? "_Import selected" : "_Save set…", "PrimaryButton");
        _apply.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetAutomationId(_apply, "Skilly.ApplySet");
        _apply.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(_apply); footer.Children.Add(buttons);
        var controls = new StackPanel { Margin = new Thickness(20, 14, 20, 8) };
        DockPanel.SetDock(controls, Dock.Top); root.Children.Add(controls);
        _name = new TextBox { Text = name, MaxLength = 120, Style = (Style)FindResource("IndustryTextBox") };
        AutomationProperties.SetAutomationId(_name, "Skilly.SetName"); AutomationProperties.SetName(_name, "Skill set name");
        if (!importing)
        {
            var label = new Label { Content = "Set _name", Target = _name, Padding = new Thickness(0, 0, 0, 5) };
            controls.Children.Add(label); controls.Children.Add(_name);
        }
        _name.TextChanged += (_, _) => UpdateSelection();
        var selection = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 8, 0, 0) };
        var all = WorkbenchWindow.Button(this, "Select _all"); var none = WorkbenchWindow.Button(this, "Select _none");
        none.Margin = new Thickness(8, 0, 0, 0);
        AutomationProperties.SetAutomationId(all, "Skilly.SetSelectAll"); AutomationProperties.SetAutomationId(none, "Skilly.SetSelectNone");
        all.Click += (_, _) => { foreach (var row in _choices) row.Check.IsChecked = row.Choice.Blocker is null; };
        none.Click += (_, _) => { foreach (var row in _choices) row.Check.IsChecked = false; };
        selection.Children.Add(all); selection.Children.Add(none); controls.Children.Add(selection);
        var list = new StackPanel();
        foreach (var choice in choices)
        {
            var content = new StackPanel();
            var title = WorkbenchWindow.Text(choice.Name, 14); title.FontWeight = FontWeights.SemiBold; content.Children.Add(title);
            content.Children.Add(WorkbenchWindow.Text(choice.Description, 12, "Text75Brush"));
            content.Children.Add(WorkbenchWindow.Text(choice.Detail, 10.5, "Text55Brush", mono: true));
            if (choice.Blocker is not null) content.Children.Add(WorkbenchWindow.Text("Skipped: " + choice.Blocker, 12, "Accent800Brush"));
            var check = new CheckBox
            {
                IsEnabled = choice.Blocker is null, IsChecked = choice.Selected && choice.Blocker is null,
                Style = (Style)FindResource("IndustryCheckBox"), VerticalAlignment = VerticalAlignment.Top,
                Margin = new Thickness(0, 3, 10, 0), Width = 14,
            };
            AutomationProperties.SetName(check, choice.Name);
            AutomationProperties.SetAutomationId(check, "Skilly.SetSkill." + choice.Id);
            _choices.Add((choice, check));
            check.Checked += (_, _) => UpdateSelection(); check.Unchecked += (_, _) => UpdateSelection();
            var row = new Grid { Margin = new Thickness(12, 9, 12, 9) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(28) });
            row.ColumnDefinitions.Add(new ColumnDefinition());
            row.Children.Add(check); Grid.SetColumn(content, 1); row.Children.Add(content);
            content.MouseLeftButtonUp += (_, _) => { if (check.IsEnabled) check.IsChecked = check.IsChecked != true; };
            list.Children.Add(row);
        }
        var scroll = new ScrollViewer { Content = list, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
        var panel = WorkbenchWindow.Panel(scroll); panel.Margin = new Thickness(20, 0, 20, 0); root.Children.Add(panel);
        UpdateSelection();
    }

    private void UpdateSelection()
    {
        var count = SelectedIds.Count;
        var skipped = _choices.Count(row => row.Choice.Blocker is not null);
        var duplicate = !_importing && _choices.Where(row => row.Check.IsChecked == true)
            .GroupBy(row => row.Choice.Name, StringComparer.OrdinalIgnoreCase).Any(group => group.Count() > 1);
        _summary.Text = $"{count} of {_choices.Count} Skills selected" + (skipped > 0 ? $" · {skipped} skipped" : "");
        if (duplicate) _summary.Text += " · Select only one installation per folder name.";
        _apply.IsEnabled = count > 0 && SetName.Length > 0 && !SetName.Any(char.IsControl) && !duplicate;
        _apply.Content = _importing ? $"_Import selected ({count})" : $"_Save set ({count})…";
    }
}
