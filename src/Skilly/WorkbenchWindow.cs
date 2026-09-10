using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using System.Windows.Shell;

namespace Skilly;

/// <summary>Shared Industry shell for code-built review and document windows.</summary>
internal static class WorkbenchWindow
{
    public static DockPanel Shell(Window window, string kicker, string title, string? path = null)
    {
        window.SetResourceReference(Window.IconProperty, "BrandIcon");
        window.SetResourceReference(Window.BackgroundProperty, "BgBrush");
        window.SetResourceReference(Window.ForegroundProperty, "TextBrush");
        window.SetResourceReference(Window.FontFamilyProperty, "BodyFont");
        window.FontSize = 13.5;
        window.UseLayoutRounding = true;
        window.SnapsToDevicePixels = true;
        TextOptions.SetTextFormattingMode(window, TextFormattingMode.Display);
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        window.Width = Math.Min(window.Width, SystemParameters.WorkArea.Width);
        window.Height = Math.Min(window.Height, SystemParameters.WorkArea.Height);
        WindowChrome.SetWindowChrome(window, new WindowChrome
        {
            CaptionHeight = 64, CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0, 0, 0, 1), ResizeBorderThickness = new Thickness(6),
            UseAeroCaptionButtons = false,
        });
        var root = new DockPanel();
        window.StateChanged += (_, _) => root.Margin = window.WindowState == WindowState.Maximized ? new Thickness(7) : default;
        var header = new Border { Padding = new Thickness(20, 14, 20, 16) };
        header.SetResourceReference(Border.BackgroundProperty, "Accent900Brush");
        var headerGrid = new Grid();
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition());
        headerGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var headings = new StackPanel { Margin = new Thickness(0, 0, 12, 0) };
        var label = Text(kicker, 13, "Paper70Brush");
        label.SetResourceReference(TextBlock.FontFamilyProperty, "HeadingFont");
        label.FontWeight = FontWeights.SemiBold;
        headings.Children.Add(label);
        var name = Text(title, 24, "BgBrush");
        name.SetResourceReference(TextBlock.FontFamilyProperty, "HeadingFont");
        name.FontWeight = FontWeights.SemiBold;
        name.Margin = new Thickness(0, 2, 0, 0);
        headings.Children.Add(name);
        if (path is not null)
        {
            var location = Text(path, 10.5, "Paper60Brush", mono: true);
            location.TextWrapping = TextWrapping.NoWrap;
            location.TextTrimming = TextTrimming.CharacterEllipsis;
            location.ToolTip = path;
            location.Margin = new Thickness(0, 5, 0, 0);
            headings.Children.Add(location);
        }
        headerGrid.Children.Add(headings);
        var close = Button(window, "", "SteelCloseButton");
        close.VerticalAlignment = VerticalAlignment.Top;
        var icon = new Path
        {
            Data = Geometry.Parse("M18,6 L6,18 M6,6 L18,18"), StrokeThickness = 1.5,
            StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round,
            Stretch = Stretch.Uniform, Width = 12, Height = 12,
        };
        icon.SetResourceReference(Shape.StrokeProperty, "BgBrush");
        close.Content = icon;
        close.Click += (_, _) => window.Close();
        AutomationProperties.SetName(close, "Close");
        WindowChrome.SetIsHitTestVisibleInChrome(close, true);
        Grid.SetColumn(close, 1); headerGrid.Children.Add(close);
        header.Child = headerGrid;
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        window.Content = root;
        return root;
    }

    public static TextBlock Text(string text, double size = 12.5, string brush = "TextBrush", bool mono = false)
    {
        var block = new TextBlock { Text = text, FontSize = size, TextWrapping = TextWrapping.Wrap };
        block.SetResourceReference(TextBlock.ForegroundProperty, brush);
        if (mono) block.SetResourceReference(TextBlock.FontFamilyProperty, "MonoFont");
        return block;
    }

    public static TextBlock Kicker(string text)
    {
        var block = new TextBlock { Text = text };
        block.SetResourceReference(FrameworkElement.StyleProperty, "SectionKicker");
        return block;
    }

    public static ContentControl Panel(UIElement content, Thickness? padding = null)
    {
        var panel = new ContentControl { Content = content, Padding = padding ?? default };
        panel.SetResourceReference(FrameworkElement.StyleProperty, "BlueprintPanel");
        return panel;
    }

    public static Button Button(Window window, string label, string style = "SecondaryButton")
        => new() { Content = label, Style = (Style)window.FindResource(style) };

    public static TextBox Document(Window window)
        => new() { Style = (Style)window.FindResource("ReadOnlyDocument") };

    public static ListBox List(Window window)
        => new()
        {
            Background = Brushes.Transparent, BorderThickness = new Thickness(0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            ItemContainerStyle = (Style)window.FindResource("SkillRowContainer"),
        };

    public static Border Tag(string text, string kind = "neutral")
    {
        var tag = new Border { CornerRadius = new CornerRadius(3), Padding = new Thickness(7, 2, 7, 2), BorderThickness = new Thickness(1), VerticalAlignment = VerticalAlignment.Center };
        tag.SetResourceReference(Border.BackgroundProperty, kind switch { "dark" => "Accent900Brush", "tinted" => "Accent100Brush", "outline" => "BgBrush", _ => "Neutral100Brush" });
        tag.BorderBrush = Brushes.Transparent;
        if (kind == "outline") tag.SetResourceReference(Border.BorderBrushProperty, "Accent700Brush");
        tag.Child = Text(text, 10.5, kind == "dark" ? "BgBrush" : kind is "outline" or "tinted" ? "Accent800Brush" : "Neutral800Brush");
        return tag;
    }

    public static void Intro(DockPanel root, string text)
    {
        var intro = Text(text, 12.5, "Text75Brush");
        intro.Margin = new Thickness(20, 12, 20, 0);
        DockPanel.SetDock(intro, Dock.Top); root.Children.Add(intro);
    }
}
