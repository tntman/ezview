using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using MagickImage = ImageMagick.MagickImage;
using OrientationType = ImageMagick.OrientationType;

namespace EzView;

// Full-window grid of everything in the queue (the current folder), with thumbnails for images and
// sequences. Click a tile to open it; Esc, G or the X closes the gallery.
class Gallery
{
    public readonly Panel View;
    readonly WrapPanel _tiles = new() { HorizontalAlignment = HorizontalAlignment.Center, Margin = new(12, 0, 12, 24) };
    readonly ScrollViewer _scroll;
    readonly TextBlock _title = new() { FontSize = 15, FontWeight = FontWeight.Bold, Foreground = Ui.Text, VerticalAlignment = VerticalAlignment.Center };
    readonly TextBlock _count = new() { FontSize = 12.5, Foreground = Ui.Muted, Margin = new(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
    readonly Dictionary<string, Bitmap?> _thumbs = new(StringComparer.OrdinalIgnoreCase);
    readonly Action<int> _open;
    int _generation;
    string? _folder;
    public DateTime ClosedAt { get; private set; }
    // 3D thumbnails load whole models one at a time; they get their own lane so images never wait behind them.
    static readonly SemaphoreSlim ModelLane = new(1);
    const int ThumbSize = 320;

    public Gallery(Window window, Action<int> open)
    {
        _open = open;
        var close = new Button
        {
            Content = Ui.Icon("", 11), Width = 34, Height = 34, CornerRadius = new(8), Background = Brushes.Transparent, Foreground = Ui.Text,
            BorderThickness = new(0), Focusable = false, HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center,
        };
        close.Click += (_, _) => Hide();
        DockPanel.SetDock(close, Dock.Right);
        var header = new DockPanel
        {
            Height = 56, Margin = new(24, 0, 16, 0), Background = Brushes.Transparent,
            Children = { close, new StackPanel { Orientation = Orientation.Horizontal, Children = { _title, _count } } },
        };
        // The header covers the title bar, so it moves the window too.
        header.PointerPressed += (_, e) => { if (e.ClickCount == 1 && e.GetCurrentPoint(header).Properties.IsLeftButtonPressed) window.BeginMoveDrag(e); };
        _scroll = new ScrollViewer { Content = _tiles };
        var body = new DockPanel { Children = { header, _scroll } };
        DockPanel.SetDock(header, Dock.Top);
        View = new Panel { IsVisible = false, Background = Ui.Bg, Children = { body } };
    }

    public void Show(List<Entry> queue, int current)
    {
        var gen = ++_generation;
        var dir = queue.Count > 0 ? Path.GetDirectoryName(queue[0].Path) : null;
        // Thumbnails are kept while browsing one folder; a new folder starts a fresh cache.
        if (!string.Equals(dir, _folder, StringComparison.OrdinalIgnoreCase)) { _thumbs.Clear(); _folder = dir; }
        var folder = Path.GetFileName(dir);
        _title.Text = string.IsNullOrEmpty(folder) ? "Gallery" : folder;
        _count.Text = $"{queue.Count} {(queue.Count == 1 ? "file" : "files")}";
        _tiles.Children.Clear();
        var pending = new List<(Image Target, TextBlock Glyph, Entry Entry)>();
        Border? currentTile = null;
        for (int i = 0; i < queue.Count; i++)
        {
            var (tile, image, glyph) = Tile(queue[i], i, i == current);
            _tiles.Children.Add(tile);
            if (i == current) currentTile = tile;
            if (Thumbnailable(queue[i]))
            {
                if (_thumbs.TryGetValue(ThumbPath(queue[i]), out var cached)) SetThumb(image, glyph, cached);
                else pending.Add((image, glyph, queue[i]));
            }
        }
        View.IsVisible = true;
        if (currentTile != null) Avalonia.Threading.Dispatcher.UIThread.Post(() => currentTile.BringIntoView(), Avalonia.Threading.DispatcherPriority.Background);

        // Thumbnails load in the background, in queue order, a few at a time; reopening or closing stops the old batch.
        // ponytail: everything is decoded up front; visible-first loading if folders with thousands of images feel slow
        var gate = new SemaphoreSlim(Math.Max(2, Environment.ProcessorCount / 2));
        foreach (var (target, glyph, entry) in pending)
            _ = Task.Run(async () =>
            {
                var lane = Media.KindOf(ThumbPath(entry)) == Kind.Model ? ModelLane : gate;
                await lane.WaitAsync();
                try
                {
                    if (gen != _generation) return;
                    var key = ThumbPath(entry);
                    Bitmap? thumb = null;
                    try { thumb = Thumb(key); } catch { } // unreadable: keeps its icon
                    Avalonia.Threading.Dispatcher.UIThread.Post(() =>
                    {
                        _thumbs[key] = thumb;
                        if (gen == _generation) SetThumb(target, glyph, thumb);
                    });
                }
                finally { lane.Release(); }
            });
    }

    public void Hide()
    {
        if (View.IsVisible) ClosedAt = DateTime.Now;
        _generation++;
        View.IsVisible = false;
    }

    static bool Thumbnailable(Entry e) => true; // images ourselves, everything else via Windows
    static string ThumbPath(Entry e) => e.IsSequence ? e.Frames![0] : e.Path;

    static void SetThumb(Image target, TextBlock glyph, Bitmap? thumb)
    {
        target.Source = thumb;
        glyph.IsVisible = thumb == null;
    }

    (Border, Image, TextBlock) Tile(Entry entry, int index, bool isCurrent)
    {
        var image = new Image { Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.HighQuality);
        var glyph = Ui.Icon(entry.Glyph, 30);
        glyph.Foreground = Ui.Muted;
        glyph.HorizontalAlignment = HorizontalAlignment.Center;
        var name = new TextBlock
        {
            Text = entry.Name, FontSize = 12, Foreground = isCurrent ? Ui.Accent : Ui.Text, TextTrimming = TextTrimming.CharacterEllipsis,
            Margin = new(2, 7, 2, 0), HorizontalAlignment = HorizontalAlignment.Center,
        };
        DockPanel.SetDock(name, Dock.Bottom);
        var tile = new Border
        {
            Width = 208, Height = 186, Margin = new(6), Padding = new(6), CornerRadius = new(10),
            Background = Brushes.Transparent, BorderBrush = isCurrent ? Ui.Accent : Brushes.Transparent, BorderThickness = new(1.5),
            Cursor = new Cursor(StandardCursorType.Hand),
            Child = new DockPanel
            {
                Children =
                {
                    name,
                    new Border { CornerRadius = new(6), ClipToBounds = true, Background = Ui.Panel, Child = new Panel { Children = { glyph, image } } },
                },
            },
        };
        ToolTip.SetTip(tile, entry.Name);
        tile.PointerEntered += (_, _) => tile.Background = Ui.Hover;
        tile.PointerExited += (_, _) => tile.Background = Brushes.Transparent;
        tile.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(tile).Properties.IsLeftButtonPressed) return;
            e.Handled = true;
            Hide();
            _open(index);
        };
        return (tile, image, glyph);
    }

