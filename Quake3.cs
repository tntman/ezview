using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using ImageMagick;

namespace EzView;

// Quake 3 / Quake Live maps (IBSP 46/47), from a loose .bsp in an extracted game folder or straight from a .pk3.
// Polygons, triangle meshes and bezier patches, grouped per (texture, lightmap) into drawable surfaces.
// ponytail: shader scripts aren't parsed, so surfaces whose image lives under a different name stay untextured
static class Quake3
{
    // Verts: 8 floats per vertex (position, normal, uv) in Y-up space; LmUv: 2 floats per vertex; Lm: 128x128 RGBA.
    public sealed record Surface(string Texture, float[] Verts, uint[] Idx, float[] LmUv, byte[]? Rgba, int W, int H, byte[]? Lm);

    public static bool Handles(string ext) => ext is ".bsp" or ".pk3";

    public static (List<Surface> Surfaces, List<(string, string)> Info) Load(string path)
    {
        if (path.EndsWith(".pk3", StringComparison.OrdinalIgnoreCase))
        {
            using var zip = ZipFile.OpenRead(path);
            var entries = zip.Entries.GroupBy(e => e.FullName.Replace('\\', '/').ToLowerInvariant()).ToDictionary(g => g.Key, g => g.First());
            var map = entries.Keys.Where(k => k.StartsWith("maps/") && k.EndsWith(".bsp")).Order().FirstOrDefault()
                      ?? throw new Exception("No map (maps/*.bsp) inside the .pk3");
            byte[]? Read(string name)
            {
                if (!entries.TryGetValue(name.ToLowerInvariant(), out var e)) return null;
                using var s = e.Open();
                var ms = new MemoryStream();
                s.CopyTo(ms);
                return ms.ToArray();
            }
            return Parse(Read(map)!, Read, Path.GetFileName(map));
        }
        // Loose .bsp: textures are relative to the game folder, the parent of "maps".
        var dir = Path.GetDirectoryName(Path.GetFullPath(path))!;
        var root = Path.GetFileName(dir).Equals("maps", StringComparison.OrdinalIgnoreCase) ? Path.GetDirectoryName(dir)! : dir;
        return Parse(File.ReadAllBytes(path), n => File.Exists(Path.Combine(root, n)) ? File.ReadAllBytes(Path.Combine(root, n)) : null, Path.GetFileName(path));
    }

