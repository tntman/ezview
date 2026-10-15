using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Avalonia.Threading;
using SilkGL = Silk.NET.OpenGL.GL;

namespace EzView;

// libmpv through its render API: frames land in Avalonia's GL framebuffer, so the overlay can sit on top.
unsafe class VideoView : OpenGlControlBase, ICustomHitTest, IPlayback
{
    const string Lib = "libmpv-2";
    [DllImport(Lib)] static extern nint mpv_create();
    [DllImport(Lib)] static extern int mpv_initialize(nint h);
    [DllImport(Lib)] static extern int mpv_set_option_string(nint h, byte[] name, byte[] value);
    [DllImport(Lib)] static extern int mpv_command(nint h, nint* args);
    [DllImport(Lib)] static extern int mpv_get_property(nint h, byte[] name, int format, void* data);
    [DllImport(Lib)] static extern nint mpv_get_property_string(nint h, byte[] name);
    [DllImport(Lib)] static extern void mpv_free(nint data);
    [DllImport(Lib)] static extern int mpv_render_context_create(out nint ctx, nint h, RenderParam* p);
    [DllImport(Lib)] static extern void mpv_render_context_set_update_callback(nint ctx, nint cb, nint data);
    [DllImport(Lib)] static extern int mpv_render_context_render(nint ctx, RenderParam* p);
    [DllImport(Lib)] static extern void mpv_render_context_free(nint ctx);

    struct RenderParam { public int Type; public void* Data; }
    struct GlInitParams { public nint GetProcAddress, Ctx; }
    struct GlFbo { public int Fbo, W, H, InternalFormat; }
    delegate nint GetProcFn(nint ctx, nint name);
    delegate void UpdateFn(nint ctx);

    static byte[] U8(string s) => Encoding.UTF8.GetBytes(s + "\0");

    readonly nint _mpv;
    nint _ctx;
    GlInterface? _gl;
    // Kept in fields so the GC never collects delegates native code still calls.
    readonly GetProcFn _getProc;
    readonly UpdateFn _update;

    // ponytail: mpv is never terminated, process exit cleans it up
    public VideoView()
    {
        _mpv = mpv_create();
        foreach (var (k, v) in new[] {
            // ponytail: hw decode + copy to RAM, since ANGLE has no zero-copy interop with mpv; a native d3d11 swapchain avoids the copy if 8K ever stutters
            ("vo", "libmpv"), ("hwdec", "auto-copy-safe"), ("keep-open", "yes"), ("osd-level", "0"),
            ("input-default-bindings", "no"), ("volume", "25"), ("volume-max", "100"), ("load-scripts", "no"), ("ytdl", "no"), ("background-color", "#000000"),
            ("screenshot-directory", ScreenshotDir), ("screenshot-format", "png") })
            mpv_set_option_string(_mpv, U8(k), U8(v));
        mpv_initialize(_mpv);
        _getProc = (_, name) => _gl!.GetProcAddress(Marshal.PtrToStringAnsi(name)!);
        _update = _ => Dispatcher.UIThread.Post(RequestNextFrameRendering);
    }

    public bool HitTest(Point p) => true;

    public readonly GlBlur Blur = new();

    protected override void OnOpenGlInit(GlInterface gl)
    {
        _gl = gl;
        fixed (byte* api = U8("opengl"))
        {
            var init = new GlInitParams { GetProcAddress = Marshal.GetFunctionPointerForDelegate(_getProc) };
            var p = stackalloc RenderParam[] { new() { Type = 1, Data = api }, new() { Type = 2, Data = &init }, new() };
            mpv_render_context_create(out _ctx, _mpv, p);
        }
        mpv_render_context_set_update_callback(_ctx, Marshal.GetFunctionPointerForDelegate(_update), 0);
        Blur.Init(SilkGL.GetApi(gl.GetProcAddress), gl.ContextInfo.Version.Type == GlProfileType.OpenGLES);
        if (_pendingLoad is { } load) Dispatcher.UIThread.Post(load);
        _pendingLoad = null;
    }

