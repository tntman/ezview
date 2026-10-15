using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Shapes;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace EzView;

enum LoopMode { Off, File, Queue }

// Anything the player bar can drive: video/audio/sequences (mpv) and animated 3D scenes.
interface IPlayback
{
    double Position { get; }   // seconds
    double Duration { get; }
    bool Paused { get; }
    int Frame { get; }         // absolute frame number, e.g. 1001
    int FirstFrame { get; }
    int FrameCount { get; }
    void TogglePause();
    void Seek(double seconds);
    void SeekFraction(double fraction, bool exact);
    void StepFrame(int delta);
}

// Bottom player bar for video/audio/sequences: title, prev/play/next, progress + frame, loop, volume.
class PlayBar : Border
{
    readonly Func<IPlayback?> _player;
    readonly TextBlock _title = new() { Foreground = Ui.Text, FontSize = 13, FontWeight = FontWeight.Bold, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _sub = new() { Foreground = Ui.Muted, FontSize = 11, Margin = new(0, 2, 0, 0), TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _art = Ui.Icon("", 16);
    readonly TextBlock _time = new() { Foreground = Ui.Muted, FontSize = 11, VerticalAlignment = VerticalAlignment.Center, Margin = new(14, 0, 12, 0), MinWidth = 170 };
    readonly Button _play, _mute, _loop;
    readonly Track _progress, _volume;
    string _baseSub = "";
    bool _sequence;

    public PlayBar(Func<IPlayback?> player, Action<int> step, Action cycleLoop)
    {
        _player = player;
        Height = 72;
        Background = Ui.Glass;
        BorderBrush = Ui.Edge;
        BorderThickness = new(0, 1, 0, 0);
        VerticalAlignment = VerticalAlignment.Bottom;
        _art.Foreground = Ui.Muted;
        _art.HorizontalAlignment = HorizontalAlignment.Center;

        _play = IconButton("", () => _player()?.TogglePause(), 38);
        _play.BorderBrush = Brush.Parse("#555");
        _play.BorderThickness = new(1.5);
        _mute = IconButton("", () => (_player() as VideoView)?.ToggleMute(), 32);
        _loop = IconButton("", cycleLoop, 32);
        _progress = new Track(knob: false, (f, final) => _player()?.SeekFraction(f, final));
        _volume = new Track(knob: true, (f, _) => (_player() as VideoView)?.SetVolume(f * 100)) { Width = 96 };

        var info = new DockPanel
        {
            Width = 260, Margin = new(16, 0, 16, 0), VerticalAlignment = VerticalAlignment.Center,
            Children =
            {
                new Border { Width = 44, Height = 44, CornerRadius = new(8), Background = Brush.Parse("#222"), Margin = new(0, 0, 12, 0), Child = _art },
                new StackPanel { VerticalAlignment = VerticalAlignment.Center, Children = { _title, _sub } },
            },
        };
        DockPanel.SetDock(info.Children[0], Dock.Left);

        var controls = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 6, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 20, 0),
            Children = { IconButton("", () => step(-1), 32), _play, IconButton("", () => step(1), 32) },
        };

        var right = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new(0, 0, 20, 0), Children = { _loop, _mute, _volume } };

