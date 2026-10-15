using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace EzView;

// Shared look: dark surfaces, orange accent, uppercase section headers, line icons.
static class Ui
{
    public static readonly IBrush Bg = Brush.Parse("#0f0f0f");
    public static readonly IBrush Surface = Brush.Parse("#141414");
    // Side panels and menus: solid, like the sidebar in the reference design.
    public static readonly IBrush Panel = Brush.Parse("#121212");
    // Top bar and player bar: light tint over the real blur drawn by GlBlur / ImageView.
    public static readonly IBrush Glass = Brush.Parse("#cc0c0c0c");
    public static readonly IBrush Edge = Brush.Parse("#22ffffff");
    public static readonly IBrush Hover = Brush.Parse("#12ffffff");
    public static readonly IBrush Text = Brush.Parse("#e6e6e6");
    public static readonly IBrush Muted = Brush.Parse("#7a7a7a");
    public static readonly IBrush Accent = Brush.Parse("#ff5b2e");
    public const string FontUri = "avares://ezview/Assets/Fonts#Nunito Sans";
    public static readonly FontFamily Font = new(FontUri);
    public static readonly FontFamily Icons = new("Segoe Fluent Icons, Segoe MDL2 Assets");

    public static TextBlock Header(string text) => new()
    {
        Text = text.ToUpperInvariant(), FontSize = 11, LetterSpacing = 1.2, Foreground = Muted, Margin = new(20, 20, 20, 8),
    };

    public static TextBlock Icon(string glyph, double size = 15) => new()
    {
        Text = glyph, FontFamily = Icons, FontSize = size, VerticalAlignment = VerticalAlignment.Center,
    };

    // A floating card along one edge; MainWindow sets its margin so it sits between the bars.
    public const double Gap = 12;

    public static Border SidePanel(bool left, Control content)
    {
        var close = new Button
        {
            Content = "", FontFamily = Icons, FontSize = 10, Width = 28, Height = 28, Padding = new(0),
            CornerRadius = new(14), Background = Brushes.Transparent, Foreground = Muted, BorderThickness = new(0),
            Focusable = false, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            Margin = new(0, 12, 12, 0),
        };
        var panel = new Border
        {
            Width = 290, Background = Panel, BorderBrush = Edge, BorderThickness = new(1), CornerRadius = new(14),
            ClipToBounds = true,
            BoxShadow = BoxShadows.Parse("0 8 32 0 #80000000"),
            HorizontalAlignment = left ? HorizontalAlignment.Left : HorizontalAlignment.Right,
            IsVisible = false, Child = new Avalonia.Controls.Panel { Children = { content, close } },
        };
        close.Click += (_, _) => panel.IsVisible = false;
        return panel;
    }
}
