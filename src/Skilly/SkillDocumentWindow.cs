using System.Windows;
using System.Windows.Controls;

namespace Skilly;

/// <summary>Shows Markdown as text, without executing embedded HTML or links.</summary>
public sealed class SkillDocumentWindow : Window
{
    public SkillDocumentWindow(string path, string text)
    {
        Title = "SKILL.md - " + path;
        Width = 820; Height = 650; MinWidth = 420; MinHeight = 320;
        var name = System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(path));
        var root = WorkbenchWindow.Shell(this, "SKILL.MD · READ ONLY", name ?? "SKILL.md", path);
        WorkbenchWindow.Intro(root, "Markdown is shown as text. Embedded HTML and links are not executed.");
        var close = WorkbenchWindow.Button(this, "_Close");
        close.IsCancel = true; close.HorizontalAlignment = HorizontalAlignment.Right;
        close.MinWidth = 90; close.Margin = new Thickness(20, 0, 20, 18);
        close.Click += (_, _) => Close(); DockPanel.SetDock(close, Dock.Bottom); root.Children.Add(close);
        var content = WorkbenchWindow.Document(this);
        content.Text = text; content.TextWrapping = TextWrapping.Wrap; content.FontSize = 12;
        TextBlock.SetLineHeight(content, 19.2);
        System.Windows.Automation.AutomationProperties.SetName(content, "SKILL.md content, read only");
        var panel = WorkbenchWindow.Panel(content); panel.Margin = new Thickness(20, 14, 20, 14);
        root.Children.Add(panel);
    }
}
