using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace EzView;

enum Kind { None, Image, Video, Model }

// One item in the queue: a single file, or an image sequence (Frames != null).
record Entry(string Path, string[]? Frames = null, int FirstFrame = 0)
{
    public bool IsSequence => Frames != null;
    public string Ext => System.IO.Path.GetExtension(Path).ToLowerInvariant();
    public bool IsAudio => Media.AudioExt.Contains(Ext);
    public Kind Kind => IsSequence ? Kind.Video : Media.KindOf(Path);

    public string Name => IsSequence
        ? $"{Media.SeqPrefix(Path)}[{FirstFrame}–{FirstFrame + Frames!.Length - 1}]{Ext}"
        : System.IO.Path.GetFileName(Path);

    public string Glyph => IsSequence ? "" : Kind switch
    {
        Kind.Video => IsAudio ? "" : "",
        Kind.Model => "",
        _ => "",
    };
}

static partial class Media
{
    static readonly HashSet<string> ImageExt = [
        ".jpg", ".jpeg", ".png", ".webp", ".bmp", ".heic", ".heif", ".avif", ".jxl", ".tif", ".tiff", ".psd", ".psb",
        ".exr", ".dpx", ".hdr", ".dds", ".tga", ".pcx", ".ico", ".svg", ".jp2", ".qoi", ".xcf", ".ppm", ".pgm", ".pbm", ".pnm",
        ".dng", ".cr2", ".cr3", ".nef", ".nrw", ".arw", ".sr2", ".orf", ".rw2", ".raf", ".srw", ".pef", ".rwl", ".3fr", ".iiq", ".x3f", ".erf", ".kdc"];
    public static readonly HashSet<string> AudioExt = [".mp3", ".flac", ".wav", ".ogg", ".opus", ".m4a", ".aac", ".wma", ".ape", ".aiff"];
    static readonly HashSet<string> VideoExt = [
        ".mp4", ".m4v", ".mkv", ".webm", ".avi", ".mov", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts", ".mts", ".3gp",
        ".ogv", ".vob", ".rmvb", ".asf", ".mxf", ".gif", .. AudioExt];
    static readonly HashSet<string> ModelExt = [
        ".obj", ".fbx", ".gltf", ".glb", ".stl", ".ply", ".3ds", ".dae", ".blend", ".3mf", ".amf", ".x", ".b3d", ".lwo", ".lws",
        ".ase", ".ac", ".off", ".nff", ".cob", ".xgl", ".zgl", ".irr", ".irrmesh", ".ms3d", ".smd",
        ".md2", ".md3", ".md5mesh", ".mdl", ".mdc", ".hmp", ".3d", ".q3o", ".q3s", ".ter",
        ".abc", ".usd", ".usda", ".usdc", ".usdz", ".pk3"];
    static readonly HashSet<string> SequenceExt = [".exr", ".dpx", ".png", ".jpg", ".jpeg", ".tif", ".tiff", ".tga"];

    public static IEnumerable<string> AllExtensions => ImageExt.Concat(VideoExt).Concat(ModelExt);

    public static Kind KindOf(string path)
    {
        var ext = Path.GetExtension(path).ToLowerInvariant();
        return ImageExt.Contains(ext) ? Kind.Image : VideoExt.Contains(ext) ? Kind.Video : ModelExt.Contains(ext) ? Kind.Model : Kind.None;
    }

    // ponytail: Windows-only natural sort (same order as Explorer), swap for a managed comparer when porting
    [LibraryImport("shlwapi.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int StrCmpLogicalW(string a, string b);

    // VFX naming: "shot.1001.exr" for any sequence format, also "shot_1001.exr" for exr/dpx.
    // A plain "IMG_0001.jpg" photo series is deliberately NOT a sequence.
    [GeneratedRegex(@"^(?<pre>.*?[._])(?<num>\d+)(?<ext>\.[^.]+)$")]
    private static partial Regex FrameName();

    static Match? Frame(string path)
    {
        var m = FrameName().Match(Path.GetFileName(path));
        if (!m.Success) return null;
        var ext = m.Groups["ext"].Value.ToLowerInvariant();
        var sep = m.Groups["pre"].Value[^1];
        return SequenceExt.Contains(ext) && (sep == '.' || ext is ".exr" or ".dpx") ? m : null;
    }

    // Half-Life splits a model into man.mdl + manT.mdl (textures) + man01.mdl (animations); only the first can be viewed.
    // Returns the main model for such a companion file, or null.
    public static string? MdlCompanionBase(string path)
    {
        if (!path.EndsWith(".mdl", StringComparison.OrdinalIgnoreCase)) return null;
        var m = MdlCompanion().Match(Path.GetFileNameWithoutExtension(path));
        if (!m.Success) return null;
        var main = Path.Combine(Path.GetDirectoryName(path)!, m.Groups[1].Value + Path.GetExtension(path));
        return File.Exists(main) ? main : null;
    }

    [GeneratedRegex(@"^(.+?)(T|\d\d)$")]
    private static partial Regex MdlCompanion();

    public static string SeqPrefix(string path) => Frame(path)?.Groups["pre"].Value ?? Path.GetFileNameWithoutExtension(path);

    // Sorted entries for a list of files; frames of the same sequence collapse into one entry.
    public static List<Entry> Group(IEnumerable<string> files)
    {
        var sorted = files.Where(f => KindOf(f) != Kind.None && MdlCompanionBase(f) == null).ToArray();
        Array.Sort(sorted, StrCmpLogicalW);
        var seqs = sorted
            .Select(f => (f, m: Frame(f)))
            .Where(x => x.m != null)
            .GroupBy(x => (Path.GetDirectoryName(x.f), x.m!.Groups["pre"].Value.ToLowerInvariant(), x.m.Groups["num"].Length, x.m.Groups["ext"].Value.ToLowerInvariant()))
            .Where(g => g.Count() >= 2)
            .ToDictionary(g => g.First().f, g => g.Select(x => x.f).ToArray());
        var inSeq = seqs.Values.SelectMany(v => v).ToHashSet();
        var result = new List<Entry>();
        foreach (var f in sorted)
        {
            if (seqs.TryGetValue(f, out var frames)) result.Add(new Entry(f, frames, int.Parse(Frame(f)!.Groups["num"].Value)));
            else if (!inSeq.Contains(f)) result.Add(new Entry(f));
        }
        return result;
    }

    public static string Size(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.##} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.#} MB",
        >= 1L << 10 => $"{bytes / 1024.0:0} kB",
        _ => $"{bytes} B",
    };
}