    Action? _pendingLoad;

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (_ctx == 0) return;
        var s = VisualRoot?.RenderScaling ?? 1;
        var fbo = new GlFbo { Fbo = fb, W = (int)(Bounds.Width * s), H = (int)(Bounds.Height * s) };
        int flip = 1;
        var p = stackalloc RenderParam[] { new() { Type = 3, Data = &fbo }, new() { Type = 4, Data = &flip }, new() };
        mpv_render_context_render(_ctx, p);
        Blur.Apply(fb, fbo.W, fbo.H, s);
    }

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        Blur.Deinit();
        if (_ctx != 0) mpv_render_context_free(_ctx);
        _ctx = 0;
    }

    public void Command(params string[] args)
    {
        var ptrs = new nint[args.Length + 1];
        for (int i = 0; i < args.Length; i++) ptrs[i] = Marshal.StringToCoTaskMemUTF8(args[i]);
        fixed (nint* p = ptrs) mpv_command(_mpv, p);
        foreach (var x in ptrs) Marshal.FreeCoTaskMem(x);
    }

    public double Get(string name)
    {
        double v = 0;
        mpv_get_property(_mpv, U8(name), 5 /* MPV_FORMAT_DOUBLE */, &v);
        return v;
    }

    public string GetString(string name)
    {
        var p = mpv_get_property_string(_mpv, U8(name));
        if (p == 0) return "";
        var s = Marshal.PtrToStringUTF8(p) ?? "";
        mpv_free(p);
        return s;
    }

    public record Track(string Type, int Id, string Label, bool Selected);

    public List<Track> Tracks()
    {
        var list = new List<Track>();
        for (int i = 0, n = (int)Get("track-list/count"); i < n; i++)
        {
            var k = $"track-list/{i}/";
            var label = string.Join(" · ", new[] { GetString(k + "title"), GetString(k + "lang"), GetString(k + "codec") }.Where(s => s != ""));
            list.Add(new Track(GetString(k + "type"), (int)Get(k + "id"), label == "" ? $"Track {i + 1}" : label, GetFlag(k + "selected")));
        }
        return list;
    }

    public static readonly string ScreenshotDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "ezview");

    public void Screenshot()
    {
        Directory.CreateDirectory(ScreenshotDir);
        Command("screenshot", "video");
    }

    // Containers like mkv often have no fps field; fall back to the measured decoder rate.
    public double Fps => Get("container-fps") is > 0 and var f ? f : Get("estimated-vf-fps");

    // ---- IPlayback
    public int FrameOffset = 1; // first frame number: 1 for video, e.g. 1001 for an image sequence
    int[]? _numbers;            // real frame numbers of a sequence, so gaps show the right number
    public double Position => Get("time-pos");
    public double Duration => Get("duration");
    public bool Paused => GetFlag("pause");
    public int FirstFrame => FrameOffset;
    public int LastFrame => _numbers is { Length: > 0 } n ? n[^1] : FrameOffset + FrameCount - 1;

    public int FrameCount => (int)Get("estimated-frame-count") is > 0 and var n ? n
        : Fps > 0 && Duration > 0 ? (int)Math.Round(Duration * Fps) : 0;

    public int Frame
    {
        get
        {
            int f = Get("estimated-frame-count") > 0 ? (int)Get("estimated-frame-number") : (int)Math.Round(Position * Fps);
            f = Math.Clamp(f, 0, Math.Max(FrameCount, 1) - 1); // rounding can overshoot on the last frame
            return _numbers is { Length: > 0 } n ? n[Math.Min(f, n.Length - 1)] : f + FrameOffset;
        }
    }

    public void StepFrame(int delta) => Command(delta < 0 ? "frame-back-step" : "frame-step");
    public void SeekFraction(double fraction, bool exact) => SeekPercent(fraction, exact);

    public bool GetFlag(string name)
    {
        int v = 0;
        mpv_get_property(_mpv, U8(name), 3 /* MPV_FORMAT_FLAG */, &v);
        return v != 0;
    }

    static string Num(double d) => d.ToString(CultureInfo.InvariantCulture);

    // mpv's video output needs the render context, which only exists after the first GL frame.
    public void Load(string path, bool loop, int[]? numbers = null)
    {
        if (_ctx == 0) { _pendingLoad = () => Load(path, loop, numbers); RequestNextFrameRendering(); return; }
        _numbers = numbers;
        Command("set", "loop-file", loop ? "inf" : "no");
        Command("loadfile", path);
        Command("set", "pause", "no");
    }

    static readonly string SequenceList = Path.Combine(Path.GetTempPath(), "ezview-sequence.txt");

    // Image sequences play through mpv's mf:// demuxer from a list file, so every player feature works on them.
    // ponytail: no frame prefetch, 4K EXR may stutter; add a frame cache if it matters
    public void LoadSequence(IEnumerable<string> frames, double fps, bool loop)
    {
        var list = frames.ToList();
        File.WriteAllLines(SequenceList, list);
        Command("set", "mf-fps", Num(fps));
        var numbers = list.Select(Media.FrameNumber).ToList();
        Load("mf://@" + SequenceList, loop, numbers.All(n => n != null) ? numbers.Select(n => n!.Value).ToArray() : null);
    }

    public void Stop() => Command("stop");
    // Play after the end starts over instead of doing nothing.
    public void TogglePause()
    {
        if (GetFlag("eof-reached"))
        {
            Command("seek", "0", "absolute+exact");
            Command("set", "pause", "no");
        }
        else Command("cycle", "pause");
    }
    public void Seek(double seconds) => Command("seek", Num(seconds), "relative+exact");
    public void SeekPercent(double fraction, bool exact = true) =>
        Command("seek", Num(fraction * 100), exact ? "absolute-percent+exact" : "absolute-percent+keyframes");
    public void Volume(int delta) => Command("add", "volume", Num(delta));
    public void ToggleMute() => Command("cycle", "mute");
    public void SetVolume(double percent) => Command("set", "volume", Num(Math.Round(percent)));

    // A click toggles pause only once it's clearly not the start of a double-click (which means fullscreen),
    // so double-clicking never flickers playback off and on.
    readonly DispatcherTimer _clickTimer = new() { Interval = TimeSpan.FromMilliseconds(250) };

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        if (!e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        _clickTimer.Stop();
        if (e.ClickCount >= 2) return;
        _clickTimer.Tick -= OnSingleClick;
        _clickTimer.Tick += OnSingleClick;
        _clickTimer.Start();
    }

    // Called by the window when it sees the double-click, in case this view didn't get the second press.
    public void CancelClick() => _clickTimer.Stop();

    void OnSingleClick(object? sender, EventArgs e)
    {
        _clickTimer.Stop();
        TogglePause();
    }
}