        var grid = new Grid { ColumnDefinitions = new("Auto,Auto,*,Auto,Auto") };
        foreach (var (c, i) in new Control[] { info, controls, _progress, _time, right }.Select((c, i) => (c, i)))
        {
            Grid.SetColumn(c, i);
            grid.Children.Add(c);
        }
        Child = grid;
    }

    static Button IconButton(string glyph, Action click, double size)
    {
        var b = new Button
        {
            Content = glyph, FontFamily = Ui.Icons, FontSize = 14, Width = size, Height = size, CornerRadius = new(size / 2),
            Background = Brushes.Transparent, Foreground = Ui.Text, BorderThickness = new(0), Padding = new(0),
            Focusable = false, VerticalAlignment = VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => click();
        return b;
    }

    /// <param name="sequence">Show the frame as "1013 (1001–1048)" instead of "13 / 48".</param>
    public void SetFile(string title, string sub, string glyph, bool sequence)
    {
        _title.Text = title;
        _sub.Text = _baseSub = sub;
        _art.Text = glyph;
        _sequence = sequence;
    }

    // Loop, mute and volume only mean something for mpv media.
    public void SetMediaControls(bool visible) => _loop.IsVisible = _mute.IsVisible = _volume.IsVisible = visible;

    public void SetLoop(LoopMode mode)
    {
        _loop.Content = mode == LoopMode.File ? "" : "";
        _loop.Foreground = mode == LoopMode.Off ? Ui.Muted : Ui.Accent;
        ToolTip.SetTip(_loop, mode switch { LoopMode.File => "Repeat file", LoopMode.Queue => "Repeat queue", _ => "Repeat off" });
    }

    public void Update()
    {
        if (_player() is not { } pl) return;
        double pos = pl.Position, dur = pl.Duration;
        if (!_progress.Dragging) _progress.Value = dur > 0 ? pos / dur : 0;
        int frames = pl.FrameCount, first = pl.FirstFrame, frame = pl.Frame;
        var frameText = frames <= 0 ? ""
            : _sequence ? $"  ·  {frame} ({first}–{first + frames - 1})"
            : $"  ·  {frame} / {first + frames - 1}";
        _time.Text = $"{Fmt(pos)} / {Fmt(dur)}{frameText}";
        _play.Content = pl.Paused ? "" : "";
        if (pl is not VideoView v) return;
        double w = v.Get("width"), h = v.Get("height"), speed = v.Get("speed");
        if (!_volume.Dragging) _volume.Value = v.Get("volume") / 100;
        _mute.Content = v.GetFlag("mute") ? "" : "";
        _sub.Text = _baseSub + (w > 0 ? $"  ·  {w} × {h}" : "") + (Math.Abs(speed - 1) > 0.01 ? $"  ·  {speed:0.##}×" : "");
    }

    static string Fmt(double s) => TimeSpan.FromSeconds(Math.Max(s, 0)).ToString(s >= 3600 ? @"h\:mm\:ss" : @"m\:ss");

    // Thin clickable/draggable bar: grey track, accent fill, optional white knob.
    // seek(value, final): final is true on release, so dragging can use fast seeks and the drop lands exactly.
    class Track : Panel
    {
        readonly Border _fill = new() { Height = 4, CornerRadius = new(2), Background = Ui.Accent, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center };
        readonly Ellipse? _knob;
        double _value, _sent = -1;

        public bool Dragging { get; private set; }

        public Track(bool knob, Action<double, bool> seek)
        {
            Height = 24;
            Background = Brushes.Transparent;
            VerticalAlignment = VerticalAlignment.Center;
            Cursor = new Cursor(StandardCursorType.Hand);
            Children.Add(new Border { Height = 4, CornerRadius = new(2), Background = Brush.Parse("#333"), VerticalAlignment = VerticalAlignment.Center });
            Children.Add(_fill);
            if (knob) Children.Add(_knob = new Ellipse { Width = 12, Height = 12, Fill = Brushes.White, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center });

            double At(PointerEventArgs e) => Bounds.Width > 0 ? Math.Clamp(e.GetPosition(this).X / Bounds.Width, 0, 1) : 0;
            PointerPressed += (_, e) =>
            {
                if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
                e.Pointer.Capture(this);
                Dragging = true;
                Value = At(e);
                seek(_sent = _value, false);
                e.Handled = true;
            };
            PointerMoved += (_, e) =>
            {
                if (!Dragging) return;
                Value = At(e);
                if (Math.Abs(_value - _sent) > 0.002) seek(_sent = _value, false);
            };
            PointerReleased += (_, e) =>
            {
                if (!Dragging) return;
                Dragging = false;
                e.Pointer.Capture(null);
                seek(Value = At(e), true);
                e.Handled = true;
            };
            PointerCaptureLost += (_, _) => Dragging = false;
            SizeChanged += (_, _) => Value = _value;
        }

        public double Value
        {
            get => _value;
            set
            {
                _value = Math.Clamp(value, 0, 1);
                _fill.Width = Bounds.Width * _value;
                if (_knob != null) _knob.Margin = new(Math.Max(0, Bounds.Width * _value - 6), 0, 0, 0);
            }
        }
    }
}
