using System.Windows;
using System.Windows.Controls;
using System.Windows.Automation;
using Skilly.Providers;

namespace Skilly;

public sealed class UpdatePreviewWindow : Window
{
    public UpdatePreviewWindow(IReadOnlyList<UpdatePreview> previews)
    {
        Title = "Review updates";
        Width = 1000; Height = 720; MinWidth = 720; MinHeight = 520;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        var root = new DockPanel { Margin = new Thickness(18) };
        var skills = previews.SelectMany(preview => preview.Skills).ToList();
        var summary = new TextBlock
        {
            Text = $"Review {skills.Count} Skill(s), {previews.Count} provider operation(s). Select a Skill, then a file. No installed content has changed.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12), FontSize = 15,
        };
        DockPanel.SetDock(summary, Dock.Top); root.Children.Add(summary);
        var footer = new StackPanel { Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var blocker = string.Join("\n", previews.Where(preview => preview.Blocker is not null).Select(preview => preview.Blocker));
        if (blocker.Length > 0) footer.Children.Add(new TextBlock { Text = blocker, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 8) });
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = new Button { Content = "Cancel", IsCancel = true, Padding = new Thickness(18, 7, 18, 7), Margin = new Thickness(0, 0, 8, 0) };
        cancel.Click += (_, _) => { DialogResult = false; };
        var apply = new Button { Content = $"Apply reviewed updates ({skills.Count})", IsEnabled = previews.Count > 0 && previews.All(preview => preview.CanApply), Padding = new Thickness(18, 7, 18, 7) };
        AutomationProperties.SetAutomationId(apply, "Skilly.ApplyReviewedUpdates");
        apply.Click += (_, _) => { DialogResult = true; };
        buttons.Children.Add(cancel); buttons.Children.Add(apply); footer.Children.Add(buttons);
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(100) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        var skillList = new ListBox { ItemsSource = skills, DisplayMemberPath = nameof(SkillUpdatePreview.Summary) };
        AutomationProperties.SetAutomationId(skillList, "Skilly.PreviewSkills");
        grid.Children.Add(skillList);
        var path = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 8) };
        Grid.SetRow(path, 1); grid.Children.Add(path);
        var filesGrid = new Grid(); Grid.SetRow(filesGrid, 2); grid.Children.Add(filesGrid);
        filesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(240), MinWidth = 120 });
        filesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(6) });
        filesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var files = new ListBox();
        AutomationProperties.SetAutomationId(files, "Skilly.PreviewFiles");
        filesGrid.Children.Add(files);
        var splitter = new GridSplitter { Width = 6, HorizontalAlignment = HorizontalAlignment.Stretch };
        Grid.SetColumn(splitter, 1); filesGrid.Children.Add(splitter);
        var tabs = new TabControl(); Grid.SetColumn(tabs, 2); filesGrid.Children.Add(tabs);
        var diff = ReadOnlyText(); var before = ReadOnlyText(); var after = ReadOnlyText();
        AutomationProperties.SetAutomationId(diff, "Skilly.PreviewDiff");
        tabs.Items.Add(new TabItem { Header = "Changes", Content = diff });
        tabs.Items.Add(new TabItem { Header = "Installed", Content = before });
        tabs.Items.Add(new TabItem { Header = "Available", Content = after });
        skillList.SelectionChanged += (_, _) =>
        {
            if (skillList.SelectedItem is not SkillUpdatePreview skill) return;
            path.Text = skill.LocalPath;
            files.ItemsSource = skill.Files.Select(file => new ListBoxItem { Content = $"{file.Change}: {file.Path}", Tag = file }).ToList();
            diff.Text = skill.Files.Count == 0 ? "No file content changes. The source revision or provider metadata changes." : "Select a file.";
            before.Text = after.Text = string.Empty;
            if (files.Items.Count > 0) files.SelectedIndex = 0;
        };
        files.SelectionChanged += (_, _) =>
        {
            if (files.SelectedItem is not ListBoxItem { Tag: FileChange file }) return;
            diff.Text = file.Diff; before.Text = file.BeforeText; after.Text = file.AfterText;
        };
        root.Children.Add(grid); Content = root;
        if (skills.Count > 0) skillList.SelectedIndex = 0;
    }

    private static TextBox ReadOnlyText() => new()
    {
        IsReadOnly = true, TextWrapping = TextWrapping.NoWrap, VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, FontFamily = new System.Windows.Media.FontFamily("Consolas"),
        FontSize = 12, Padding = new Thickness(8),
    };
}
