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
    readonly Rectangle _swatch = new() { Width = 28, Height = 28, RadiusX = 6, RadiusY = 6, Stroke = Ui.Edge, StrokeThickness = 1, Fill = Brushes.Transparent };
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
        var pixel = new Grid { ColumnDefinitions = new("Auto,*"), Margin = new(20, 0) };
        var lines = new StackPanel { Margin = new(12, 0, 0, 0), Spacing = 2, Children = { _pos, _rgba, _hex } };
        Grid.SetColumn(lines, 1);
        pixel.Children.Add(_swatch);
        pixel.Children.Add(lines);
        body.Children.Add(pixel);
        _pos.Text = "Move over the image";
        _pos.Foreground = Ui.Muted;

        _image.PixelHovered += (x, y, r, g, b, a) =>
        {
            _pos.Foreground = Ui.Text;
            _pos.Text = $"x {x}   y {y}";
            _rgba.Text = $"R {r}   G {g}   B {b}   A {a}";
            _hex.Text = $"#{r:X2}{g:X2}{b:X2}{(a < 255 ? a.ToString("X2") : "")}   ·   {r / 255.0:0.000} {g / 255.0:0.000} {b / 255.0:0.000}";
            _swatch.Fill = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        };
        _image.PixelLeft += () =>
        {
            _pos.Foreground = Ui.Muted;
            _pos.Text = "Move over the image";
            _rgba.Text = _hex.Text = "";
            _swatch.Fill = Brushes.Transparent;
        };

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
