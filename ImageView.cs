using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using ImageMagick;

namespace EzView;

class ImageView : Control, ICustomHitTest
{
    // Skia decodes these fastest; everything else goes through ImageMagick.
    static readonly HashSet<string> Fast = [".jpg", ".jpeg", ".png", ".webp", ".bmp"];

    Bitmap? _bmp;
    double _zoom = 1, _x, _y;
    bool _fit = true;
    Point? _drag;
    int _loadId;

    double Scale => VisualRoot?.RenderScaling ?? 1;
    public bool HitTest(Point p) => true;

    // Frosted glass under the bars: a blurred copy of the image, clipped to the bar strips.
    // ponytail: Skia re-blurs the whole layer on every pan/zoom frame; render a small pre-blurred bitmap if that ever lags
    sealed class BlurLayer(ImageView owner) : Control
    {
        public override void Render(DrawingContext ctx) => owner.DrawImage(ctx);
    }

    readonly BlurLayer _layer;
    double _top, _bottom;

    public ImageView()
    {
        _layer = new BlurLayer(this) { Effect = new BlurEffect { Radius = 40 }, IsHitTestVisible = false, IsVisible = false };
        VisualChildren.Add(_layer);
        VisualChildren.Add(_loupe);
    }

    public void SetStrips(double top, double bottom, double opacity)
    {
        (_top, _bottom) = (top, bottom);
        _layer.Opacity = opacity;
        _layer.IsVisible = opacity > 0.01 && top + bottom > 0;
        UpdateClip();
    }

    void UpdateClip()
    {
        double w = Bounds.Width, h = Bounds.Height;
        _layer.Clip = new GeometryGroup
        {
            Children = { new RectangleGeometry(new Rect(0, 0, w, _top)), new RectangleGeometry(new Rect(0, h - _bottom, w, _bottom)) },
        };
    }

    void Redraw()
    {
        InvalidateVisual();
        _layer.InvalidateVisual();
    }

    public async Task Load(string path)
    {
        var id = ++_loadId;
        var bmp = await Task.Run(() => Decode(path));
        if (id != _loadId) { bmp.Dispose(); return; }
        _bmp?.Dispose();
        _bmp = bmp;
        _path = path;
        _src = null;
        _shown?.Dispose();
        _shown = null;
        if (Adjusted) _ = Rebuild(); // settings carry over, so images can be compared the same way
        Fit();
    }

    // ---- Inspection: channel view, exposure and gamma (one 256-entry LUT), and a pixel readout.
    public int Channel { get; private set; }      // 0 RGB, 1 R, 2 G, 3 B, 4 A, 5 luminance
    public double Exposure { get; private set; }  // stops
    public double Gamma { get; private set; } = 1;
    public bool Adjusted => Channel != 0 || Exposure != 0 || Gamma != 1;
    public bool Inspecting;                                       // pixel readout wanted (panel open)
    public event Action<int, int, byte, byte, byte, byte>? PixelHovered; // x, y, r, g, b, a
    public event Action? PixelLeft;

    string? _path;
    byte[]? _src; // BGRA, straight alpha, decoded on demand
    int _sw, _sh, _adjustId;
    WriteableBitmap? _shown;

    public void SetAdjust(int channel, double exposure, double gamma)
    {
        (Channel, Exposure, Gamma) = (channel, exposure, gamma);
        _ = Rebuild();
    }

    async Task<bool> EnsureSource()
    {
        if (_src != null) return true;
        if (_path is not { } path) return false;
        var id = _loadId;
        var (px, w, h) = await Task.Run(() =>
        {
            // ponytail: 8-bit decode, so exposure can't recover HDR highlights; needs a Q16-HDRI Magick build for that
            using var img = new MagickImage(path);
            img.AutoOrient();
            return (img.ToByteArray(MagickFormat.Bgra), (int)img.Width, (int)img.Height);
        });
        if (id != _loadId) return false;
        (_src, _sw, _sh) = (px, w, h);
        return true;
    }

    async Task Rebuild()
    {
        var id = ++_adjustId;
        if (!Adjusted) { _shown?.Dispose(); _shown = null; Redraw(); return; }
        if (!await EnsureSource() || id != _adjustId) return;
        var (src, w, h, ch, ex, ga) = (_src!, _sw, _sh, Channel, Exposure, Gamma);
        var bmp = await Task.Run(() => Adjust(src, w, h, ch, ex, ga));
        if (id != _adjustId) { bmp.Dispose(); return; }
        _shown?.Dispose();
        _shown = bmp;
        Redraw();
    }

