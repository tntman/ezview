using System.Buffers.Binary;
using System.Numerics;
using System.Text;

namespace EzView;

// Minimal Alembic reader: Ogawa container + the AbcCoreOgawa layout, enough for
// Xform hierarchies and PolyMesh/SubD point caches.
// ponytail: whole file in memory and every sample decoded up front; stream samples if huge caches matter
static class Alembic
{
    // ---- Ogawa: groups of child offsets; MSB set means the child is a data blob ----
    sealed class Ogawa(byte[] d)
    {
        const ulong DataBit = 0x8000000000000000;
        public ulong Root => U64(8);
        ulong U64(long p) => BinaryPrimitives.ReadUInt64LittleEndian(d.AsSpan((int)p));

        public ulong[] Children(ulong group)
        {
            if (group == 0 || (group & DataBit) != 0) return [];
            var n = (int)U64((long)group);
            var r = new ulong[n];
            for (int i = 0; i < n; i++) r[i] = U64((long)group + 8 + 8L * i);
            return r;
        }

        public static bool IsData(ulong child) => (child & DataBit) != 0;

        public ReadOnlySpan<byte> Data(ulong child)
        {
            var p = (long)(child & ~DataBit);
            return p == 0 ? [] : d.AsSpan((int)p + 8, (int)U64(p));
        }
    }

    record Prop(string Name, int Type, int Pod, int Extent, int NumSamples, int First, int Last, int Ts, string Meta, ulong Child);

    sealed class Reader
    {
        public readonly Ogawa O;
        public readonly List<string> MetaTable = [""];
        public readonly List<(double Tpc, double[] Times)> Samplings = [];

        public Reader(byte[] bytes)
        {
            if (bytes.Length < 16 || Encoding.ASCII.GetString(bytes, 0, 5) != "Ogawa") throw new Exception("Not an Ogawa Alembic file (HDF5 Alembic isn't supported)");
            O = new Ogawa(bytes);
            var root = O.Children(O.Root);
            ReadSamplings(O.Data(root[4]));
            var meta = O.Data(root[5]);
            for (int p = 0; p < meta.Length;)
            {
                int n = meta[p++];
                MetaTable.Add(Encoding.UTF8.GetString(meta.Slice(p, n)));
                p += n;
            }
        }

        void ReadSamplings(ReadOnlySpan<byte> s)
        {
            for (int p = 0; p + 16 <= s.Length;)
            {
                p += 4; // max sample
                var tpc = BinaryPrimitives.ReadDoubleLittleEndian(s[p..]); p += 8;
                var n = BinaryPrimitives.ReadInt32LittleEndian(s[p..]); p += 4;
                var times = new double[n];
                for (int i = 0; i < n; i++, p += 8) times[i] = BinaryPrimitives.ReadDoubleLittleEndian(s[p..]);
                Samplings.Add((tpc, times));
            }
        }

        public double SampleTime(int ts, int i)
        {
            if (ts >= Samplings.Count) return i;
            var (tpc, times) = Samplings[ts];
            if (times.Length == 0) return i;
            if (tpc > 1e30) return times[Math.Min(i, times.Length - 1)]; // acyclic
            return times[i % times.Length] + i / times.Length * tpc;
        }

        static uint Sized(ReadOnlySpan<byte> b, int hint, ref int p)
        {
            uint v = hint switch { 0 => b[p], 1 => BinaryPrimitives.ReadUInt16LittleEndian(b[p..]), _ => BinaryPrimitives.ReadUInt32LittleEndian(b[p..]) };
            p += hint switch { 0 => 1, 1 => 2, _ => 4 };
            return v;
        }

        // A compound property group: children are the properties, the last child holds their headers.
        public List<Prop> Props(ulong group)
        {
            var ch = O.Children(group);
            var result = new List<Prop>();
            if (ch.Length == 0 || !Ogawa.IsData(ch[^1])) return result;
            var b = O.Data(ch[^1]);
            for (int p = 0, i = 0; p + 4 <= b.Length; i++)
            {
                uint info = BinaryPrimitives.ReadUInt32LittleEndian(b[p..]); p += 4;
                int type = (int)(info & 3), hint = (int)((info >> 2) & 3);
                int pod = 0, extent = 0, num = 0, first = 0, last = 0, ts = 0;
                if (type != 0)
                {
                    pod = (int)((info >> 4) & 0xf);
                    extent = (int)((info >> 12) & 0xff);
                    num = (int)Sized(b, hint, ref p);
                    if ((info & 0x200) != 0) { first = (int)Sized(b, hint, ref p); last = (int)Sized(b, hint, ref p); }
                    else if ((info & 0x800) != 0) { first = last = 0; }
                    else { first = 1; last = num - 1; }
                    if ((info & 0x100) != 0) ts = (int)Sized(b, hint, ref p);
                }
                int nameLen = (int)Sized(b, hint, ref p);
                var name = Encoding.UTF8.GetString(b.Slice(p, nameLen));
                p += nameLen;
                int mi = (int)((info >> 20) & 0xff);
                string meta;
                if (mi == 0xff) { var n = (int)Sized(b, hint, ref p); meta = Encoding.UTF8.GetString(b.Slice(p, n)); p += n; }
                else meta = mi < MetaTable.Count ? MetaTable[mi] : "";
                result.Add(new Prop(name, type, pod, extent, num, first, last, ts, meta, ch[i]));
            }
            return result;
        }

