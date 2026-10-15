using System.Globalization;
using Avalonia;
using Avalonia.Animation;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ImageMagick;

namespace EzView;

class MainWindow : Window
{
    readonly Grid _host = new() { Background = Ui.Bg };
    readonly ImageView _image = new();
    VideoView? _video;
    ModelView? _model;
    readonly TextBlock _msg = new() { Foreground = Ui.Muted, FontSize = 14, TextAlignment = Avalonia.Media.TextAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap, Margin = new(32) };
    readonly TextBlock _name = new() { Foreground = Ui.Text, FontSize = 13, FontWeight = Avalonia.Media.FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
    readonly TextBlock _count = new() { Foreground = Ui.Muted, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Margin = new(10, 0, 0, 0) };
    readonly Panel _overlay = new() { Transitions = [new DoubleTransition { Property = OpacityProperty, Duration = TimeSpan.FromMilliseconds(180) }] };
    readonly Border _top;
    readonly PlayBar _bottom;
    readonly QueuePanel _queuePanel;
    readonly InfoPanel _info = new();
    readonly OutlinerPanel _outliner;
    readonly ImagePanel _imagePanel;
    readonly DispatcherTimer _hide = new() { Interval = TimeSpan.FromSeconds(1.5) };
    readonly DispatcherTimer _tick = new() { Interval = TimeSpan.FromMilliseconds(250) };

    List<Entry> _queue = [];
    int _index;
    Kind _kind;
    LoopMode _loop;
    double _seqFps = 24;
    bool _menuOpen, _wasEof, _spaceDown;
    DateTime _loadedAt, _infoAt;
    int _infoId;
    WindowState _beforeFullscreen;

    Entry? Current => _queue.Count > 0 ? _queue[_index] : null;
    bool IsAudio => _kind == Kind.Video && Current is { IsAudio: true };
    // What the player bar and playback keys drive: mpv media, or an animated 3D scene.
    IPlayback? Player => _kind == Kind.Video ? _video : _kind == Kind.Model && _model?.IsAnimated == true ? _model : null;

    public MainWindow(string? startFile)
    {
        Title = "ezview";
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://ezview/Assets/ezview.ico")));
        Width = 1280; Height = 800;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        Background = Ui.Bg;
        // Grayscale AA: ClearType fringes look coloured on dark, semi-transparent surfaces.
        RenderOptions.SetTextRenderingMode(this, TextRenderingMode.Antialias);
        ExtendClientAreaToDecorationsHint = true;
        ExtendClientAreaChromeHints = Avalonia.Platform.ExtendClientAreaChromeHints.NoChrome;
        ExtendClientAreaTitleBarHeightHint = -1;

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Children = {
            Btn("", () => WindowState = WindowState.Minimized),
            Btn("", ToggleMaximize),
            Btn("", Close) } };
        DockPanel.SetDock(buttons, Dock.Right);
        _top = new Border
        {
            Height = 32, VerticalAlignment = VerticalAlignment.Top, Background = Ui.Glass, BorderBrush = Ui.Edge, BorderThickness = new(0, 0, 0, 1),
            Child = new DockPanel { Children = { buttons, new StackPanel { Orientation = Orientation.Horizontal, Margin = new(16, 0, 0, 0), Children = { _name, _count } } } },
        };
        // The Border has the background, so it (not the DockPanel inside) receives the press.
        _top.PointerPressed += (_, e) => { if (e.ClickCount == 1 && e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) BeginMoveDrag(e); };

        _bottom = new PlayBar(() => Player, Step, CycleLoop, TogglePlayQueue) { IsVisible = false };
        _bottom.SetPlayQueue(_playQueue);
        _bottom.SetLoop(_loop);
        _imagePanel = new ImagePanel(_image);
        _outliner = new OutlinerPanel(() => _kind == Kind.Model ? _model : null);
        _queuePanel = new QueuePanel(i => { _index = i; _ = ShowCurrent(); }, RemoveAt, () => AddFilesDialog());

        _overlay.Children.Add(_top);
        _overlay.Children.Add(_bottom);
        _overlay.Children.Add(_zoomBar = ZoomBar());
        _host.Children.Add(_image);
        _host.Children.Add(_msg);
        _keysView = KeysView();
        Content = new Panel { Children = { _host, _queuePanel.View, _outliner.View, _info.View, _imagePanel.View, _overlay, _keysView } };

        AddHandler(KeyDownEvent, OnKey, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, (_, e) =>
        {
            if (e.Key != Key.Space || !_spaceDown) return;
            _spaceDown = false;
            if (_kind == Kind.Model && _model is { IsAnimated: true, Navigated: false }) { _model.TogglePause(); Poke(); }
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel);
        AddHandler(PointerMovedEvent, (_, _) => Poke(), Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        // Mouse wheel over a video: volume (side panels keep their own scrolling).
        AddHandler(PointerWheelChangedEvent, (_, e) =>
        {
            if (_kind != Kind.Video || _video == null || e.Delta.Y == 0 || _keysView.IsVisible
                || _queuePanel.View.IsPointerOver || _info.View.IsPointerOver) return;
            _video.Volume(e.Delta.Y > 0 ? 5 : -5);
            Poke();
            e.Handled = true;
        });
        // Right-click without dragging opens the menu (right-drag still pans 3D models).
        Point? rightDown = null;
        AddHandler(PointerPressedEvent, (_, e) =>
        {
            if (e.GetCurrentPoint(this).Properties.IsRightButtonPressed) rightDown = e.GetPosition(this);
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        AddHandler(PointerReleasedEvent, (_, e) =>
        {
            if (e.InitialPressMouseButton == MouseButton.Right && rightDown is { } p)
            {
                var d = e.GetPosition(this) - p;
                if (Math.Abs(d.X) + Math.Abs(d.Y) < 5) ShowMenu();
            }
            rightDown = null;
        }, Avalonia.Interactivity.RoutingStrategies.Tunnel, true);
        AddHandler(DoubleTappedEvent, (_, e) =>
        {
            if (e.Source is Visual v && (v.FindAncestorOfType<Button>(true) != null || _bottom.IsPointerOver || _queuePanel.View.IsPointerOver || _outliner.View.IsPointerOver || _info.View.IsPointerOver || _imagePanel.View.IsPointerOver)) return;
            _video?.CancelClick(); // a double-click means fullscreen, never play/pause
            ToggleFullscreen();
        }, Avalonia.Interactivity.RoutingStrategies.Bubble, true);
        DragDrop.SetAllowDrop(this, true);
        AddHandler(DragDrop.DropEvent, (_, e) =>
        {
            var files = e.Data.GetFiles()?.Select(f => f.TryGetLocalPath()).OfType<string>().ToList() ?? [];
            if (e.KeyModifiers.HasFlag(KeyModifiers.Control)) AddToQueue(files);
            else if (files.Count == 1) Open(files[0]);
            else if (files.Count > 1) ReplaceQueue(files);
        });

        // The blur under the bars follows the overlay's fade, frame by frame.
        _overlay.PropertyChanged += (_, e) => { if (e.Property == OpacityProperty) UpdateStrips(); };
        _bottom.SizeChanged += (_, _) => UpdateStrips();

        _hide.Tick += (_, _) => HideOverlay();
        _tick.Tick += (_, _) => OnTick();
        _tick.Start();

        ShowMessage("Drop a file here or press Ctrl+O");
        Opened += (_, _) => { if (startFile != null) Open(startFile); };
    }

    // Image zoom pill at the bottom: − 100 % + | Fit | 1:1 (fades with the bars).
    readonly Border _zoomBar;
    readonly TextBlock _zoomText = new() { FontSize = 12, Foreground = Ui.Text, MinWidth = 48, TextAlignment = Avalonia.Media.TextAlignment.Center, VerticalAlignment = VerticalAlignment.Center };
    Button _fitBtn = null!, _actualBtn = null!, _compareBtn = null!;
    readonly Border _compareSep = new() { Width = 1, Height = 16, Background = Ui.Edge, Margin = new(4, 0), IsVisible = false };

    Border ZoomBar()
    {
        Button B(string text, string tip, Action click, bool icon = false)
        {
            var b = new Button
            {
                Content = text, FontFamily = icon ? Ui.Icons : FontFamily.Default, FontSize = icon ? 11 : 12, FontWeight = Avalonia.Media.FontWeight.SemiBold,
                Height = 28, MinWidth = 32, Padding = new(10, 0), CornerRadius = new(14), Background = Brushes.Transparent, Foreground = Ui.Text,
                BorderThickness = new(0), Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
            };
            ToolTip.SetTip(b, tip);
            b.Click += (_, _) => click();
            return b;
        }
        _fitBtn = B("Fit", "Fit to window (0)", ResetView);
        _actualBtn = B("1:1", "Actual size (H)", _image.OneToOne);
        _image.ZoomChanged += SyncZoom;
        _compareBtn = B("", "Compare mode (C), flip A/B (Tab), pin B (B)", () => { if (_image.CompareActive) _image.CycleCompare(); });
        _compareBtn.IsVisible = false;
        _image.CompareChanged += SyncCompare;
        return new Border
        {
            IsVisible = false, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Bottom, Margin = new(0, 0, 0, 16),
            Background = Ui.Panel, BorderBrush = Ui.Edge, BorderThickness = new(1), CornerRadius = new(18), Padding = new(4),
            Child = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 2,
                Children =
                {
                    B("", "Zoom out", () => _image.ZoomBy(1 / 1.25), icon: true), _zoomText, B("", "Zoom in", () => _image.ZoomBy(1.25), icon: true),
                    new Border { Width = 1, Height = 16, Background = Ui.Edge, Margin = new(4, 0) },
                    _fitBtn, _actualBtn, _compareSep, _compareBtn,
                },
            },
        };
    }

    IEnumerable<MenuItem> CompareItems()
    {
        bool isB = _image.ComparePinned && !_image.CompareActive;
        yield return Item("", isB ? "Unpin as B" : "Set as compare image (B)", _image.PinCompare, "B");
        if (!_image.CompareActive) yield break;
        yield return Choice("Wipe", () => _image.SetCompareMode(CompareMode.Wipe), _image.CompareMode == CompareMode.Wipe);
        yield return Choice("Flip A / B  (Tab)", _image.SwapAB, _image.CompareMode == CompareMode.Flip);
        yield return Choice("Difference", () => _image.SetCompareMode(CompareMode.Difference), _image.CompareMode == CompareMode.Difference);
        yield return Item("", "Stop comparing", _image.ClearCompare);
    }

    void SyncCompare()
    {
        var label = _image.CompareLabel;
        _compareBtn.IsVisible = _compareSep.IsVisible = label != null;
        _compareBtn.Content = new TextBlock { Text = label }; // a plain string would treat "_" as an access key
        _compareBtn.Foreground = _image.CompareActive ? Ui.Accent : Ui.Muted;
    }

    void SyncZoom()
    {
        _zoomText.Text = $"{_image.Zoom * 100:0.#} %";
        _fitBtn.Foreground = _image.IsFit ? Ui.Accent : Ui.Text;
        _actualBtn.Foreground = !_image.IsFit && Math.Abs(_image.Zoom - 1) < 1e-6 ? Ui.Accent : Ui.Text;
    }

    // ---- Keyboard shortcuts sheet (F1 or the right-click menu); Esc, the X or a click outside closes it.
    readonly Panel _keysView;

    Panel KeysView()
    {
        (string Title, (string Keys, string What)[] Rows)[] groups =
        [
            ("General", [
                ("← →  PgUp PgDn", "Previous / next file"),
                ("Double-click  F  F11", "Fullscreen"),
                ("Q  I", "Queue / info panel"),
                ("Ctrl+O", "Open file"),
                ("Esc", "Leave fullscreen, close"),
                ("F1", "This sheet"),
            ]),
            ("Video", [
                ("Space  K", "Play / pause"),
                ("← →", "Back / forward 5 s, keeps playing"),
                (",  .", "One frame back / forward (← → in sequences)"),
                ("J L  Ctrl+← →", "Jump 10 s / 60 s"),
                ("Home  0–9", "Start / jump to 0–90 %"),
                ("[  ]  Backspace", "Slower / faster / normal speed"),
                ("↑ ↓  Wheel  M", "Volume / mute"),
                ("R  A", "Loop mode / play queue"),
                ("S", "Save frame"),
            ]),
            ("Images", [
                ("E", "Adjust and inspect panel"),
                ("0  H", "Fit / actual size"),
                ("+ −  Wheel", "Zoom"),
                ("R  Shift+R  Shift+F", "Rotate right / left, flip"),
                ("B  C  Tab", "Pin as B / compare mode / flip A and B"),
                ("Ctrl+C", "Copy picked color"),
            ]),
            ("3D", [
                ("LMB  MMB  RMB", "Tumble / pan / dolly"),
                ("F  H", "Frame selection / reset view"),
                ("W  G  T", "Wireframe / grid / textures"),
                ("B  M", "Skeleton / geometry"),
                ("O  Esc", "Outliner / clear selection"),
                ("Space", "Play / pause (when not navigating)"),
            ]),
        ];
        var columns = new Grid { ColumnDefinitions = new("*,40,*") };
        var left = new StackPanel();
        var right = new StackPanel();
        Grid.SetColumn(right, 2);
        columns.Children.Add(left);
        columns.Children.Add(right);
        for (int g = 0; g < groups.Length; g++)
        {
            var col = g % 2 == 0 ? left : right;
            col.Children.Add(new TextBlock { Text = groups[g].Title.ToUpperInvariant(), FontSize = 11, LetterSpacing = 1.2, Foreground = Ui.Muted, Margin = new(0, g < 2 ? 0 : 22, 0, 8) });
            foreach (var (keys, what) in groups[g].Rows)
            {
                var row = new Grid { ColumnDefinitions = new("170,*"), Margin = new(0, 3) };
                var k = new TextBlock { Text = keys, FontSize = 12.5, FontWeight = Avalonia.Media.FontWeight.SemiBold, Foreground = Ui.Text, TextWrapping = TextWrapping.Wrap };
                var w = new TextBlock { Text = what, FontSize = 12.5, Foreground = Ui.Muted, TextWrapping = TextWrapping.Wrap };
                Grid.SetColumn(w, 1);
                row.Children.Add(k);
                row.Children.Add(w);
                col.Children.Add(row);
            }
        }
        var close = Btn("\uE711", () => _keysView!.IsVisible = false);
        close.HorizontalAlignment = HorizontalAlignment.Right;
        close.CornerRadius = new(8);
        var card = new Border
        {
            Background = Ui.Panel, BorderBrush = Ui.Edge, BorderThickness = new(1), CornerRadius = new(14), Padding = new(28, 18, 28, 28),
            MaxWidth = 820, Margin = new(24), HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Child = new StackPanel
            {
                Children =
                {
                    new Grid { Margin = new(0, 0, -14, 10), Children = { new TextBlock { Text = "Keyboard shortcuts", FontSize = 16, FontWeight = Avalonia.Media.FontWeight.Bold, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center }, close } },
                    new ScrollViewer { Content = columns, MaxHeight = 620 },
                },
            },
        };
        card.PointerPressed += (_, e) => e.Handled = true; // clicks inside the card don't close it
        var sheet = new Panel { IsVisible = false, Background = new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), Children = { card } };
        sheet.PointerPressed += (_, e) => { sheet.IsVisible = false; e.Handled = true; };
        return sheet;
    }

    static Button Btn(string glyph, Action click)
    {
        var b = new Button
        {
            Content = glyph, FontFamily = Ui.Icons, FontSize = 10,
            Width = 46, Height = 32, Background = Brushes.Transparent, Foreground = Brush.Parse("#aaa"),
            BorderThickness = new(0), CornerRadius = new(0), Focusable = false, VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        b.Click += (_, _) => click();
        return b;
    }

    // ---- Queue ----

    void Open(string path)
    {
        path = Path.GetFullPath(path);
        path = Media.MdlCompanionBase(path) ?? path; // manT.mdl / man01.mdl open their main model
        _queue = Media.Group(Directory.EnumerateFiles(Path.GetDirectoryName(path)!));
        _index = _queue.FindIndex(e => Same(e.Path, path) || e.Frames?.Any(f => Same(f, path)) == true);
        if (_index < 0) { _queue = [new Entry(path)]; _index = 0; }
        _ = ShowCurrent();
    }

    static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    void ReplaceQueue(IEnumerable<string> files)
    {
        var entries = Media.Group(files);
        if (entries.Count == 0) return;
        _queue = entries;
        _index = 0;
        _ = ShowCurrent();
    }

    void AddToQueue(IEnumerable<string> files)
    {
        var entries = Media.Group(files);
        if (entries.Count == 0) return;
        bool empty = _queue.Count == 0;
        _queue.AddRange(entries);
        if (empty) _ = ShowCurrent();
        else RefreshQueue();
    }

    void RemoveAt(int i)
    {
        _queue.RemoveAt(i);
        if (_queue.Count == 0)
        {
            _video?.Stop();
            SetKind(Kind.None);
            _name.Text = _count.Text = "";
            ShowMessage("The queue is empty");
            RefreshQueue();
            return;
        }
        if (i < _index) _index--;
        else if (i == _index) { _index = Math.Min(_index, _queue.Count - 1); _ = ShowCurrent(); return; }
        RefreshQueue();
    }

    void Step(int d)
    {
        if (_queue.Count == 0) return;
        _index = (_index + d + _queue.Count) % _queue.Count;
        _ = ShowCurrent();
    }

    // End of a video/audio item: play the next one, wrap only when looping the queue.
    // Off by default: a file stops at its end. Repeat-queue implies playing through.
    bool _playQueue;

    void TogglePlayQueue()
    {
        _playQueue = !_playQueue;
        _bottom.SetPlayQueue(_playQueue);
    }

    void Advance()
    {
        if (!_playQueue && _loop != LoopMode.Queue) return;
        if (_index < _queue.Count - 1) Step(1);
        else if (_loop == LoopMode.Queue) { _index = 0; _ = ShowCurrent(); }
    }

    void RefreshQueue()
    {
        _count.Text = _queue.Count > 1 ? $"{_index + 1} / {_queue.Count}" : "";
        if (_queuePanel.View.IsVisible) _queuePanel.Refresh(_queue, _index);
    }

    void CycleLoop()
    {
        _loop = (LoopMode)(((int)_loop + 1) % 3);
        _bottom.SetLoop(_loop);
        if (_kind == Kind.Video) _video?.Command("set", "loop-file", _loop == LoopMode.File || Current?.Ext == ".gif" ? "inf" : "no");
    }

    async Task ShowCurrent()
    {
        if (Current is not { } entry) return;
        var kind = entry.Kind == Kind.None ? Kind.Image : entry.Kind; // unknown extension: let ImageMagick try
        _name.Text = entry.Name;
        Title = $"{entry.Name} – ezview";
        RefreshQueue();
        _msg.IsVisible = false;
        if (kind != Kind.Video) _video?.Stop();
        try
        {
            SetKind(kind);
            switch (kind)
            {
                case Kind.Image: await _image.Load(entry.Path); break;
                case Kind.Video:
                    _loadedAt = DateTime.Now;
                    bool loop = _loop == LoopMode.File || entry.Ext == ".gif";
                    var ext = entry.Ext.TrimStart('.').ToUpperInvariant();
                    if (entry.IsSequence)
                    {
                        _video!.FrameOffset = entry.FirstFrame;
                        _bottom.SetFile(entry.Name, $"{ext} sequence  ·  {entry.Frames!.Length} frames  ·  {_seqFps:0.###} fps", entry.Glyph, true);
                        _video!.LoadSequence(entry.Frames, _seqFps, loop);
                    }
                    else
                    {
                        _video!.FrameOffset = 1;
                        _bottom.SetFile(Path.GetFileNameWithoutExtension(entry.Path), ext, entry.Glyph, false);
                        _video!.Load(entry.Path, loop);
                    }
                    break;
                case Kind.Model:
                    await _model!.Load(entry.Path);
                    if (Current == entry && _outliner.View.IsVisible) _outliner.Refresh();
                    if (Current == entry && _model.IsAnimated)
                    {
                        _bottom.SetFile(entry.Name, $"{_model.Info[0].Value}  ·  {_model.FrameCount} frames  ·  {_model.Info.First(i => i.Key == "Frame rate").Value}", entry.Glyph, true);
                        _bottom.IsVisible = true;
                        UpdateStrips();
                    }
                    break;
            }
        }
        catch (Exception ex)
        {
            if (Current != entry) return; // superseded by a newer file
            SetKind(Kind.None);
            ShowMessage($"Can't open {entry.Name}\n{ex.Message}");
        }
        RefreshInfo();
        Poke();
    }

    void SetKind(Kind kind)
    {
        if (kind == Kind.Video && _video == null) _host.Children.Insert(0, _video = new VideoView());
        if (kind == Kind.Model && _model == null)
        {
            _host.Children.Insert(0, _model = new ModelView());
            _model.SelectionChanged += () => { if (_outliner.View.IsVisible) _outliner.Reveal(); };
        }
        _kind = kind;
        _image.IsVisible = kind == Kind.Image;
        if (_video != null) _video.IsVisible = kind == Kind.Video;
        if (_model != null) _model.IsVisible = kind == Kind.Model;
        _bottom.IsVisible = kind == Kind.Video;
        _zoomBar.IsVisible = kind == Kind.Image;
        _bottom.SetMediaControls(kind == Kind.Video);
        if (kind != Kind.Model) _outliner.View.IsVisible = false;
        if (kind != Kind.Image) _imagePanel.View.IsVisible = false;
        _host.Background = kind == Kind.Video ? Brushes.Black : Ui.Bg;
        UpdateStrips();
    }

    void UpdateStrips()
    {
        double top = _top.Height, bottom = _bottom.IsVisible ? _bottom.Height : 0, opacity = _overlay.Opacity;
        // Side panels stop above the player bar so their bottom row isn't hidden under it.
        _queuePanel.View.Margin = _outliner.View.Margin = _info.View.Margin = _imagePanel.View.Margin = new(Ui.Gap, top + Ui.Gap, Ui.Gap, bottom + Ui.Gap);
        _image.SetStrips(top, bottom, opacity);
        foreach (var blur in new[] { _video?.Blur, _model?.Blur })
            if (blur != null) (blur.Top, blur.Bottom, blur.Opacity) = (top, bottom, opacity);
        _video?.RequestNextFrameRendering();
        _model?.RequestNextFrameRendering();
    }

    void ShowMessage(string text)
    {
        _msg.Text = text;
        _msg.IsVisible = true;
    }

    void OnTick()
    {
        if (Player != null && _overlay.Opacity > 0) _bottom.Update();
        if (_kind != Kind.Video || _video == null) return;
        // eof-reached can still be true from the previous file right after loadfile, hence the grace period.
        // Act on the moment playback reaches the end, so a restarted video can advance again next time.
        var eof = _video.GetFlag("eof-reached");
        if (eof && !_wasEof && (DateTime.Now - _loadedAt).TotalSeconds > 1) Advance();
        _wasEof = eof;
        if (_info.View.IsVisible && (DateTime.Now - _infoAt).TotalSeconds > 1) RefreshInfo();
    }

    // ---- Keys ----

    void OnKey(object? sender, KeyEventArgs e)
    {
        bool shift = e.KeyModifiers.HasFlag(KeyModifiers.Shift), ctrl = e.KeyModifiers.HasFlag(KeyModifiers.Control);
        var v = _kind == Kind.Video ? _video : null;
        var pl = Player;
        // 3D: Space is held for Houdini-style navigation, so playback toggles on release, and only if the view wasn't dragged.
        if (e.Key == Key.Space && _kind == Kind.Model)
        {
            if (!_spaceDown) { _spaceDown = true; _model!.Navigated = false; }
            e.Handled = true;
            return;
        }
        if (_keysView.IsVisible)
        {
            if (e.Key is Key.Escape or Key.F1) _keysView.IsVisible = false;
            e.Handled = true;
            return;
        }
        if (e.Key == Key.F1) { _keysView.IsVisible = true; e.Handled = true; return; }
        switch (e.Key)
        {
            // Playback (video and animated 3D): arrows step frames, modifiers jump (YouTube/mpv conventions).
            case Key.Left when ctrl && pl != null: pl.Seek(-60); break;
            case Key.Right when ctrl && pl != null: pl.Seek(60); break;
            case Key.Left when shift && pl != null: pl.Seek(-5); break;
            case Key.Right when shift && pl != null: pl.Seek(5); break;
            // Video: arrows jump 5 s and keep playing; sequences and 3D step frames, as do , and . everywhere.
            case Key.Left when v != null && Current?.IsSequence != true: v.Seek(-5); break;
            case Key.Right when v != null && Current?.IsSequence != true: v.Seek(5); break;
            case Key.Left or Key.OemComma when pl != null: pl.StepFrame(-1); break;
            case Key.Right or Key.OemPeriod when pl != null: pl.StepFrame(1); break;
            case Key.J when pl != null: pl.Seek(-10); break;
            case Key.L when pl != null: pl.Seek(10); break;
            case Key.Space or Key.K when pl != null: pl.TogglePause(); break;
            case Key.Home when pl != null: pl.SeekFraction(0, true); break;
            case >= Key.D0 and <= Key.D9 when v != null: v.SeekPercent((e.Key - Key.D0) / 10.0); break;
            case Key.OemOpenBrackets when v != null: v.Command("multiply", "speed", "0.8"); break;
            case Key.OemCloseBrackets when v != null: v.Command("multiply", "speed", "1.25"); break;
            case Key.Back when v != null: v.Command("set", "speed", "1"); break;
            case Key.Up when v != null: v.Volume(5); break;
            case Key.Down when v != null: v.Volume(-5); break;
            case Key.M when v != null: v.ToggleMute(); break;
            case Key.S when v != null: v.Screenshot(); break;
            case Key.H when _kind == Kind.Model: _model!.Reset(); break;
            case Key.E when _kind == Kind.Image: TogglePanel(_imagePanel.View); break;
            case Key.B when _kind == Kind.Model: _model!.ToggleSkeleton(); break;
            case Key.B when _kind == Kind.Image: _image.PinCompare(); break;
            case Key.C when !ctrl && _kind == Kind.Image && _image.CompareActive: _image.CycleCompare(); break;
            case Key.Tab when _kind == Kind.Image && _image.CompareActive: _image.SwapAB(); break;
            case Key.OemPlus or Key.Add when _kind == Kind.Image: _image.ZoomBy(1.25); break;
            case Key.OemMinus or Key.Subtract when _kind == Kind.Image: _image.ZoomBy(1 / 1.25); break;
            case Key.M when _kind == Kind.Model: _model!.ToggleGeometry(); break;
            case Key.O when !ctrl && _kind == Kind.Model: TogglePanel(_outliner.View); break;
            case Key.W when _kind == Kind.Model: _model!.ToggleWireframe(); break;
            case Key.G when _kind == Kind.Model: _model!.ToggleGrid(); break;
            case Key.T when _kind == Kind.Model: _model!.ToggleTextures(); break;
            case Key.R when _kind == Kind.Image: _image.Rotate(shift ? -1 : 1); break;
            case Key.R: CycleLoop(); break;
            case Key.A when Player is VideoView: TogglePlayQueue(); break;
            case Key.Left or Key.PageUp: Step(-1); break;
            case Key.Right or Key.PageDown: Step(1); break;
            case Key.Q: TogglePanel(_queuePanel.View); break;
            case Key.I: TogglePanel(_info.View); break;
            case Key.F when shift && _kind == Kind.Image: _image.FlipHorizontal(); break;
            case Key.F when _kind == Kind.Model: _model!.FocusSelected(); break;
            case Key.F or Key.F11: ToggleFullscreen(); break;
            case Key.Escape when _kind == Kind.Model && _model!.Selected != null: _model.Select(null); break;
            case Key.Escape: if (WindowState == WindowState.FullScreen) ToggleFullscreen(); else Close(); break;
            case Key.O when ctrl: OpenDialog(); break;
            case Key.C when ctrl && _kind == Kind.Image && _imagePanel.View.IsVisible: _imagePanel.CopyHex(); break;
            case Key.D0 or Key.NumPad0: ResetView(); break;
            case Key.D1 or Key.NumPad1 or Key.H when _kind == Kind.Image: _image.OneToOne(); break;
            default: return;
        }
        e.Handled = true;
        Poke();
    }

    void ResetView()
    {
        if (_kind == Kind.Image) _image.Fit();
        else if (_kind == Kind.Model) _model!.Reset();
    }

    void TogglePanel(Border panel)
    {
        panel.IsVisible = !panel.IsVisible;
        // The queue and the outliner share the left edge.
        if (panel.IsVisible && panel == _outliner.View) _queuePanel.View.IsVisible = false;
        if (panel.IsVisible && panel == _queuePanel.View) _outliner.View.IsVisible = false;
        // Info and image adjustments share the right edge.
        if (panel.IsVisible && panel == _imagePanel.View) { _info.View.IsVisible = false; _imagePanel.Sync(); }
        if (panel.IsVisible && panel == _info.View) _imagePanel.View.IsVisible = false;
        if (panel == _outliner.View) _outliner.Refresh();
        if (panel == _queuePanel.View) RefreshQueue();
        else RefreshInfo();
    }

    // ---- Context menu ----

    static MenuItem Item(string glyph, string header, Action click, string? gesture = null, bool? check = null)
    {
        var m = new MenuItem { Header = header, Icon = Ui.Icon(glyph, 14), InputGesture = gesture == null ? null : KeyGesture.Parse(gesture) };
        if (check == true) m.Classes.Add("checked");
        m.Click += (_, _) => click();
        return m;
    }

    static MenuItem Sub(string glyph, string header, IEnumerable<MenuItem> items) =>
        new() { Header = header, Icon = Ui.Icon(glyph, 14), ItemsSource = items.ToList() };

    static MenuItem Choice(string header, Action click, bool selected)
    {
        var m = new MenuItem { Header = header, Icon = selected ? Ui.Icon("", 12) : null };
        if (selected) m.Classes.Add("checked");
        m.Click += (_, _) => click();
        return m;
    }

    // A thin line between groups; never as the first item.
    static void Group(List<Control> items)
    {
        if (items.Count > 0) items.Add(new Separator());
    }

    static void Explorer(string args) => System.Diagnostics.Process.Start("explorer.exe", args);

    void ShowMenu()
    {
        var v = _kind == Kind.Video ? _video : null;
        var entry = Current;
        var items = new List<Control>();
        if (v != null)
        {
            Group(items);
            var paused = v.GetFlag("pause");
            items.Add(Item(paused ? "" : "", paused ? "Play" : "Pause", v.TogglePause, "Space"));
            items.Add(Item("", "Mute", v.ToggleMute, "M", v.GetFlag("mute")));
            items.Add(Sub("", "Repeat", new[] { (LoopMode.Off, "Off"), (LoopMode.File, "Repeat file"), (LoopMode.Queue, "Repeat queue") }
                .Select(x => Choice(x.Item2, () => { while (_loop != x.Item1) CycleLoop(); }, _loop == x.Item1))));
            var speed = v.Get("speed");
            items.Add(Sub("", "Speed", new[] { 0.25, 0.5, 0.75, 1, 1.25, 1.5, 2 }.Select(s =>
                Choice($"{s:0.##}×", () => v.Command("set", "speed", s.ToString(CultureInfo.InvariantCulture)), Math.Abs(speed - s) < 0.01))));
            if (entry?.IsSequence == true)
                items.Add(Sub("", "Frame rate", new[] { 23.976, 24, 25, 30, 48, 60 }.Select(f =>
                    Choice($"{f:0.###} fps", () => { _seqFps = f; _ = ShowCurrent(); }, Math.Abs(_seqFps - f) < 0.001))));
            var tracks = v.Tracks();
            foreach (var (type, prop, title, glyph) in new[] { ("audio", "aid", "Audio track", ""), ("sub", "sid", "Subtitles", "") })
            {
                var t = tracks.Where(x => x.Type == type).ToList();
                if (t.Count == 0 || (type == "audio" && t.Count < 2)) continue;
                items.Add(Sub(glyph, title, t.Select(x => Choice(x.Label, () => v.Command("set", prop, x.Id.ToString()), x.Selected))
                    .Append(Choice("Off", () => v.Command("set", prop, "no"), !t.Any(x => x.Selected)))));
            }
            items.Add(Item("", "Save frame", v.Screenshot, "S"));
            items.Add(Item("", "Open saved frames folder", () => Explorer($"\"{VideoView.ScreenshotDir}\"")));
        }
        else if (_kind == Kind.Image)
        {
            Group(items);
            items.Add(Item("", "Fit to window", ResetView, "D0"));
            items.Add(Item("", "Actual size", _image.OneToOne, "H"));
            items.Add(Item("\uE7AD", "Rotate right", () => _image.Rotate(1), "R"));
            items.Add(Item("\uE7AD", "Rotate left", () => _image.Rotate(-1), "Shift+R"));
            items.Add(Item("\uE8AB", "Flip horizontal", _image.FlipHorizontal, "Shift+F"));
            items.Add(Item("\uE8AB", "Flip vertical", _image.FlipVertical));
            items.Add(Sub("\uE8A9", "Compare", CompareItems()));
            items.Add(Item("\uE790", "Adjust image\u2026", () => TogglePanel(_imagePanel.View), "E", _imagePanel.View.IsVisible || _image.Adjusted));
        }
        else if (_kind == Kind.Model)
        {
            Group(items);
            items.Add(Item("", "Reset view", ResetView, "D0"));
            items.Add(Item("\uE71E", _model!.Selected != null ? "Frame selected" : "Frame all", _model.FocusSelected, "F"));
            items.Add(Item("\uE8FD", "Outliner", () => TogglePanel(_outliner.View), "O", _outliner.View.IsVisible));
            if (_model!.IsAnimated) items.Add(Item(_model.Paused ? "" : "", _model.Paused ? "Play" : "Pause", _model.TogglePause, "Space"));
            items.Add(Item("", "Wireframe", _model!.ToggleWireframe, "W", _model.Wireframe));
            items.Add(Item("", "Grid", _model.ToggleGrid, "G", _model.ShowGrid));
            items.Add(Item("\uF158", "Geometry", _model.ToggleGeometry, "M", _model.ShowGeometry));
            if (_model.HasSkeleton) items.Add(Item("\uE805", "Skeleton", _model.ToggleSkeleton, "B", _model.ShowSkeleton));
            items.Add(Item("", "Textures and materials", _model.ToggleTextures, "T", _model.Textures));
        }

        Group(items);
        if (_queue.Count > 1)
        {
            items.Add(Item("", "Previous", () => Step(-1), "PageUp"));
            items.Add(Item("", "Next", () => Step(1), "PageDown"));
        }
        items.Add(Item("", "Show queue", () => TogglePanel(_queuePanel.View), "Q", _queuePanel.View.IsVisible));
        items.Add(Item("", "Add files to queue…", () => AddFilesDialog()));
        items.Add(Item("\uE8FD", "Play queue", TogglePlayQueue, "A", _playQueue));

        Group(items);
        items.Add(Item("", "Info", () => TogglePanel(_info.View), "I", _info.View.IsVisible));
        items.Add(Item("", "Open file…", OpenDialog, "Ctrl+O"));
        items.Add(Item("", "Set as default viewer…", () => { Registration.Register(); Registration.OpenDefaultAppsSettings(); }));
        if (entry != null)
        {
            items.Add(Item("", "Show in Explorer", () => Explorer($"/select,\"{entry.Path}\"")));
            items.Add(Item("", "Copy path", () => Clipboard?.SetTextAsync(entry.Path)));
        }

        Group(items);
        items.Add(Item("\uE765", "Keyboard shortcuts", () => _keysView.IsVisible = true, "F1"));
        items.Add(Item("", "Fullscreen", ToggleFullscreen, "F11", WindowState == WindowState.FullScreen));
        items.Add(Item("", "Close", Close, "Escape"));

        var menu = new ContextMenu { ItemsSource = items };
        RenderOptions.SetTextRenderingMode(menu, TextRenderingMode.Antialias);
        menu.Opened += (_, _) => _menuOpen = true;
        menu.Closed += (_, _) => { _menuOpen = false; Poke(); };
        menu.Open(this);
    }

    // ---- Info ----

    async void RefreshInfo()
    {
        if (!_info.View.IsVisible || Current is not { } entry) return;
        _infoAt = DateTime.Now;
        var id = ++_infoId;
        var files = entry.Frames ?? [entry.Path];
        var fi = new FileInfo(entry.Path);
        var sections = new List<(string, List<(string, string)>)>
        {
            ("File", [
                ("Name", entry.Name),
                ("Folder", fi.DirectoryName ?? ""),
                ("Size", Media.Size(files.Sum(f => new FileInfo(f).Length))),
                ("Modified", fi.LastWriteTime.ToString("yyyy-MM-dd HH:mm")),
                ("Created", fi.CreationTime.ToString("yyyy-MM-dd HH:mm")),
            ]),
        };
        if (_kind == Kind.Image) sections.AddRange(await Task.Run(() => ImageInfo(entry.Path)));
        else if (_kind == Kind.Video && _video != null) sections.AddRange(VideoInfo(_video, entry));
        else if (_kind == Kind.Model && _model != null) sections.Add(("3D model", _model.Info));
        if (id == _infoId) _info.Show(sections);
    }

    static List<(string, List<(string, string)>)> ImageInfo(string path)
    {
        using var img = new MagickImage();
        img.Ping(path);
        var rows = new List<(string, string)>
        {
            ("Dimensions", $"{img.Width} × {img.Height} px  ({img.Width * img.Height / 1e6:0.#} MP)"),
            ("Format", $"{img.Format}"),
            ("Color space", $"{img.ColorSpace}"),
            ("Bit depth", img.ChannelCount > 0 ? $"{img.Depth} bit × {img.ChannelCount} channels" : $"{img.Depth} bit"),
            ("Compression", $"{img.Compression}"),
        };
        if (img.Density.X > 0) rows.Add(("Resolution", $"{img.Density.X:0} × {img.Density.Y:0} {img.Density.Units}"));
        var result = new List<(string, List<(string, string)>)> { ("Image", rows) };
        if (img.GetExifProfile() is { } exif)
        {
            // The values photographers look for first, then everything else.
            string[] first = ["Make", "Model", "LensModel", "FNumber", "ExposureTime", "ISOSpeedRatings", "FocalLength", "DateTimeOriginal"];
            var tags = exif.Values
                .Select(x => (Key: x.Tag.ToString(), Value: Exif(x.GetValue())))
                .Where(x => x.Value != null)
                .OrderBy(x => Array.IndexOf(first, x.Key) is var i and >= 0 ? i : first.Length)
                .Select(x => (x.Key, x.Value!))
                .ToList();
            result.Add(("EXIF", tags));
        }
        return result;
    }

    static string? Exif(object? value) => value switch
    {
        null => null,
        byte[] b => b.Length <= 16 ? BitConverter.ToString(b) : null, // skip maker notes and other blobs
        Rational r => r.Denominator is 0 or 1 ? $"{r.Numerator}" : r.Numerator < r.Denominator ? $"{r.Numerator}/{r.Denominator}" : $"{r.ToDouble():0.##}",
        SignedRational r => $"{r.ToDouble():0.##}",
        Array a => string.Join(", ", a.Cast<object>().Select(Exif)),
        _ => value.ToString(),
    };

    static List<(string, List<(string, string)>)> VideoInfo(VideoView v, Entry entry)
    {
        string S(string p) => v.GetString(p);
        var w = S("width");
        var video = new List<(string, string)>();
        if (entry.IsSequence)
        {
            video.Add(("Frames", $"{entry.Frames!.Length}"));
            video.Add(("Range", $"{entry.FirstFrame}–{entry.FirstFrame + entry.Frames.Length - 1}"));
        }
        if (w != "")
        {
            video.Add(("Codec", S("video-format")));
            video.Add(("Resolution", $"{w} × {S("height")}"));
            video.Add(("Frame rate", $"{v.Fps:0.###} fps"));
            video.Add(("Pixel format", S("video-params/pixelformat")));
            video.Add(("Color", $"{S("video-params/primaries")}, {S("video-params/gamma")}, {S("video-params/colormatrix")}"));
            if (v.Get("video-bitrate") is > 0 and var vb) video.Add(("Bitrate", $"{vb / 1000:0} kbit/s"));
            video.Add(("Decoding", S("hwdec-current") is "" or "no" ? "Software" : $"Hardware ({S("hwdec-current")})"));
        }
        var audio = new List<(string, string)>();
        if (S("audio-codec-name") is { Length: > 0 } ac)
        {
            audio.Add(("Codec", ac));
            audio.Add(("Sample rate", $"{S("audio-params/samplerate")} Hz"));
            audio.Add(("Channels", $"{S("audio-params/channel-count")}  ({S("audio-params/hr-channels")})"));
            if (v.Get("audio-bitrate") is > 0 and var ab) audio.Add(("Bitrate", $"{ab / 1000:0} kbit/s"));
        }
        var general = new List<(string, string)>
        {
            ("Container", S("file-format")),
            ("Duration", TimeSpan.FromSeconds(v.Get("duration")).ToString(@"h\:mm\:ss\.fff")),
        };
        var tracks = v.Tracks().Select(t => ($"{t.Type} #{t.Id}", t.Label + (t.Selected ? "  (selected)" : ""))).ToList();
        var meta = new List<(string, string)>();
        for (int i = 0, n = (int)v.Get("metadata/list/count"); i < n; i++)
            meta.Add((S($"metadata/list/{i}/key"), S($"metadata/list/{i}/value")));
        return [("Media", general), (entry.IsSequence ? "Sequence" : "Video", video), ("Audio", audio), ("Tracks", tracks), ("Metadata", meta)];
    }

    // ---- Window ----

    async void OpenDialog()
    {
        var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true });
        var paths = r.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
        if (paths.Count == 1) Open(paths[0]);
        else if (paths.Count > 1) ReplaceQueue(paths);
    }

    async Task AddFilesDialog()
    {
        var r = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = true, Title = "Add to queue" });
        AddToQueue(r.Select(f => f.TryGetLocalPath()).OfType<string>());
    }

    void ToggleMaximize()
    {
        // From fullscreen, the maximize button means "leave fullscreen", not "maximize".
        if (WindowState == WindowState.FullScreen) ToggleFullscreen();
        else WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    void ToggleFullscreen()
    {
        if (WindowState == WindowState.FullScreen) WindowState = _beforeFullscreen;
        else { _beforeFullscreen = WindowState; WindowState = WindowState.FullScreen; }
    }

    void Poke()
    {
        _overlay.Opacity = 1;
        _overlay.IsHitTestVisible = true;
        Cursor = Cursor.Default;
        _hide.Stop();
        _hide.Start();
        if (Player != null) _bottom.Update();
    }

    void HideOverlay()
    {
        if (_queue.Count == 0 || _menuOpen || IsAudio || _top.IsPointerOver || _bottom.IsPointerOver || _zoomBar.IsPointerOver) return;
        _hide.Stop();
        _overlay.Opacity = 0;
        _overlay.IsHitTestVisible = false;
        // Keep the cursor while it's over an open side panel, and while inspecting an image.
        if (!_imagePanel.View.IsVisible && !_queuePanel.View.IsPointerOver && !_outliner.View.IsPointerOver && !_info.View.IsPointerOver && !_imagePanel.View.IsPointerOver) Cursor = new Cursor(StandardCursorType.None);
    }
}
