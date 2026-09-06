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
        var skills = previews.SelectMany(preview => preview.Skills.Select(skill => (Skill: skill, preview.Provider))).ToList();
        var root = WorkbenchWindow.Shell(this, "REVIEW UPDATES", $"{skills.Count} Skills · {previews.Count} provider operations");
        WorkbenchWindow.Intro(root, "Select a Skill, then a file. No installed content has changed.");
        var footer = new DockPanel { Margin = new Thickness(20, 0, 20, 18) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = WorkbenchWindow.Button(this, "_Cancel");
        cancel.IsCancel = true; cancel.Margin = new Thickness(0, 0, 8, 0);
        cancel.Click += (_, _) => Close();
        var apply = WorkbenchWindow.Button(this, $"Apply reviewed updates ({skills.Count})", "PrimaryButton");
        apply.IsEnabled = previews.Count > 0 && previews.All(preview => preview.CanApply);
        AutomationProperties.SetAutomationId(apply, "Skilly.ApplyReviewedUpdates");
        apply.Click += (_, _) => DialogResult = true;
        buttons.Children.Add(cancel); buttons.Children.Add(apply);
        DockPanel.SetDock(buttons, Dock.Right); footer.Children.Add(buttons);
        var blocker = WorkbenchWindow.Text(string.Join("\n", previews.Where(preview => preview.Blocker is not null).Select(preview => preview.Blocker)), 12, "Accent800Brush");
        blocker.Margin = new Thickness(0, 0, 16, 0); blocker.VerticalAlignment = VerticalAlignment.Center;
        footer.Children.Add(blocker);

        var grid = new Grid { Margin = new Thickness(20, 14, 20, 14) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(300), MinWidth = 200 });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(16) });
        grid.ColumnDefinitions.Add(new ColumnDefinition());
        var left = new DockPanel();
        var heading = WorkbenchWindow.Kicker("AFFECTED SKILLS"); heading.Margin = new Thickness(14, 12, 14, 10);
        DockPanel.SetDock(heading, Dock.Top); left.Children.Add(heading);
        var skillList = WorkbenchWindow.List(this);
        AutomationProperties.SetAutomationId(skillList, "Skilly.PreviewSkills");
        foreach (var (skill, provider) in skills)
        {
            var row = new StackPanel();
            var nameLine = new DockPanel();
            var tag = WorkbenchWindow.Tag(provider); DockPanel.SetDock(tag, Dock.Right); nameLine.Children.Add(tag);
            var name = WorkbenchWindow.Text(skill.Name, 13); name.FontWeight = FontWeights.SemiBold;
            name.Margin = new Thickness(0, 0, 6, 0); nameLine.Children.Add(name); row.Children.Add(nameLine);
            var revision = WorkbenchWindow.Text($"{Short(skill.InstalledRevision)} → {Short(skill.TargetRevision)}", 10.5, "Text55Brush", mono: true);
            revision.Margin = new Thickness(0, 5, 0, 3); row.Children.Add(revision);
            row.Children.Add(WorkbenchWindow.Text($"{skill.Files.Count} changed file(s)", 11.5, "Text60Brush"));
            skillList.Items.Add(new ListBoxItem { Content = row, Tag = skill });
        }
        left.Children.Add(skillList); grid.Children.Add(WorkbenchWindow.Panel(left));
        var right = new DockPanel();
        var pathHeading = new StackPanel { Margin = new Thickness(14, 12, 14, 10) };
        pathHeading.Children.Add(WorkbenchWindow.Kicker("LOCAL PATH"));
        var path = WorkbenchWindow.Text("", 11, "Text55Brush", mono: true); path.Margin = new Thickness(0, 4, 0, 0);
        pathHeading.Children.Add(path); DockPanel.SetDock(pathHeading, Dock.Top); right.Children.Add(pathHeading);
        var filesGrid = new Grid();
        filesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230), MinWidth = 100 });
        filesGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(5) });
        filesGrid.ColumnDefinitions.Add(new ColumnDefinition { MinWidth = 100 });
        bool? compactLayout = null;
        grid.SizeChanged += (_, args) =>
        {
            var compact = args.NewSize.Width < 810;
            if (compactLayout == compact) return;
            compactLayout = compact;
            grid.ColumnDefinitions[0].Width = new GridLength(compact ? 220 : 300);
            filesGrid.ColumnDefinitions[0].Width = new GridLength(compact ? 125 : 230);
        };
        var files = WorkbenchWindow.List(this);
        AutomationProperties.SetAutomationId(files, "Skilly.PreviewFiles");
        filesGrid.Children.Add(files);
        var splitter = new GridSplitter { Width = 5, HorizontalAlignment = HorizontalAlignment.Stretch };
        splitter.SetResourceReference(BackgroundProperty, "DividerBrush");
        Grid.SetColumn(splitter, 1); filesGrid.Children.Add(splitter);
        var tabs = new TabControl { Style = (Style)FindResource("IndustryTabs") };
        Grid.SetColumn(tabs, 2); filesGrid.Children.Add(tabs);
        var diff = WorkbenchWindow.Document(this); var before = WorkbenchWindow.Document(this); var after = WorkbenchWindow.Document(this);
        AutomationProperties.SetAutomationId(diff, "Skilly.PreviewDiff");
        tabs.Items.Add(new TabItem { Header = "CHANGES", Content = diff });
        tabs.Items.Add(new TabItem { Header = "INSTALLED", Content = before });
        tabs.Items.Add(new TabItem { Header = "AVAILABLE", Content = after });
        skillList.SelectionChanged += (_, _) =>
        {
            if (skillList.SelectedItem is not ListBoxItem { Tag: SkillUpdatePreview skill }) return;
            path.Text = skill.LocalPath;
            files.Items.Clear();
            foreach (var file in skill.Files)
            {
                var row = new StackPanel();
                var tag = WorkbenchWindow.Tag(file.Change, file.Change switch { "Added" => "tinted", "Removed" => "dark", _ => "neutral" });
                tag.HorizontalAlignment = HorizontalAlignment.Left; row.Children.Add(tag);
                var filePath = WorkbenchWindow.Text(file.Path, 11, mono: true); filePath.Margin = new Thickness(0, 4, 0, 0);
                row.Children.Add(filePath); files.Items.Add(new ListBoxItem { Content = row, Tag = file });
            }
            diff.Text = skill.Files.Count == 0 ? "No file content changes. The source revision or provider metadata changes." : "Select a file.";
            before.Text = after.Text = string.Empty;
            if (files.Items.Count > 0) files.SelectedIndex = 0;
        };
        files.SelectionChanged += (_, _) =>
        {
            if (files.SelectedItem is not ListBoxItem { Tag: FileChange file }) return;
            diff.Text = file.Diff; before.Text = file.BeforeText; after.Text = file.AfterText;
        };
        right.Children.Add(filesGrid);
        var rightPanel = WorkbenchWindow.Panel(right); Grid.SetColumn(rightPanel, 2); grid.Children.Add(rightPanel);
        root.Children.Add(grid);
        if (skills.Count > 0) skillList.SelectedIndex = 0;
    }

    private static string Short(string revision) => revision.Length > 12 ? revision[..12] : revision;
}