    static unsafe WriteableBitmap Adjust(byte[] src, int w, int h, int channel, double exposure, double gamma)
    {
        var lut = new byte[256];
        double gain = Math.Pow(2, exposure);
        for (int i = 0; i < 256; i++) lut[i] = (byte)Math.Round(Math.Pow(Math.Min(1, i / 255.0 * gain), 1 / gamma) * 255);
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        nint dst = fb.Address;
        int stride = fb.RowBytes;
        Parallel.For(0, h, y =>
        {
            byte* d = (byte*)dst + (long)y * stride;
            int s = y * w * 4;
            for (int x = 0; x < w; x++, s += 4, d += 4)
            {
                byte b = src[s], g = src[s + 1], r = src[s + 2], a = src[s + 3];
                if (channel == 0) { d[0] = lut[b]; d[1] = lut[g]; d[2] = lut[r]; d[3] = a; continue; }
                byte v = channel switch
                {
                    1 => lut[r], 2 => lut[g], 3 => lut[b], 4 => a,
                    _ => lut[(int)(0.2126 * r + 0.7152 * g + 0.0722 * b)],
                };
                d[0] = d[1] = d[2] = v;
                d[3] = 255; // single channels are shown opaque, as gray
            }
        });
        return wb;
    }

    static Bitmap Decode(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (Fast.Contains(ext))
        {
            var orientation = OrientationType.TopLeft;
            if (ext is ".jpg" or ".jpeg")
            {
                using var info = new MagickImage();
                info.Ping(path);
                orientation = info.Orientation;
            }
            if (orientation is OrientationType.TopLeft or OrientationType.Undefined) return new Bitmap(path);
        }
        // ponytail: whole image decoded into memory, add tiling when gigapixel images matter
        using var img = new MagickImage(path);
        img.AutoOrient();
        int w = (int)img.Width, h = (int)img.Height;
        var px = img.ToByteArray(MagickFormat.Bgra);
        var wb = new WriteableBitmap(new PixelSize(w, h), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using (var fb = wb.Lock())
            for (int y = 0; y < h; y++) Marshal.Copy(px, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        return wb;
    }

    public void Fit()
    {
        _fit = true;
        Redraw();
    }

    public void OneToOne() => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), 1 / Scale);

    void ZoomAt(Point p, double zoom)
    {
        _x = p.X - (p.X - _x) * zoom / _zoom;
        _y = p.Y - (p.Y - _y) * zoom / _zoom;
        _zoom = zoom;
        _fit = false;
        Redraw();
    }

    public override void Render(DrawingContext ctx) => DrawImage(ctx);

    void DrawImage(DrawingContext ctx)
    {
        if (_bmp == null) return;
        Bitmap shown = _shown ?? _bmp;
        var s = _bmp.PixelSize;
        if (_fit)
        {
            _zoom = Math.Min(1 / Scale, Math.Min(Bounds.Width / s.Width, Bounds.Height / s.Height));
            _x = (Bounds.Width - s.Width * _zoom) / 2;
            _y = (Bounds.Height - s.Height * _zoom) / 2;
        }
        // Crisp pixels when zoomed far in, smooth otherwise.
        var mode = _zoom * Scale >= 3 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
            ctx.DrawImage(shown, new Rect(shown.Size), new Rect(_x, _y, s.Width * _zoom, s.Height * _zoom));
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        UpdateClip();
        Redraw();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        if (_bmp == null) return;
        ZoomAt(e.GetPosition(this), Math.Clamp(_zoom * Math.Pow(1.2, e.Delta.Y), 0.01, 64));
        e.Handled = true;
    }

