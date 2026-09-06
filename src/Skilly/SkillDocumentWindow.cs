using System.Windows;
using System.Windows.Controls;

namespace Skilly;

/// <summary>Shows Markdown as text, without executing embedded HTML or links.</summary>
public sealed class SkillDocumentWindow : Window
{
    public SkillDocumentWindow(string path, string text)
    {
        Title = "SKILL.md - " + path;
        Width = 820;
        Height = 650;
        MinWidth = 420;
        MinHeight = 320;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var panel = new DockPanel { Margin = new Thickness(16) };
        var close = new Button { Content = "_Close", IsCancel = true, HorizontalAlignment = HorizontalAlignment.Right, Padding = new Thickness(16, 6, 16, 6) };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        panel.Children.Add(close);
        var content = new TextBox
        {
            Text = text, IsReadOnly = true, TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            Margin = new Thickness(0, 0, 0, 12),
        };
        System.Windows.Automation.AutomationProperties.SetName(content, "SKILL.md content, read only");
        panel.Children.Add(content);
        Content = panel;
    }
}
