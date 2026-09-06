using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Skilly.Infrastructure;

namespace Skilly;

public sealed class OperationHistoryWindow : Window
{
    public OperationHistoryWindow(ObservableCollection<OperationEntry> history, string? notice)
    {
        Title = "Operation history"; Width = 1000; Height = 650; MinWidth = 650; MinHeight = 420;
        Width = Math.Min(Width, SystemParameters.WorkArea.Width);
        Height = Math.Min(Height, SystemParameters.WorkArea.Height);
        SetResourceReference(BackgroundProperty, "BgBrush");
        SetResourceReference(ForegroundProperty, "TextBrush");
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(16) };
        var heading = new TextBlock { Text = notice ?? "Latest update results. Interrupted operations are never retried automatically.", TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 12) };
        DockPanel.SetDock(heading, Dock.Top); root.Children.Add(heading);
        var detail = new TextBox { IsReadOnly = true, TextWrapping = TextWrapping.Wrap, Height = 135, VerticalScrollBarVisibility = ScrollBarVisibility.Auto, Margin = new Thickness(0, 12, 0, 0) };
        DockPanel.SetDock(detail, Dock.Bottom); root.Children.Add(detail);
        var list = new DataGrid { ItemsSource = history, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false, SelectionMode = DataGridSelectionMode.Single };
        foreach (var (header, property, width) in new[] { ("Started", "StartedAt", 165), ("Operation", "Operation", 170), ("Skill", "Skill", 200), ("Result", "Status", 110) })
            list.Columns.Add(new DataGridTextColumn { Header = header, Binding = new Binding(property), Width = width });
        list.SelectionChanged += (_, _) => { if (list.SelectedItem is OperationEntry row) detail.Text = $"{row.Path}\n\n{row.Detail}"; };
        System.Windows.Automation.AutomationProperties.SetAutomationId(list, "Skilly.OperationHistory");
        root.Children.Add(list); Content = root;
    }
}