        static int MapIndex(Prop h, int i) =>
            i < h.First || (h.First == 0 && h.Last == 0) ? 0 : Math.Min(i, h.Last);

        // Raw bytes of sample i (array samples carry a 16-byte hash in front).
        public ReadOnlySpan<byte> Sample(Prop h, int i)
        {
            var ch = O.Children(h.Child);
            int k = MapIndex(h, i);
            int step = h.Type == 1 ? 1 : 2; // arrays store (data, dims) pairs
            for (; k >= 0; k--)
            {
                if (k * step >= ch.Length) continue;
                var d = O.Data(ch[k * step]);
                if (d.Length == 0) continue; // repeated sample: use the previous one
                return h.Type == 1 && d.Length != 16 + PodSize(h.Pod) * Math.Max(1, h.Extent) ? d : d[16..];
            }
            return [];
        }

        public static int PodSize(int pod) => pod switch { 0 or 1 or 2 => 1, 3 or 4 or 9 => 2, 5 or 6 or 10 => 4, _ => 8 };
    }

    record ObjHeader(string Name, string Meta, ulong Group);

    static List<ObjHeader> Children(Reader r, ulong objGroup)
    {
        var ch = r.O.Children(objGroup);
        var list = new List<ObjHeader>();
        if (ch.Length < 2 || !Ogawa.IsData(ch[^1])) return list;
        var b = r.O.Data(ch[^1]);
        int end = b.Length - 32, child = 1; // trailing 32 bytes are hashes
        for (int p = 0; p < end; child++)
        {
            int n = BinaryPrimitives.ReadInt32LittleEndian(b[p..]); p += 4;
            var name = Encoding.UTF8.GetString(b.Slice(p, n)); p += n;
            int mi = b[p++];
            string meta;
            if (mi == 0xff) { int m = BinaryPrimitives.ReadInt32LittleEndian(b[p..]); p += 4; meta = Encoding.UTF8.GetString(b.Slice(p, m)); p += m; }
            else meta = mi < r.MetaTable.Count ? r.MetaTable[mi] : "";
            list.Add(new ObjHeader(name, meta, ch[child]));
        }
        return list;
    }

    static string Schema(string meta) =>
        meta.Split(';').Select(kv => kv.Split('=', 2)).FirstOrDefault(kv => kv[0] == "schema") is { Length: 2 } s ? s[1] : "";

    public static AnimScene Load(string path)
    {
        var r = new Reader(File.ReadAllBytes(path));
        var scene = new AnimScene { Format = "Alembic" };
        var top = r.O.Children(r.O.Root)[2];
        scene.Outline.Name = "Archive";
        Walk(r, top, null, scene, "", scene.Outline);
        scene.IndexOutline();
        scene.FitRange(scene.Meshes.SelectMany(m => new[] { m.Times }.Concat(Chain(m.Node).Select(n => n.Times))));
        scene.Fps = GuessFps(r, scene);
        return scene;
    }

    static IEnumerable<AnimNode> Chain(AnimNode? n) { for (; n != null; n = n.Parent) yield return n; }

    static double GuessFps(Reader r, AnimScene s)
    {
        var tpc = r.Samplings.Select(x => x.Tpc).Where(t => t > 1e-6 && t < 1e30).DefaultIfEmpty(1 / 24.0).Min();
        return Math.Round(1 / tpc, 3);
    }

    static void Walk(Reader r, ulong objGroup, AnimNode? parent, AnimScene scene, string path, OutlineNode outParent)
    {
        foreach (var child in Children(r, objGroup))
        {
            var schema = Schema(child.Meta);
            var outline = outParent.Add(child.Name, schema.Replace("AbcGeom_", "").Split('_')[0]);
            var props = r.Props(r.O.Children(child.Group).FirstOrDefault());
            var node = parent;
            if (schema.StartsWith("AbcGeom_Xform"))
                node = ReadXform(r, props, parent);
            else if (schema.StartsWith("AbcGeom_PolyMesh") || schema.StartsWith("AbcGeom_SubD"))
                ReadMesh(r, props, node ?? new AnimNode(), scene, path + "/" + child.Name, outline);
            Walk(r, child.Group, node, scene, path + "/" + child.Name, outline);
        }
    }