    static (List<Surface>, List<(string, string)>) Parse(byte[] d, Func<string, byte[]?> read, string name)
    {
        if (d.Length < 8 + 17 * 8 || Encoding.ASCII.GetString(d, 0, 4) != "IBSP")
            throw new Exception(BinaryPrimitives.ReadInt32LittleEndian(d) is 29 or 30
                ? "Quake 1 / Half-Life maps aren't supported yet (only Quake 3)"
                : "Not a Quake 3 map (IBSP)");
        int version = I(d, 4);
        if (version is not (46 or 47)) throw new Exception($"Unsupported IBSP version {version}");
        (int Off, int Len) Lump(int i) => (I(d, 8 + i * 8), I(d, 12 + i * 8));

        var (to, tl) = Lump(1);
        var textures = Enumerable.Range(0, tl / 72).Select(i => (
            Name: Encoding.ASCII.GetString(d, to + i * 72, 64).TrimEnd('\0').Split('\0')[0],
            Flags: I(d, to + i * 72 + 64))).ToArray();

        var (vo, vl) = Lump(10);
        int nv = vl / 44;
        var (mo, ml) = Lump(11);
        var meshVerts = Enumerable.Range(0, ml / 4).Select(i => I(d, mo + i * 4)).ToArray();
        var (lo, ll) = Lump(14);
        var lightmaps = Enumerable.Range(0, ll / (128 * 128 * 3)).Select(i =>
        {
            var rgba = new byte[128 * 128 * 4];
            for (int p = 0; p < 128 * 128; p++)
            {
                rgba[p * 4] = d[lo + i * 49152 + p * 3];
                rgba[p * 4 + 1] = d[lo + i * 49152 + p * 3 + 1];
                rgba[p * 4 + 2] = d[lo + i * 49152 + p * 3 + 2];
                rgba[p * 4 + 3] = 255;
            }
            return rgba;
        }).ToArray();

        // One vertex = position, texture uv, lightmap uv, normal (Quake is Z-up: stored as (x, z, -y)).
        float F(int o) => BinaryPrimitives.ReadSingleLittleEndian(d.AsSpan(o));
        float[] Attr(int v)
        {
            int o = vo + v * 44;
            return [F(o), F(o + 8), -F(o + 4), F(o + 28), F(o + 36), -F(o + 32), F(o + 12), F(o + 16), F(o + 20), F(o + 24)];
        }

        var groups = new Dictionary<(int Tex, int Lm), (List<float> V, List<float> L, List<uint> I)>();
        void Add((List<float> V, List<float> L, List<uint> I) g, float[] a)
        {
            g.V.AddRange(a.AsSpan(0, 8).ToArray());
            g.L.Add(a[8]); g.L.Add(a[9]);
        }

        var (fo, fl) = Lump(13);
        int patches = 0, faces = fl / 104;
        for (int f = 0; f < faces; f++)
        {
            int o = fo + f * 104;
            int tex = I(d, o), type = I(d, o + 8), first = I(d, o + 12), count = I(d, o + 16), mv = I(d, o + 20), nmv = I(d, o + 24), lm = I(d, o + 28);
            if (tex < 0 || tex >= textures.Length || type is 4) continue;
            var t = textures[tex];
            // Skip invisible surfaces: nodraw / sky flags and the editor-only "common" textures (clip, trigger, caulk…).
            if ((t.Flags & (0x80 | 0x4)) != 0 || t.Name.StartsWith("textures/common/", StringComparison.OrdinalIgnoreCase)) continue;
            if (!groups.TryGetValue((tex, lm), out var g)) groups[(tex, lm)] = g = ([], [], []);
            uint baseIdx = (uint)(g.V.Count / 8);
            if (type is 1 or 3)
            {
                for (int i = 0; i < count && first + i < nv; i++) Add(g, Attr(first + i));
                for (int i = 0; i < nmv; i++) g.I.Add(baseIdx + (uint)meshVerts[mv + i]);
            }
            else if (type == 2)
            {
                patches++;
                Patch(g, first, I(d, o + 96), I(d, o + 100), Attr);
            }
        }

        // Textures: the shader name plus a real image extension, read once per name.
        var cache = new Dictionary<string, (byte[], int, int)?>();
        (byte[], int, int)? Texture(string shader)
        {
            if (cache.TryGetValue(shader, out var hit)) return hit;
            (byte[], int, int)? result = null;
            foreach (var ext in new[] { ".jpg", ".tga", ".png" })
            {
                if (read(shader + ext) is not { } bytes) continue;
                try
                {
                    using var img = new MagickImage(bytes, new MagickReadSettings { Format = ext == ".tga" ? MagickFormat.Tga : MagickFormat.Unknown });
                    using var px = img.GetPixelsUnsafe();
                    result = (px.ToByteArray(PixelMapping.RGBA)!, (int)img.Width, (int)img.Height);
                    break;
                }
                catch { /* broken image: try the next extension */ }
            }
            return cache[shader] = result;
        }

        var surfaces = new List<Surface>();
        foreach (var ((tex, lm), g) in groups)
        {
            if (g.I.Count == 0) continue;
            var img = Texture(textures[tex].Name);
            surfaces.Add(new Surface(textures[tex].Name, [.. g.V], [.. g.I], [.. g.L], img?.Item1, img?.Item2 ?? 0, img?.Item3 ?? 0,
                lm >= 0 && lm < lightmaps.Length ? lightmaps[lm] : null));
        }
        var used = surfaces.Select(s => s.Texture).Distinct().ToList();
        var info = new List<(string, string)>
        {
            ("Format", $"Quake 3 map (IBSP {version})"),
            ("Map", name),
            ("Faces", faces.ToString("N0")),
            ("Curved patches", patches.ToString("N0")),
            ("Triangles", surfaces.Sum(s => s.Idx.Length / 3).ToString("N0")),
            ("Textures", $"{used.Count(n => cache.GetValueOrDefault(n) != null)} found, {used.Count(n => cache.GetValueOrDefault(n) == null)} missing"),
            ("Lightmaps", lightmaps.Length.ToString()),
        };
        return (surfaces, info);
    }

    static int I(byte[] d, int o) => BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(o));

    // A patch is a grid of 3x3 quadratic bezier pieces; each piece is tessellated into an (L+1)^2 vertex grid.
    static void Patch((List<float> V, List<float> L, List<uint> I) g, int first, int w, int h, Func<int, float[]> attr)
    {
        const int L = 8;
        static float B(int i, float t) => i switch { 0 => (1 - t) * (1 - t), 1 => 2 * t * (1 - t), _ => t * t };
        for (int py = 0; py + 2 < h; py += 2)
            for (int px = 0; px + 2 < w; px += 2)
            {
                var ctrl = new float[3, 3][];
                for (int j = 0; j < 3; j++)
                    for (int i = 0; i < 3; i++) ctrl[j, i] = attr(first + (py + j) * w + px + i);
                uint baseIdx = (uint)(g.V.Count / 8);
                for (int a = 0; a <= L; a++)
                    for (int b = 0; b <= L; b++)
                    {
                        float v = a / (float)L, u = b / (float)L;
                        var p = new float[10];
                        for (int j = 0; j < 3; j++)
                            for (int i = 0; i < 3; i++)
                            {
                                float k = B(j, v) * B(i, u);
                                for (int c = 0; c < 10; c++) p[c] += k * ctrl[j, i][c];
                            }
                        float len = MathF.Sqrt(p[3] * p[3] + p[4] * p[4] + p[5] * p[5]);
                        if (len > 0) { p[3] /= len; p[4] /= len; p[5] /= len; }
                        g.V.AddRange(p.AsSpan(0, 8).ToArray());
                        g.L.Add(p[8]); g.L.Add(p[9]);
                    }
                for (int a = 0; a < L; a++)
                    for (int b = 0; b < L; b++)
                    {
                        uint i0 = baseIdx + (uint)(a * (L + 1) + b), i1 = i0 + 1, i2 = i0 + L + 1, i3 = i2 + 1;
                        g.I.AddRange([i0, i2, i1, i1, i2, i3]);
                    }
            }
    }
}