    Point _pressAt;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsMiddleButtonPressed) return;
        _drag = _pressAt = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (Inspecting && _drag == null) Inspect(e.GetPosition(this));
        if (_drag is not { } d) return;
        var p = e.GetPosition(this);
        _x += p.X - d.X;
        _y += p.Y - d.Y;
        _drag = p;
        _fit = false;
        Redraw();
    }

    // Pixel under the pointer (image coordinates: one image pixel spans _zoom DIPs).
    (int X, int Y)? PixelAt(Point p)
    {
        if (_src == null) return null;
        int x = (int)Math.Floor((p.X - _x) / _zoom), y = (int)Math.Floor((p.Y - _y) / _zoom);
        return x < 0 || y < 0 || x >= _sw || y >= _sh ? null : (x, y);
    }

    void Inspect(Point p)
    {
        if (_bmp == null) return;
        if (_src == null) { _ = EnsureSource(); return; }
        if (PixelAt(p) is not var (x, y)) { PixelLeft?.Invoke(); _loupe.Hide(); return; }
        int i = (y * _sw + x) * 4;
        PixelHovered?.Invoke(x, y, _src[i + 2], _src[i + 1], _src[i], _src[i + 3]);
        if (Loupe) _loupe.Show(p, x, y, _src, _sw, _sh);
        else _loupe.Hide();
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        PixelLeft?.Invoke();
        _loupe.Hide();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        // A left click that didn't pan picks the pixel (the panel locks/unlocks it).
        var p = e.GetPosition(this);
        if (Inspecting && e.InitialPressMouseButton == MouseButton.Left && Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) < 4
            && PixelAt(p) is var (x, y))
        {
            int i = (y * _sw + x) * 4;
            PixelClicked?.Invoke(x, y, _src![i + 2], _src[i + 1], _src[i], _src[i + 3]);
        }
        _drag = null;
        e.Pointer.Capture(null);
    }

    public event Action<int, int, byte, byte, byte, byte>? PixelClicked;
    public bool Loupe = true;
    readonly LoupeView _loupe = new() { IsHitTestVisible = false };

    // Magnifier next to the pointer: 11x11 source pixels, nearest-neighbour, grid and a marked center pixel.
    sealed class LoupeView : Control
    {
        const int N = 11, Cell = 14, Size = N * Cell;
        readonly WriteableBitmap _bmp = new(new PixelSize(N, N), new Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        Point _at;
        bool _shown;

        public unsafe void Show(Point at, int cx, int cy, byte[] src, int w, int h)
        {
            using (var fb = _bmp.Lock())
                for (int y = 0; y < N; y++)
                {
                    var row = (byte*)fb.Address + y * fb.RowBytes;
                    for (int x = 0; x < N; x++)
                    {
                        int sx = cx + x - N / 2, sy = cy + y - N / 2;
                        if (sx < 0 || sy < 0 || sx >= w || sy >= h) { row[x * 4] = row[x * 4 + 1] = row[x * 4 + 2] = 14; row[x * 4 + 3] = 255; continue; }
                        int i = (sy * w + sx) * 4;
                        row[x * 4] = src[i]; row[x * 4 + 1] = src[i + 1]; row[x * 4 + 2] = src[i + 2]; row[x * 4 + 3] = 255; // opaque, so alpha doesn't hide color
                    }
                }
            (_at, _shown) = (at, true);
            InvalidateVisual();
        }

        public void Hide()
        {
            if (!_shown) return;
            _shown = false;
            InvalidateVisual();
        }

        public override void Render(DrawingContext ctx)
        {
            if (!_shown) return;
            // Below-right of the pointer, flipped to stay inside the view.
            double x = _at.X + 24, y = _at.Y + 24;
            if (x + Size > Bounds.Width) x = _at.X - 24 - Size;
            if (y + Size > Bounds.Height) y = _at.Y - 24 - Size;
            var box = new Rect(x, y, Size, Size);
            var clip = new RoundedRect(box, 10);
            using (ctx.PushClip(clip))
            {
                using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = BitmapInterpolationMode.None }))
                    ctx.DrawImage(_bmp, new Rect(0, 0, N, N), box);
                var grid = new Pen(new SolidColorBrush(Color.FromArgb(40, 0, 0, 0)), 1);
                for (int i = 1; i < N; i++)
                {
                    ctx.DrawLine(grid, new Point(x + i * Cell, y), new Point(x + i * Cell, y + Size));
                    ctx.DrawLine(grid, new Point(x, y + i * Cell), new Point(x + Size, y + i * Cell));
                }
                var center = new Rect(x + N / 2 * Cell, y + N / 2 * Cell, Cell, Cell);
                ctx.DrawRectangle(null, new Pen(Brushes.Black, 3), center);
                ctx.DrawRectangle(null, new Pen(Ui.Accent, 1.5), center);
            }
            ctx.DrawRectangle(null, new Pen(Ui.Edge, 1), clip);
        }
    }
}