    static AnimNode ReadXform(Reader r, List<Prop> props, AnimNode? parent)
    {
        var node = new AnimNode { Parent = parent };
        var xf = props.FirstOrDefault(p => p.Name == ".xform" && p.Type == 0);
        if (xf == null) return node;
        var sub = r.Props(xf.Child);
        var ops = sub.FirstOrDefault(p => p.Name == ".ops");
        var vals = sub.FirstOrDefault(p => p.Name == ".vals");
        if (sub.FirstOrDefault(p => p.Name == ".inherits") is { } inh && r.Sample(inh, 0) is { Length: > 0 } ib) node.Inherits = ib[0] != 0;
        if (ops == null || vals == null) return node;
        var opCodes = r.Sample(ops, 0).ToArray();
        int n = Math.Max(1, vals.NumSamples);
        node.Times = new double[n];
        node.Locals = new Matrix4x4[n];
        for (int i = 0; i < n; i++)
        {
            node.Times[i] = r.SampleTime(vals.Ts, i);
            var v = Doubles(r.Sample(vals, i), vals.Pod);
            node.Locals[i] = Compose(opCodes, v);
        }
        return node;
    }

    static double[] Doubles(ReadOnlySpan<byte> b, int pod)
    {
        int size = Reader.PodSize(pod), n = b.Length / size;
        var v = new double[n];
        for (int i = 0; i < n; i++)
            v[i] = pod == 10 ? BinaryPrimitives.ReadSingleLittleEndian(b[(i * 4)..]) : BinaryPrimitives.ReadDoubleLittleEndian(b[(i * 8)..]);
        return v;
    }

    // Alembic ops (type in the high nibble), first op outermost: M = op[n-1] * ... * op[0].
    static Matrix4x4 Compose(byte[] ops, double[] v)
    {
        var m = Matrix4x4.Identity;
        int k = 0;
        float F(int i) => k + i < v.Length ? (float)v[k + i] : 0;
        foreach (var op in ops)
        {
            Matrix4x4 o;
            switch (op >> 4)
            {
                case 0: o = Matrix4x4.CreateScale(F(0), F(1), F(2)); k += 3; break;
                case 1: o = Matrix4x4.CreateTranslation(F(0), F(1), F(2)); k += 3; break;
                case 2: o = Matrix4x4.CreateFromAxisAngle(Vector3.Normalize(new(F(0), F(1), F(2))), F(3) * MathF.PI / 180); k += 4; break;
                case 3:
                    o = new Matrix4x4(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7), F(8), F(9), F(10), F(11), F(12), F(13), F(14), F(15));
                    k += 16; break;
                case 4: o = Matrix4x4.CreateRotationX(F(0) * MathF.PI / 180); k += 1; break;
                case 5: o = Matrix4x4.CreateRotationY(F(0) * MathF.PI / 180); k += 1; break;
                default: o = Matrix4x4.CreateRotationZ(F(0) * MathF.PI / 180); k += 1; break;
            }
            m = o * m;
        }
        return m;
    }

    static int[] Ints(ReadOnlySpan<byte> b)
    {
        var v = new int[b.Length / 4];
        for (int i = 0; i < v.Length; i++) v[i] = BinaryPrimitives.ReadInt32LittleEndian(b[(i * 4)..]);
        return v;
    }

    static Vector3[] Vec3s(ReadOnlySpan<byte> b)
    {
        var v = new Vector3[b.Length / 12];
        for (int i = 0; i < v.Length; i++)
            v[i] = new(BinaryPrimitives.ReadSingleLittleEndian(b[(i * 12)..]), BinaryPrimitives.ReadSingleLittleEndian(b[(i * 12 + 4)..]), BinaryPrimitives.ReadSingleLittleEndian(b[(i * 12 + 8)..]));
        return v;
    }

    static void ReadMesh(Reader r, List<Prop> props, AnimNode node, AnimScene scene, string name, OutlineNode outline)
    {
        var geom = props.FirstOrDefault(p => p.Name == ".geom" && p.Type == 0);
        if (geom == null) return;
        var g = r.Props(geom.Child);
        var P = g.FirstOrDefault(p => p.Name == "P");
        var fi = g.FirstOrDefault(p => p.Name == ".faceIndices");
        var fc = g.FirstOrDefault(p => p.Name == ".faceCounts");
        if (P == null || fi == null || fc == null) return;
        // ponytail: topology taken from sample 0; meshes whose face layout changes per frame play with frame 0's faces
        var tris = AnimScene.Triangulate(Ints(r.Sample(fc, 0)), Ints(r.Sample(fi, 0)));
        int n = Math.Max(1, P.NumSamples);
        var times = new double[n];
        var points = new Vector3[n][];
        for (int i = 0; i < n; i++)
        {
            times[i] = r.SampleTime(P.Ts, i);
            int prev = i > 0 && Math.Min(i, P.Last) == Math.Min(i - 1, P.Last) && i > P.Last ? i - 1 : -1;
            points[i] = prev >= 0 ? points[prev] : Vec3s(r.Sample(P, i));
        }
        scene.Meshes.Add(new AnimMesh { Name = name, Node = node, Tris = tris, Times = times, Points = points, Outline = outline });
    }
}
