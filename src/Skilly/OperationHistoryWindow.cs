using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Media;
using Skilly.Infrastructure;

namespace Skilly;

public sealed class OperationHistoryWindow : Window
{
    public OperationHistoryWindow(ObservableCollection<OperationEntry> history, string? notice)
    {
        Title = "Operation history"; Width = 1000; Height = 650; MinWidth = 650; MinHeight = 420;
        var root = WorkbenchWindow.Shell(this, "OPERATION HISTORY", notice ?? "Latest update results");
        WorkbenchWindow.Intro(root, "Interrupted operations are never retried automatically. Up to 1,000 per-Skill results are kept locally within 4 MB.");
        var detailBody = new DockPanel();
        var path = WorkbenchWindow.Text("", 11, "Text55Brush", mono: true);
        path.Margin = new Thickness(12, 12, 12, 0); DockPanel.SetDock(path, Dock.Top); detailBody.Children.Add(path);
        var detail = WorkbenchWindow.Document(this);
        detail.SetResourceReference(FontFamilyProperty, "BodyFont"); detail.FontSize = 12.5;
        detail.TextWrapping = TextWrapping.Wrap; detailBody.Children.Add(detail);
        var detailPanel = WorkbenchWindow.Panel(detailBody); detailPanel.Height = 135;
        detailPanel.Margin = new Thickness(20, 0, 20, 18);
        DockPanel.SetDock(detailPanel, Dock.Bottom); root.Children.Add(detailPanel);
        var list = new DataGrid
        {
            ItemsSource = history, AutoGenerateColumns = false, IsReadOnly = true, CanUserAddRows = false,
            SelectionMode = DataGridSelectionMode.Single, SelectionUnit = DataGridSelectionUnit.FullRow,
            Background = Brushes.Transparent, BorderThickness = new Thickness(0), HeadersVisibility = DataGridHeadersVisibility.Column,
            GridLinesVisibility = DataGridGridLinesVisibility.Horizontal, RowBackground = Brushes.Transparent,
        };
        list.SetResourceReference(DataGrid.HorizontalGridLinesBrushProperty, "Text08Brush");
        var headerStyle = new Style(typeof(DataGridColumnHeader));
        headerStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        headerStyle.Setters.Add(new Setter(ForegroundProperty, FindResource("Text60Brush")));
        headerStyle.Setters.Add(new Setter(FontFamilyProperty, FindResource("HeadingFont")));
        headerStyle.Setters.Add(new Setter(FontSizeProperty, 12.5));
        headerStyle.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
        headerStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(14, 8, 14, 8)));
        headerStyle.Setters.Add(new Setter(BorderBrushProperty, FindResource("DividerBrush")));
        headerStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0, 0, 0, 1)));
        list.ColumnHeaderStyle = headerStyle;
        var cellStyle = new Style(typeof(DataGridCell));
        cellStyle.Setters.Add(new Setter(BorderThicknessProperty, new Thickness(0)));
        cellStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        cellStyle.Setters.Add(new Setter(ForegroundProperty, FindResource("TextBrush")));
        cellStyle.Setters.Add(new Setter(PaddingProperty, new Thickness(0)));
        list.CellStyle = cellStyle;
        var rowStyle = new Style(typeof(DataGridRow));
        rowStyle.Setters.Add(new Setter(BackgroundProperty, Brushes.Transparent));
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(BackgroundProperty, FindResource("AccentTint08Brush"))); rowStyle.Triggers.Add(hover);
        var selected = new Trigger { Property = DataGridRow.IsSelectedProperty, Value = true };
        selected.Setters.Add(new Setter(BackgroundProperty, FindResource("Accent100Brush"))); rowStyle.Triggers.Add(selected);
        list.RowStyle = rowStyle;
        foreach (var (header, property, width) in new[] { ("STARTED", "StartedAt", 170), ("OPERATION", "Operation", 170), ("SKILL", "Skill", 0) })
        {
            var textStyle = new Style(typeof(TextBlock));
            textStyle.Setters.Add(new Setter(MarginProperty, new Thickness(14, 8, 14, 8)));
            textStyle.Setters.Add(new Setter(TextBlock.TextTrimmingProperty, TextTrimming.CharacterEllipsis));
            textStyle.Setters.Add(new Setter(FontSizeProperty, property == "StartedAt" ? 11.5 : 12.5));
            if (property == "StartedAt") textStyle.Setters.Add(new Setter(FontFamilyProperty, FindResource("MonoFont")));
            if (property == "Skill") textStyle.Setters.Add(new Setter(FontWeightProperty, FontWeights.SemiBold));
            list.Columns.Add(new DataGridTextColumn
            {
                Header = header, Binding = new Binding(property) { StringFormat = property == "StartedAt" ? "yyyy-MM-dd HH:mm:ss" : null },
                Width = width == 0 ? new DataGridLength(1, DataGridLengthUnitType.Star) : new DataGridLength(width),
                MinWidth = 100, ElementStyle = textStyle,
            });
        }
        list.Columns.Add(new DataGridTemplateColumn { Header = "RESULT", Width = 130, SortMemberPath = "Status", CellTemplate = (DataTemplate)FindResource("HistoryResultTag") });
        list.SelectionChanged += (_, _) =>
        {
            if (list.SelectedItem is OperationEntry row) { path.Text = row.Path; detail.Text = row.Detail; }
        };
        System.Windows.Automation.AutomationProperties.SetAutomationId(list, "Skilly.OperationHistory");
        var panel = WorkbenchWindow.Panel(list); panel.Margin = new Thickness(20, 14, 20, 14);
        root.Children.Add(panel);
    }
}
