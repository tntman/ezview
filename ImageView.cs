using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Rendering;
using ImageMagick;
using Vector4 = System.Numerics.Vector4;

namespace EzView;

enum CompareMode { Wipe, Flip, Difference }

// The pixel under the pointer: its stored value (0–1 for 8-bit files, raw floats for deep ones) and its on-screen color.
readonly record struct PixelInfo(int X, int Y, Vector4 Value, byte R, byte G, byte B, byte A, bool Deep);

// Pixels for inspection, adjustments and comparison. Deep files (EXR, HDR, 16-bit) keep float RGBA as stored (linear for
// EXR/HDR), so exposure can bring back highlights. Bgra is always the unadjusted 8-bit display image.
sealed record Source(byte[] Bgra, float[]? Rgba, bool Linear, int W, int H);

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

    // Neighbours in the queue are decoded ahead, so stepping through a folder shows the next image at once.
    readonly Dictionary<string, Task<(Bitmap, Source?)>> _ahead = new(StringComparer.OrdinalIgnoreCase);

    public void Prefetch(IEnumerable<string> paths)
    {
        // ponytail: files over 64 MB are skipped, they'd hold hundreds of MB decoded; a size-aware budget if that matters
        var want = paths.Where(p => !string.Equals(p, _path, StringComparison.OrdinalIgnoreCase) && new FileInfo(p).Length < 64 << 20)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var (p, t) in _ahead.Where(kv => !want.Contains(kv.Key)).ToList())
        {
            _ahead.Remove(p);
            _ = t.ContinueWith(x => { if (x.IsCompletedSuccessfully) x.Result.Item1.Dispose(); });
        }
        foreach (var p in want)
            if (!_ahead.ContainsKey(p)) _ahead[p] = Task.Run(() => Decode(p));
    }

    public async Task Load(string path)
    {
        var id = ++_loadId;
        var full = _ahead.Remove(path, out var ready) ? ready : Task.Run(() => Decode(path));
        // Big photos: a quick screen-sized decode first, swapped for the full image when it's ready.
        double previewWidth = 0;
        if (!full.IsCompleted && await Task.Run(() => Preview(path)) is { } preview)
        {
            if (id != _loadId || full.IsCompleted) preview.Dispose();
            else
            {
                Show(preview, path, null);
                previewWidth = preview.PixelSize.Width;
                Fit();
            }
        }
        Bitmap bmp;
        Source? src;
        try { (bmp, src) = await full; }
        catch when (id != _loadId) { return; }
        if (id != _loadId) { bmp.Dispose(); return; }
        Source? b = null;
        if (_bPath != null && _bPath != path)
        {
            var (bp, size) = (_bPath, bmp.PixelSize);
            try { b = await Task.Run(() => ReadFile(bp, size.Width, size.Height)); } catch { }
            if (id != _loadId) { bmp.Dispose(); return; }
        }
        bool zoomedPreview = previewWidth > 0 && !_fit;
        if (zoomedPreview) _zoom *= previewWidth / bmp.PixelSize.Width; // same view, now in full-size pixels
        Show(bmp, path, src);
        _b = b;
        // Settings carry over, so images can be compared the same way.
        if (Adjusted || Inspecting || CompareActive) _ = Rebuild();
        else DropCompare();
        CompareChanged?.Invoke();
        if (zoomedPreview) Redraw();
        else Fit();
    }

    void Show(Bitmap bmp, string path, Source? src)
    {
        if (_path != path) (_rot, _flip) = (0, false);
        _bmp?.Dispose();
        _bmp = bmp;
        _path = path;
        _src = src;
        _shown?.Dispose();
        _shown = null;
        _shownBytes = null;
    }

    // Screen-sized decode of a large Skia-readable file (null when not worth it or EXIF-rotated).
    static Bitmap? Preview(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        if (!Fast.Contains(ext) || new FileInfo(path).Length < 1 << 20) return null;
        using (var info = new MagickImage())
        {
            info.Ping(path); // header only
            if (info.Width * info.Height < 16_000_000 || info.Orientation is not (OrientationType.TopLeft or OrientationType.Undefined)) return null;
        }
        using var stream = File.OpenRead(path);
        return Bitmap.DecodeToWidth(stream, 2560, BitmapInterpolationMode.MediumQuality);
    }

    // ---- Inspection: channel view, exposure and gamma, histogram and a pixel readout.
    public int Channel { get; private set; }      // 0 RGB, 1 R, 2 G, 3 B, 4 A, 5 luminance
    public double Exposure { get; private set; }  // stops, applied in linear light
    public double Gamma { get; private set; } = 1;
    public bool Raw { get; private set; }         // view transform off: linear files shown as stored (for EXRs written with sRGB values)
    public bool Adjusted => Channel != 0 || Exposure != 0 || Gamma != 1 || Raw;
    bool _inspecting;
    public bool Inspecting                                        // pixel readout and histogram wanted (panel open)
    {
        get => _inspecting;
        set
        {
            _inspecting = value;
            if (!value) _loupe.Hide();
            else _ = Rebuild();
        }
    }
    public event Action<PixelInfo>? PixelHovered, PixelClicked;
    public event Action? PixelLeft;
    public event Action<int[]>? HistogramChanged; // 3 × 256 bins (R, G, B) of what is shown

    string? _path;
    Source? _src;            // decoded on demand for Skia-decoded files
    int _adjustId;
    WriteableBitmap? _shown; // adjusted image, null when unadjusted
    byte[]? _shownBytes;

    public void SetAdjust(int channel, double exposure, double gamma)
    {
        (Channel, Exposure, Gamma) = (channel, exposure, gamma);
        _ = Rebuild();
    }

    public void SetRaw(bool raw)
    {
        Raw = raw;
        _ = Rebuild();
    }

    async Task<bool> EnsureSource()
    {
        if (_src != null) return true;
        if (_path is not { } path) return false;
        var id = _loadId;
        var src = await Task.Run(() => ReadFile(path, null, null));
        if (id != _loadId) return false;
        _src = src;
        return true;
    }

    // Applies the adjustments, then refreshes the histogram (panel open) and the compare image.
    async Task Rebuild()
    {
        var id = ++_adjustId;
        var (ch, ex, ga, raw) = (Channel, Exposure, Gamma, Raw);
        byte[]? bytes = null;
        if (Adjusted)
        {
            if (!await EnsureSource() || id != _adjustId) return;
            var src = _src!;
            bytes = await Task.Run(() => Adjust(src, ch, ex, ga, raw));
            if (id != _adjustId) return;
            _shown?.Dispose();
            _shown = ToBitmap(bytes, src.W, src.H);
        }
        else { _shown?.Dispose(); _shown = null; }
        _shownBytes = bytes;
        Redraw();

        if (Inspecting && await EnsureSource() && id == _adjustId)
        {
            var shown = bytes ?? _src!.Bgra;
            var hist = await Task.Run(() => Histogram(shown));
            if (id != _adjustId) return;
            HistogramChanged?.Invoke(hist);
        }

        if (!CompareActive) { DropCompare(); return; }
        var b = _b!;
        var bBytes = Adjusted ? await Task.Run(() => Adjust(b, ch, ex, ga, raw)) : b.Bgra;
        if (id != _adjustId) return;
        DropCompare();
        _bShown = ToBitmap(bBytes, b.W, b.H);
        if (CompareMode == CompareMode.Difference && await EnsureSource() && id == _adjustId)
        {
            var a = _shownBytes ?? _src!.Bgra;
            if (a.Length == bBytes.Length)
            {
                var d = await Task.Run(() => Diff(a, bBytes));
                if (id != _adjustId) return;
                _diff = ToBitmap(d, b.W, b.H);
            }
        }
        Redraw();
    }

    static float ToLinear(float x) => x <= 0.04045f ? x / 12.92f : MathF.Pow((x + 0.055f) / 1.055f, 2.4f);
    static float ToSrgb(float l) => l <= 0.0031308f ? l * 12.92f : 1.055f * MathF.Pow(l, 1 / 2.4f) - 0.055f;

    // Display bytes (BGRA) for a source with a channel view, exposure (stops, linear light) and display gamma.
    // raw: no linear -> sRGB view transform, stored values are shown as they are.
    internal static unsafe byte[] Adjust(Source s, int channel, double exposure, double gamma, bool raw = false)
    {
        var dst = new byte[s.W * s.H * 4];
        float gain = (float)Math.Pow(2, exposure), invGamma = (float)(1 / gamma);
        if (s.Rgba == null)
        {
            var lut = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                float x = i / 255f;
                if (exposure != 0) x = ToSrgb(Math.Min(1, ToLinear(x) * gain));
                lut[i] = (byte)Math.Round(MathF.Pow(x, invGamma) * 255);
            }
            var src = s.Bgra;
            Parallel.For(0, s.H, y =>
            {
                for (int i = y * s.W * 4, end = i + s.W * 4; i < end; i += 4)
                {
                    byte b = src[i], g = src[i + 1], r = src[i + 2], a = src[i + 3];
                    if (channel == 0) { dst[i] = lut[b]; dst[i + 1] = lut[g]; dst[i + 2] = lut[r]; dst[i + 3] = a; continue; }
                    byte v = channel switch
                    {
                        1 => lut[r], 2 => lut[g], 3 => lut[b], 4 => a,
                        _ => lut[(int)(0.2126 * r + 0.7152 * g + 0.0722 * b)],
                    };
                    dst[i] = dst[i + 1] = dst[i + 2] = v;
                    dst[i + 3] = 255; // single channels are shown opaque, as gray
                }
            });
            return dst;
        }

        // Deep: linear value -> clamp -> sRGB -> display gamma, through a fine LUT.
        const int N = 16384;
        var enc = new byte[N];
        for (int i = 0; i < N; i++) enc[i] = (byte)Math.Round(MathF.Pow(ToSrgb(i / (float)(N - 1)), invGamma) * 255);
        var f = s.Rgba;
        bool linear = s.Linear && !raw;
        Parallel.For(0, s.H, y =>
        {
            float L(float v) => (linear ? v : ToLinear(Math.Max(0, v))) * gain;
            byte E(float l) => enc[(int)(Math.Clamp(l, 0, 1) * (N - 1) + 0.5f)];
            for (int p = y * s.W, end = p + s.W; p < end; p++)
            {
                int i = p * 4;
                float r = L(f[i]), g = L(f[i + 1]), b = L(f[i + 2]), a = f[i + 3];
                if (channel == 0) { dst[i] = E(b); dst[i + 1] = E(g); dst[i + 2] = E(r); dst[i + 3] = (byte)(Math.Clamp(a, 0, 1) * 255 + 0.5f); continue; }
                byte v = channel switch
                {
                    1 => E(r), 2 => E(g), 3 => E(b), 4 => (byte)(Math.Clamp(a, 0, 1) * 255 + 0.5f),
                    _ => E(0.2126f * r + 0.7152f * g + 0.0722f * b),
                };
                dst[i] = dst[i + 1] = dst[i + 2] = v;
                dst[i + 3] = 255;
            }
        });
        return dst;
    }

    static int[] Histogram(byte[] bgra)
    {
        var h = new int[768];
        int n = bgra.Length / 4, step = Math.Max(1, n / 2_000_000); // sample big images
        for (int p = 0; p < n; p += step)
        {
            int i = p * 4;
            if (bgra[i + 3] == 0) continue;
            h[bgra[i + 2]]++;
            h[256 + bgra[i + 1]]++;
            h[512 + bgra[i]]++;
        }
        return h;
    }

    // |A − B| per channel, amplified ×4 so small differences show.
    static byte[] Diff(byte[] a, byte[] b)
    {
        var d = new byte[a.Length];
        Parallel.For(0, a.Length / 4, p =>
        {
            int i = p * 4;
            for (int c = 0; c < 3; c++) d[i + c] = (byte)Math.Min(255, Math.Abs(a[i + c] - b[i + c]) * 4);
            d[i + 3] = 255;
        });
        return d;
    }

    internal static WriteableBitmap ToBitmap(byte[] bgra, int w, int h)
    {
        var wb = new WriteableBitmap(new PixelSize(w, h), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
        using var fb = wb.Lock();
        for (int y = 0; y < h; y++) Marshal.Copy(bgra, y * w * 4, fb.Address + y * fb.RowBytes, w * 4);
        return wb;
    }

    static (Bitmap, Source?) Decode(string path)
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
            if (orientation is OrientationType.TopLeft or OrientationType.Undefined) return (new Bitmap(path), null);
        }
        // ponytail: whole image decoded into memory, add tiling when gigapixel images matter
        var src = ReadFile(path, null, null);
        return (ToBitmap(src.Bgra, src.W, src.H), src);
    }

    // Decode with ImageMagick (EXIF orientation applied), optionally resampled to a size (the compare image).
    internal static Source ReadFile(string path, int? w, int? h)
    {
        using var img = new MagickImage(path);
        img.AutoOrient();
        if (w is { } tw && h is { } th && (img.Width != tw || img.Height != th))
            img.Resize(new MagickGeometry((uint)tw, (uint)th) { IgnoreAspectRatio = true });
        int width = (int)img.Width, height = (int)img.Height;
        if (img.Depth > 8)
        {
            // EXR/HDR report gamma 1 (linear data); 16-bit PNG/TIFF stay sRGB-encoded.
            bool linear = Math.Abs(img.Gamma - 1) < 0.05 || img.ColorSpace is ColorSpace.RGB or ColorSpace.scRGB;
            img.ColorType = ColorType.TrueColorAlpha;
            float[]? f;
            using (var px = img.GetPixelsUnsafe()) f = px.ToArray();
            if (f != null && f.Length == width * height * 4)
            {
                float k = 1 / Quantum.Max;
                for (int i = 0; i < f.Length; i++) f[i] *= k;
                var deep = new Source([], f, linear, width, height);
                return deep with { Bgra = Adjust(deep, 0, 0, 1) };
            }
        }
        using var pixels = img.GetPixelsUnsafe();
        return new Source(pixels.ToByteArray(PixelMapping.BGRA)!, null, false, width, height);
    }

    // ---- View: zoom, pan, quarter-turn rotation and horizontal flip (view only, reset per image).
    int _rot;   // quarter turns clockwise
    bool _flip;

    public void Rotate(int quarterTurns) => Reorient(() => _rot = ((_rot + quarterTurns) % 4 + 4) % 4);
    public void FlipHorizontal() => Reorient(() => _flip = !_flip);
    public void FlipVertical() => Reorient(() => { _flip = !_flip; _rot = (_rot + 2) % 4; });

    void Reorient(Action change)
    {
        var (w, h) = ShownSize;
        var center = new Point(_x + w * _zoom / 2, _y + h * _zoom / 2);
        change();
        (w, h) = ShownSize;
        (_x, _y) = (center.X - w * _zoom / 2, center.Y - h * _zoom / 2);
        Redraw();
    }

    // Size of the image as displayed (rotation swaps width and height).
    (double W, double H) ShownSize => _bmp == null ? (0, 0)
        : _rot % 2 == 0 ? (_bmp.PixelSize.Width, _bmp.PixelSize.Height) : (_bmp.PixelSize.Height, _bmp.PixelSize.Width);

    // Image pixels -> view coordinates.
    Matrix ImageToView()
    {
        var s = _bmp!.PixelSize;
        var (w, h) = ShownSize;
        return Matrix.CreateTranslation(-s.Width / 2.0, -s.Height / 2.0) * Matrix.CreateScale(_flip ? -1 : 1, 1) *
               Matrix.CreateRotation(_rot * Math.PI / 2) * Matrix.CreateTranslation(w / 2, h / 2) *
               Matrix.CreateScale(_zoom, _zoom) * Matrix.CreateTranslation(_x, _y);
    }

    public void Fit()
    {
        _fit = true;
        ZoomChanged?.Invoke();
        Redraw();
    }

    // Zoom as a fraction of actual size (1 = one image pixel per screen pixel), for the zoom bar.
    public double Zoom => _zoom * Scale;
    public bool IsFit => _fit;
    public event Action? ZoomChanged;
    (double, bool) _reported;
    public void ZoomBy(double factor) => ZoomAt(new Point(Bounds.Width / 2, Bounds.Height / 2), Math.Clamp(_zoom * factor, 0.01, 64));

    // Actual size, centered in the view.
    public void OneToOne()
    {
        if (_bmp == null) return;
        _zoom = 1 / Scale;
        Center(Bounds.Size);
        _fit = false;
        Redraw();
    }

    void Center(Size view)
    {
        var (w, h) = ShownSize;
        _x = (view.Width - w * _zoom) / 2;
        _y = (view.Height - h * _zoom) / 2;
    }

    void ZoomAt(Point p, double zoom)
    {
        _x = p.X - (p.X - _x) * zoom / _zoom;
        _y = p.Y - (p.Y - _y) * zoom / _zoom;
        _zoom = zoom;
        _fit = false;
        Redraw();
    }

    // Dark 8 px checkerboard behind the image, so transparency is visible against the background.
    static IBrush? _checker;
    static IBrush Checker => _checker ??= new DrawingBrush
    {
        TileMode = TileMode.Tile, Stretch = Stretch.None,
        DestinationRect = new RelativeRect(0, 0, 16, 16, RelativeUnit.Absolute),
        Drawing = new DrawingGroup
        {
            Children =
            {
                new GeometryDrawing { Brush = new SolidColorBrush(Color.Parse("#1c1c1c")), Geometry = new RectangleGeometry(new Rect(0, 0, 16, 16)) },
                new GeometryDrawing
                {
                    Brush = new SolidColorBrush(Color.Parse("#2a2a2a")),
                    Geometry = new GeometryGroup { Children = { new RectangleGeometry(new Rect(0, 0, 8, 8)), new RectangleGeometry(new Rect(8, 8, 8, 8)) } },
                },
            },
        },
    };

    public override void Render(DrawingContext ctx) => DrawImage(ctx);

    void DrawImage(DrawingContext ctx)
    {
        if (_bmp == null) return;
        var (w, h) = ShownSize;
        if (_fit)
        {
            _zoom = Math.Min(1 / Scale, Math.Min(Bounds.Width / w, Bounds.Height / h));
            Center(Bounds.Size);
        }
        if ((Zoom, _fit) != _reported) { _reported = (Zoom, _fit); Avalonia.Threading.Dispatcher.UIThread.Post(() => ZoomChanged?.Invoke()); }
        // Crisp pixels when zoomed far in, smooth otherwise.
        var mode = _zoom * Scale >= 3 ? BitmapInterpolationMode.None : BitmapInterpolationMode.HighQuality;
        var view = new Rect(Bounds.Size);
        ctx.FillRectangle(Checker, new Rect(_x, _y, w * _zoom, h * _zoom).Intersect(view)); // shows through transparent pixels

        Bitmap a = _shown ?? _bmp;
        bool compare = CompareActive && _bShown != null;
        Bitmap main = !compare ? a : CompareMode switch
        {
            CompareMode.Flip when _showB => _bShown!,
            CompareMode.Difference => _diff ?? a,
            _ => a,
        };
        var imageRect = new Rect(0, 0, _bmp.PixelSize.Width, _bmp.PixelSize.Height);
        using (ctx.PushRenderOptions(new RenderOptions { BitmapInterpolationMode = mode }))
        {
            using (ctx.PushTransform(ImageToView())) ctx.DrawImage(main, new Rect(main.Size), imageRect);
            if (compare && CompareMode == CompareMode.Wipe)
            {
                double wx = WipeX;
                var right = new Rect(wx, 0, Math.Max(0, view.Width - wx), view.Height);
                using (ctx.PushClip(right))
                {
                    ctx.FillRectangle(Ui.Bg, right); // B's transparent pixels must not show A
                    ctx.FillRectangle(Checker, new Rect(_x, _y, w * _zoom, h * _zoom).Intersect(right));
                    using (ctx.PushTransform(ImageToView())) ctx.DrawImage(_bShown!, new Rect(_bShown!.Size), imageRect);
                }
            }
        }
        if (compare && CompareMode == CompareMode.Wipe)
        {
            double wx = WipeX;
            ctx.DrawLine(new Pen(new SolidColorBrush(Color.FromArgb(160, 0, 0, 0)), 3), new Point(wx, 0), new Point(wx, view.Height));
            ctx.DrawLine(new Pen(Brushes.White, 1), new Point(wx, 0), new Point(wx, view.Height));
            ctx.DrawEllipse(Ui.Panel, new Pen(Brushes.White, 1.5), new Point(wx, view.Height / 2), 9, 9);
            Label(ctx, "A", new Point(wx - 22, view.Height / 2 - 8));
            Label(ctx, "B", new Point(wx + 14, view.Height / 2 - 8));
        }
    }

    static void Label(DrawingContext ctx, string text, Point at)
    {
        var t = new FormattedText(text, System.Globalization.CultureInfo.InvariantCulture, FlowDirection.LeftToRight, new Typeface(FontFamily.Default, FontStyle.Normal, Avalonia.Media.FontWeight.Bold), 12, Brushes.White);
        ctx.DrawText(t, at);
    }

    protected override void OnSizeChanged(SizeChangedEventArgs e)
    {
        _drag = null; // a double-click resizing the window must not turn into a pan
        // Keep the same point in the middle of the view (a centered 1:1 image stays centered in fullscreen).
        if (!_fit && Math.Abs(Zoom - 1) < 1e-6 && _bmp != null) Center(e.NewSize); // 1:1 mode: re-center, even after panning
        else if (!_fit && e.PreviousSize.Width > 0)
        {
            _x += (e.NewSize.Width - e.PreviousSize.Width) / 2;
            _y += (e.NewSize.Height - e.PreviousSize.Height) / 2;
        }
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
    bool _wipeDrag;

    bool NearWipe(Point p) => CompareActive && CompareMode == CompareMode.Wipe && Math.Abs(p.X - WipeX) < 12;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsMiddleButtonPressed || e.ClickCount >= 2) return;
        var p = e.GetPosition(this);
        if (props.IsLeftButtonPressed && NearWipe(p)) _wipeDrag = true;
        else _drag = _pressAt = p;
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var p = e.GetPosition(this);
        Cursor = _wipeDrag || NearWipe(p) ? new Cursor(StandardCursorType.SizeWestEast) : null;
        if (_wipeDrag)
        {
            _wipe = Math.Clamp(p.X / Math.Max(1, Bounds.Width), 0, 1);
            Redraw();
            return;
        }
        if (Inspecting && _drag == null) Inspect(p);
        if (_drag is not { } d) return;
        _x += p.X - d.X;
        _y += p.Y - d.Y;
        _drag = p;
        _fit = false;
        Redraw();
    }

    // Pixel under the pointer, in image coordinates.
    (int X, int Y)? PixelAt(Point p)
    {
        if (_src == null || _bmp == null || !ImageToView().TryInvert(out var inv)) return null;
        var q = p.Transform(inv);
        int x = (int)Math.Floor(q.X), y = (int)Math.Floor(q.Y);
        return x < 0 || y < 0 || x >= _src.W || y >= _src.H ? null : (x, y);
    }

    PixelInfo Pixel(int x, int y)
    {
        var s = _src!;
        int i = (y * s.W + x) * 4;
        byte b = s.Bgra[i], g = s.Bgra[i + 1], r = s.Bgra[i + 2], a = s.Bgra[i + 3];
        var value = s.Rgba is { } f ? new Vector4(f[i], f[i + 1], f[i + 2], f[i + 3]) : new Vector4(r, g, b, a) / 255f;
        return new PixelInfo(x, y, value, r, g, b, a, s.Rgba != null);
    }

    void Inspect(Point p)
    {
        if (_bmp == null) return;
        if (_src == null) { _ = EnsureSource(); return; }
        if (PixelAt(p) is not var (x, y)) { PixelLeft?.Invoke(); _loupe.Hide(); return; }
        PixelHovered?.Invoke(Pixel(x, y));
        if (Loupe) _loupe.Show(p, x, y, _src.Bgra, _src.W, _src.H);
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
        if (!_wipeDrag && Inspecting && e.InitialPressMouseButton == MouseButton.Left && Math.Abs(p.X - _pressAt.X) + Math.Abs(p.Y - _pressAt.Y) < 4
            && PixelAt(p) is var (x, y))
            PixelClicked?.Invoke(Pixel(x, y));
        _drag = null;
        _wipeDrag = false;
        e.Pointer.Capture(null);
    }

    // ---- Compare: B is pinned (B key), then any other image is A. Wipe, flip (Tab) or difference.
    string? _bPath;
    Source? _b;                 // B, resampled to A's size
    Bitmap? _bShown, _diff;
    double _wipe = 0.5;
    bool _showB;
    public CompareMode CompareMode { get; private set; }
    public event Action? CompareChanged;
    public bool ComparePinned => _bPath != null;
    public bool CompareActive => _b != null && _bPath != _path;
    double WipeX => _wipe * Bounds.Width;

    public string? CompareLabel => !ComparePinned ? null
        : !CompareActive ? $"B: {Path.GetFileName(_bPath)}"
        : CompareMode switch { CompareMode.Wipe => "Wipe", CompareMode.Flip => _showB ? "Showing B" : "Showing A", _ => "Difference ×4" } + $"  ·  B: {Path.GetFileName(_bPath)}";

    // Pins the current image as B (again on B: unpins).
    public void PinCompare()
    {
        if (_path == null) return;
        if (_bPath == _path) { ClearCompare(); return; }
        _bPath = _path;
        _b = null;
        (CompareMode, _showB, _wipe) = (CompareMode.Wipe, false, 0.5);
        DropCompare();
        CompareChanged?.Invoke();
    }

    public void ClearCompare()
    {
        (_bPath, _b) = (null, null);
        DropCompare();
        CompareChanged?.Invoke();
    }

    public void SetCompareMode(CompareMode mode)
    {
        CompareMode = mode;
        _showB = false;
        CompareChanged?.Invoke();
        _ = Rebuild();
    }

    public void CycleCompare() => SetCompareMode((CompareMode)(((int)CompareMode + 1) % 3));

    // Tab: flip between A and B.
    public void SwapAB()
    {
        if (CompareMode != CompareMode.Flip) SetCompareMode(CompareMode.Flip);
        _showB = !_showB;
        CompareChanged?.Invoke();
        Redraw();
    }

    void DropCompare()
    {
        _bShown?.Dispose();
        _diff?.Dispose();
        (_bShown, _diff) = (null, null);
        Redraw();
    }

    public bool Loupe = true;
    readonly LoupeView _loupe = new() { IsHitTestVisible = false };

    // Magnifier next to the pointer: 11x11 source pixels, nearest-neighbour, grid and a marked center pixel.
    sealed class LoupeView : Control
    {
        const int N = 11, Cell = 14, Size = N * Cell;
        readonly WriteableBitmap _bmp = new(new PixelSize(N, N), new Avalonia.Vector(96, 96), PixelFormat.Bgra8888, AlphaFormat.Unpremul);
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
