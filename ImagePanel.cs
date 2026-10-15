using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace EzView;

// Right side panel for inspecting images: channel view, exposure, gamma and a pixel readout.
class ImagePanel
{
    public readonly Border View;
    readonly ImageView _image;
    readonly List<TextBlock> _channels = [];
    readonly Slider _exposure = Ui.Slider(-6, 6, 0);
    readonly Slider _gamma = Ui.Slider(0.2, 3, 1);
    readonly TextBlock _exposureValue = Value(), _gammaValue = Value();
    readonly TextBlock _pos = Value(), _rgba = Value(), _hex = Value();
    readonly Rectangle _swatch = new() { Width = 46, Height = 46, RadiusX = 8, RadiusY = 8, Stroke = Ui.Edge, StrokeThickness = 1, Fill = Brushes.Transparent };
    readonly TextBlock _lockIcon = Ui.Icon("\uE72E", 13);
    TextBlock _loupeChip = null!, _unlock = null!;
    (int X, int Y, byte R, byte G, byte B, byte A)? _color, _locked;
    bool _updating;

    static TextBlock Value() => new() { Foreground = Ui.Text, FontSize = 12.5, VerticalAlignment = VerticalAlignment.Center };

    public ImagePanel(ImageView image)
    {
        _image = image;
        var body = new StackPanel { Margin = new(0, 0, 0, 20) };

        body.Children.Add(Ui.Header("Channel"));
        var channels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, Margin = new(20, 0) };
        string[] names = ["RGB", "R", "G", "B", "A", "L"];
        for (int i = 0; i < names.Length; i++)
        {
            int index = i;
            var chip = new TextBlock
            {
                Text = names[i], FontSize = 12.5, FontWeight = FontWeight.SemiBold, Padding = new(10, 5),
                Cursor = new Cursor(StandardCursorType.Hand), Background = Brushes.Transparent,
            };
            ToolTip.SetTip(chip, i switch { 0 => "Color", 1 => "Red", 2 => "Green", 3 => "Blue", 4 => "Alpha", _ => "Luminance" });
            chip.PointerPressed += (_, e) => { _image.SetAdjust(index, _image.Exposure, _image.Gamma); Sync(); e.Handled = true; };
            _channels.Add(chip);
            channels.Children.Add(new Border { CornerRadius = new(6), Child = chip });
        }
        body.Children.Add(channels);

