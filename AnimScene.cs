using System.Numerics;

namespace EzView;

// Animated geometry from Alembic / USD: transform hierarchy + meshes whose points may change per sample.
// All times are in seconds.
sealed class AnimNode
{
    public AnimNode? Parent;
    public bool Inherits = true;
    public double[] Times = [0];
    public Matrix4x4[] Locals = [Matrix4x4.Identity];

    public Matrix4x4 WorldAt(double t)
    {
        var local = Locals[AnimScene.SampleIndex(Times, t)];
        return Parent != null && Inherits ? local * Parent.WorldAt(t) : local;
    }
}

// One row in the outliner: a prim / object / mesh with its children and the meshes it owns.
sealed class OutlineNode
{
    public required string Name;
    public string Type = "";
    public OutlineNode? Parent;
    public readonly List<OutlineNode> Children = [];
    public readonly List<int> Meshes = []; // indices into AnimScene.Meshes (= ModelView parts)
    public bool Visible = true, Expanded;

    public OutlineNode Add(string name, string type)
    {
        var n = new OutlineNode { Name = name, Type = type, Parent = this };
        Children.Add(n);
        return n;
    }

    public IEnumerable<OutlineNode> Descendants() => Children.SelectMany(c => c.Descendants().Prepend(c));
}

sealed class AnimMesh
{
    public OutlineNode? Outline;
    public Matrix4x4[]? Instances; // PointInstancer placements (relative to Node); null = drawn once
    public required string Name;
    public required AnimNode Node;
    public required int[] Tris;
    public double[] Times = [0];
    public required Vector3[][] Points;
    public Vector4 Color = Vector4.One;

    public Vector3[] PointsAt(double t) => Points[AnimScene.SampleIndex(Times, t)];
    public bool Animated => Points.Length > 1;
}

sealed class AnimScene
{
    public List<AnimMesh> Meshes = [];
    public double Start, End, Fps = 24;
    public Matrix4x4 Root = Matrix4x4.Identity; // up-axis fix, applied last
    public string Format = "";
    public OutlineNode Outline = new() { Name = "Scene", Type = "Root", Expanded = true };
    public List<(AnimNode Joint, AnimNode Parent)> Bones = []; // skeleton drawn as lines joint -> parent

    // Links every mesh to its outliner node once the scene is complete.
    public void IndexOutline()
    {
        for (int i = 0; i < Meshes.Count; i++) Meshes[i].Outline?.Meshes.Add(i);
    }

    public int FrameCount => Math.Max(1, (int)Math.Round((End - Start) * Fps) + 1);
    public int FirstFrame => (int)Math.Round(Start * Fps);
    public double TimeOf(int frame) => Start + frame / Fps;

    // Held (stepped) sampling: the last sample at or before t.
    public static int SampleIndex(double[] times, double t)
    {
        if (times.Length <= 1) return 0;
        int i = Array.BinarySearch(times, t + 1e-9);
        if (i < 0) i = ~i - 1;
        return Math.Clamp(i, 0, times.Length - 1);
    }

    // Time range covering every animated sample.
    public void FitRange(IEnumerable<double[]> sampleTimes)
    {
        var all = sampleTimes.Where(t => t.Length > 1).ToList();
        if (all.Count == 0) { Start = End = 0; return; }
        Start = all.Min(t => t[0]);
        End = all.Max(t => t[^1]);
    }

    public static AnimScene Load(string path) => Path.GetExtension(path).ToLowerInvariant() switch
    {
        ".abc" => Alembic.Load(path),
        _ => Usd.Load(path),
    };

    public static bool Handles(string ext) => ext is ".abc" or ".usd" or ".usda" or ".usdc" or ".usdz";

    // Triangle fan per polygon.
    public static int[] Triangulate(int[] counts, int[] indices)
    {
        var tris = new List<int>();
        int o = 0;
        foreach (var n in counts)
        {
            for (int k = 1; k + 1 < n; k++) tris.AddRange([indices[o], indices[o + k], indices[o + k + 1]]);
            o += n;
        }
        return [.. tris];
    }

    static int Depth(AnimNode? n) { int d = 0; for (; n != null; n = n.Parent) d++; return d; }

    public string Summary() =>
        $"{Format}: {Meshes.Count} meshes, frames {FirstFrame}..{FirstFrame + FrameCount - 1} @ {Fps} fps\n" +
        string.Join("\n", Meshes.Select(m =>
        {
            var p0 = m.PointsAt(Start);
            var w0 = m.Node.WorldAt(Start) * Root;
            var wMid = m.Node.WorldAt((Start + End) / 2) * Root;
            return $"  {m.Name}: {p0.Length} pts, {m.Tris.Length / 3} tris, {m.Points.Length} point samples, " +
                   $"p0={p0.FirstOrDefault()}, xform depth={Depth(m.Node)}, world t0 T={w0.Translation}, mid T={wMid.Translation}, color={m.Color}";
        }));
}
