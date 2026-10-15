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
        Fit();
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
            ctx.DrawImage(_bmp, new Rect(_bmp.Size), new Rect(_x, _y, s.Width * _zoom, s.Height * _zoom));
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

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        var props = e.GetCurrentPoint(this).Properties;
        if (!props.IsLeftButtonPressed && !props.IsMiddleButtonPressed) return;
        _drag = e.GetPosition(this);
        e.Pointer.Capture(this);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        if (_drag is not { } d) return;
        var p = e.GetPosition(this);
        _x += p.X - d.X;
        _y += p.Y - d.Y;
        _drag = p;
        _fit = false;
        Redraw();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _drag = null;
        e.Pointer.Capture(null);
    }
}