    // Small decode: Skia scales common formats while decoding; everything else goes through ImageMagick
    // (including the view transform for EXR/HDR, so thumbnails match the viewer).
    static Bitmap? Thumb(string path)
    {
        if (Media.KindOf(path) == Kind.Model)
            return ModelView.Thumbnail(path, ThumbSize, ThumbSize * 3 / 4) is { } px ? ImageView.ToBitmap(px, ThumbSize, ThumbSize * 3 / 4) : null;
        if (Media.KindOf(path) is not (Kind.Image or Kind.None)) return ShellThumbnail.Get(path, ThumbSize); // video, gif: Explorer's thumbnail
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".jpg" or ".jpeg" or ".png" or ".webp" or ".bmp")
        {
            bool upright = true;
            if (ext is ".jpg" or ".jpeg")
            {
                using var info = new MagickImage();
                info.Ping(path);
                upright = info.Orientation is OrientationType.TopLeft or OrientationType.Undefined;
            }
            if (upright)
            {
                using var stream = File.OpenRead(path);
                return Bitmap.DecodeToWidth(stream, ThumbSize, BitmapInterpolationMode.MediumQuality);
            }
        }
        int w, h;
        using (var info = new MagickImage())
        {
            info.Ping(path);
            double k = Math.Min(1, (double)ThumbSize / Math.Max(info.Width, info.Height));
            (w, h) = (Math.Max(1, (int)(info.Width * k)), Math.Max(1, (int)(info.Height * k)));
        }
        var src = ImageView.ReadFile(path, w, h);
        return ImageView.ToBitmap(src.Bgra, src.W, src.H);
    }
}