        body.Children.Add(Ui.Header("Exposure"));
        body.Children.Add(Row(_exposure, _exposureValue));
        body.Children.Add(Ui.Header("Gamma"));
        body.Children.Add(Row(_gamma, _gammaValue));
        _exposure.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) Changed(); };
        _gamma.PropertyChanged += (_, e) => { if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty) Changed(); };

        var reset = new TextBlock
        {
            Text = "Reset", Foreground = Ui.Accent, FontSize = 13, FontWeight = FontWeight.SemiBold,
            Margin = new(20, 14, 20, 0), Cursor = new Cursor(StandardCursorType.Hand),
        };
        reset.PointerPressed += (_, e) => { _image.SetAdjust(0, 0, 1); Sync(); e.Handled = true; };
        body.Children.Add(reset);

        body.Children.Add(Ui.Header("Pixel"));
        // Loupe on/off, then swatch + values (hovered, or the locked pick), then copy buttons.
        var loupe = Chip("Loupe", () => { _image.Loupe = !_image.Loupe; SyncChips(); });
        _loupeChip = loupe;
        var unlock = Chip("Unlock", () => Lock(null));
        _unlock = unlock;
        body.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Margin = new(20, 0, 20, 10), Children = { loupe, unlock } });

        var lines = new StackPanel { Margin = new(14, 0, 0, 0), Spacing = 2, VerticalAlignment = VerticalAlignment.Center, Children = { _pos, _rgba, _hex } };
        _lockIcon.Margin = new(0, 0, 0, 0);
        _lockIcon.Foreground = Ui.Accent;
        _lockIcon.VerticalAlignment = VerticalAlignment.Top;
        var pixel = new Grid { ColumnDefinitions = new("Auto,*,Auto"), Margin = new(20, 0, 20, 0), Children = { _swatch, lines, _lockIcon } };
        Grid.SetColumn(lines, 1);
        Grid.SetColumn(_lockIcon, 2);
        body.Children.Add(pixel);

        var copy = new WrapPanel { Margin = new(16, 12, 16, 0) };
        foreach (var format in new[] { "HEX", "RGB", "RGBA", "Float", "HSV" })
        {
            TextBlock? self = null;
            self = Chip(format, () =>
            {
                if (_color is not { } c) return;
                TopLevel.GetTopLevel(View)?.Clipboard?.SetTextAsync(Format(format, c));
                self!.Text = "Copied";
                Avalonia.Threading.DispatcherTimer.RunOnce(() => self.Text = format, TimeSpan.FromSeconds(1));
            });
            ToolTip.SetTip(self, $"Copy as {format}");
            copy.Children.Add(new Border { Margin = new(4), Child = self });
        }
        body.Children.Add(copy);

        _image.PixelHovered += (x, y, r, g, b, a) => { if (_locked == null) Show((x, y, r, g, b, a)); };
        _image.PixelLeft += () => { if (_locked == null) Show(null); };
        // Click on the image locks that pixel; clicking again unlocks (and shows the pixel under the pointer).
        _image.PixelClicked += (x, y, r, g, b, a) => Lock(_locked == null ? (x, y, r, g, b, a) : null);
        Show(null);

        View = Ui.SidePanel(left: false, new ScrollViewer { Content = body });
        View.PropertyChanged += (_, e) => { if (e.Property == Visual.IsVisibleProperty) _image.Inspecting = View.IsVisible; };
        Sync();
    }

    static Grid Row(Slider slider, TextBlock value)
    {
        var g = new Grid { ColumnDefinitions = new("*,56"), Margin = new(20, 0, 16, 0) };
        value.TextAlignment = TextAlignment.Right;
        Grid.SetColumn(value, 1);
        g.Children.Add(slider);
        g.Children.Add(value);
        return g;
    }

    static TextBlock Chip(string text, Action click)
    {
        var t = new TextBlock
        {
            Text = text, FontSize = 12, FontWeight = FontWeight.SemiBold, Padding = new(10, 5), Foreground = Ui.Text,
            Background = Ui.Hover, Cursor = new Cursor(StandardCursorType.Hand),
        };
        t.PointerPressed += (_, e) => { click(); e.Handled = true; };
        return t;
    }

    void Lock((int, int, byte, byte, byte, byte)? pick)
    {
        _locked = pick;
        Show(pick);
    }

    void Show((int X, int Y, byte R, byte G, byte B, byte A)? c)
    {
        _color = c ?? _locked;
        bool has = _color != null;
        _pos.Text = has ? $"x {_color!.Value.X}   y {_color.Value.Y}" : "";
        _rgba.Text = has ? $"R {_color!.Value.R}   G {_color.Value.G}   B {_color.Value.B}   A {_color.Value.A}" : "";
        _hex.Text = has ? Format("HEX", _color!.Value) : "";
        _swatch.Fill = has ? new SolidColorBrush(Color.FromArgb(_color!.Value.A, _color.Value.R, _color.Value.G, _color.Value.B)) : Brushes.Transparent;
        _swatch.Stroke = _locked != null ? Ui.Accent : Ui.Edge;
        _lockIcon.IsVisible = _locked != null;
        SyncChips();
    }

    void SyncChips()
    {
        _loupeChip.Foreground = _image.Loupe ? Ui.Accent : Ui.Muted;
        _loupeChip.Background = _image.Loupe ? Ui.AccentSoft : Ui.Hover;
        _unlock.IsVisible = _locked != null;
    }

    // Ctrl+C in image mode: the shown color as hex.
    public bool CopyHex()
    {
        if (_color is not { } c) return false;
        TopLevel.GetTopLevel(View)?.Clipboard?.SetTextAsync(Format("HEX", c));
        return true;
    }

    static string Format(string format, (int X, int Y, byte R, byte G, byte B, byte A) c)
    {
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        string F(byte v) => (v / 255.0).ToString("0.000", inv);
        switch (format)
        {
            case "HEX": return $"#{c.R:X2}{c.G:X2}{c.B:X2}" + (c.A < 255 ? $"{c.A:X2}" : "");
            case "RGB": return $"{c.R}, {c.G}, {c.B}";
            case "RGBA": return $"{c.R}, {c.G}, {c.B}, {c.A}";
            case "Float": return $"{F(c.R)}, {F(c.G)}, {F(c.B)}" + (c.A < 255 ? $", {F(c.A)}" : "");
            default:
            {
                double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0, max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b)), d = max - min;
                double h = d == 0 ? 0 : max == r ? 60 * (((g - b) / d + 6) % 6) : max == g ? 60 * ((b - r) / d + 2) : 60 * ((r - g) / d + 4);
                return string.Format(inv, "{0:0}°, {1:0}%, {2:0}%", h, max == 0 ? 0 : d / max * 100, max * 100);
            }
        }
    }

    void Changed()
    {
        if (_updating) return;
        _image.SetAdjust(_image.Channel, Math.Round(_exposure.Value, 1), Math.Round(_gamma.Value, 2));
        Labels();
    }

    // Reflect the view's current settings (they persist across images).
    public void Sync()
    {
        _updating = true;
        _exposure.Value = _image.Exposure;
        _gamma.Value = _image.Gamma;
        _updating = false;
        for (int i = 0; i < _channels.Count; i++)
        {
            bool on = i == _image.Channel;
            _channels[i].Foreground = on ? Ui.Accent : Ui.Muted;
            ((Border)_channels[i].Parent!).Background = on ? Ui.AccentSoft : Brushes.Transparent;
        }
        Labels();
    }

    void Labels()
    {
        _exposureValue.Text = $"{(_image.Exposure >= 0 ? "+" : "")}{_image.Exposure:0.0} EV";
        _gammaValue.Text = $"{_image.Gamma:0.00}";
    }
}
