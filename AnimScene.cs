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
    public Vector2[]? Uv; // per point (FBX, glTF …); null = untextured
    public (byte[] Rgba, int W, int H)? Texture;

    public Skin? Skin; // skeletal deformation of Points[0] (UsdSkel, FBX bones)

    public Vector3[] PointsAt(double t) => Skin?.Deform(Points[0], t) ?? Points[AnimScene.SampleIndex(Times, t)];
    public bool Animated => Points.Length > 1 || Skin != null;
}

// A skeleton: one AnimNode per joint (parented, animated) and each joint's inverse bind matrix.
sealed class Skeleton
{
    public required AnimNode[] Joints;
    public required Matrix4x4[] InverseBind;
    double _cachedT = double.NaN;
    Matrix4x4[] _cached = [];

    // Skinning matrices at time t: inverse(bind) * joint world, cached because every mesh of a character asks.
    public Matrix4x4[] Transforms(double t)
    {
        lock (this)
        {
            if (t == _cachedT) return _cached;
            var m = new Matrix4x4[Joints.Length];
            for (int j = 0; j < m.Length; j++) m[j] = InverseBind[j] * Joints[j].WorldAt(t);
            (_cachedT, _cached) = (t, m);
            return m;
        }
    }
}

// Linear blend skinning of rest points: up to `Influences` (joint, weight) pairs per point, or one set for the
// whole mesh (constant/rigid binding). Joint indices go through JointMap into the skeleton's joint order.
sealed class Skin
{
    public required Skeleton Skeleton;
    public required int[] Indices;
    public required float[] Weights;
    public required int Influences;
    public bool Constant;
    public Matrix4x4 GeomBind = Matrix4x4.Identity;
    public int[]? JointMap;

    public Vector3[] Deform(Vector3[] rest, double t)
    {
        var xf = Skeleton.Transforms(t);
        var result = new Vector3[rest.Length];
        Parallel.For(0, rest.Length, i =>
        {
            var p = Vector3.Transform(rest[i], GeomBind);
            int o = Constant ? 0 : i * Influences;
            Vector3 acc = default;
            float total = 0;
            for (int k = 0; k < Influences && o + k < Indices.Length; k++)
            {
                float w = Weights[o + k];
                if (w == 0) continue;
                int j = Indices[o + k];
                if (JointMap != null) j = j < JointMap.Length ? JointMap[j] : -1;
                if (j < 0 || j >= xf.Length) continue;
                acc += Vector3.Transform(p, xf[j]) * w;
                total += w;
            }
            result[i] = total > 0 ? acc / total : p;
        });
        return result;
    }
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
