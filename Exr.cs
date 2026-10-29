using System.IO.Compression;

namespace EzView;

// AOVs (render passes) in multi-layer EXRs. ImageMagick only reads the main RGBA, so the layers are read here.
// Channels "diffuse.R", "diffuse.G" form the layer "diffuse"; bare R G B A are the main layer ("").
// ponytail: single-part scanline files with NONE / RLE / ZIPS / ZIP compression only (what most renderers write);
// add PIZ / DWAA / tiled / multi-part when files with them turn up
static class Exr
{
    record Channel(string Name, int Type, int XSampling, int YSampling)
    {
        public int Bytes => Type == 1 ? 2 : 4; // 0 uint, 1 half, 2 float
    }

    record Header(List<Channel> Channels, int Compression, (int X0, int Y0, int X1, int Y1) Data, (int X0, int Y0, int X1, int Y1) Display, long ChunksAt);

    static string Layer(string channel) => channel.LastIndexOf('.') is var i and > 0 ? channel[..i]
        : channel is "R" or "G" or "B" or "A" ? "" : channel;

    static string Suffix(string channel) => channel[(channel.LastIndexOf('.') + 1)..];

    // Layers in file order (the main layer first) with their channels ("RGBA", "XYZ", "Z"), or null when the file
    // isn't an EXR or can't be read.
    public static List<(string Layer, string Channels)>? Layers(string path)
    {
        if (!path.EndsWith(".exr", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            var layers = ReadHeader(path).Channels.GroupBy(c => Layer(c.Name)).OrderBy(g => g.Key.Length > 0)
                .Select(g => (g.Key, Names(g.Select(c => Suffix(c.Name))))).ToList();
            return layers.Count > 0 ? layers : null;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException or NotSupportedException or UnauthorizedAccessException) { return null; }
    }

    static string Names(IEnumerable<string> suffixes)
    {
        var s = suffixes.OrderBy(n => n.Length == 1 && "RGBAXYZUVW".IndexOf(n[0]) is var i and >= 0 ? i : 99).ToList();
        return string.Join(s.All(n => n.Length == 1) ? "" : " ", s);
    }

    public static Source Read(string path, string layer)
    {
        var h = ReadHeader(path);
        var chans = h.Channels.Where(c => Layer(c.Name) == layer).ToList();
        if (chans.Count == 0) throw new InvalidDataException($"No layer {layer}");
        if (h.Compression is not (0 or 1 or 2 or 3))
            throw new NotSupportedException($"{Compressions.ElementAtOrDefault(h.Compression)} compression isn't supported for AOVs");
        if (h.Channels.Any(c => c.XSampling != 1 || c.YSampling != 1)) throw new NotSupportedException("Subsampled EXR channels aren't supported");

        // Which channel goes to R, G, B, A (-1: none).
        int Find(params string[] n) => chans.FindIndex(c => n.Contains(Suffix(c.Name), StringComparer.OrdinalIgnoreCase));
        int[] map = [Find("R", "red"), Find("G", "green"), Find("B", "blue"), Find("A", "alpha")];
        if (chans.Count == 1) map = [0, 0, 0, -1];
        else if (map[0] < 0 && map[1] < 0 && map[2] < 0)
        {
            map = [Find("X", "U"), Find("Y", "V"), Find("Z", "W"), map[3]];
            if (map[0] < 0 && map[1] < 0 && map[2] < 0) map = [0, 1, Math.Min(2, chans.Count - 1), -1];
        }

        var (dx0, dy0, dx1, dy1) = h.Data;
        var (vx0, vy0, vx1, vy1) = h.Display;
        int dw = dx1 - dx0 + 1, dh = dy1 - dy0 + 1, w = vx1 - vx0 + 1, ht = vy1 - vy0 + 1;
        int lines = h.Compression == 3 ? 16 : 1, chunks = (dh + lines - 1) / lines;
        var lineBytes = h.Channels.Sum(c => c.Bytes) * dw;
        // Where each channel of this layer starts within a scanline, and how it's stored.
        var offsets = new int[chans.Count];
        for (int i = 0, at = 0; i < h.Channels.Count; at += h.Channels[i].Bytes * dw, i++)
            if (chans.IndexOf(h.Channels[i]) is var k and >= 0) offsets[k] = at;

        var rgba = new float[w * ht * 4];
        if (map[3] < 0) for (int i = 3; i < rgba.Length; i += 4) rgba[i] = 1;
        var file = File.ReadAllBytes(path);
        Parallel.For(0, chunks, n =>
        {
            long at = BitConverter.ToInt64(file, (int)h.ChunksAt + n * 8);
            int y = BitConverter.ToInt32(file, (int)at), size = BitConverter.ToInt32(file, (int)at + 4);
            int rows = Math.Min(lines, dy1 - y + 1);
            var raw = new ReadOnlySpan<byte>(file, (int)at + 8, size);
            var block = size == rows * lineBytes ? raw.ToArray() : Decompress(raw, rows * lineBytes, h.Compression);
            for (int r = 0; r < rows; r++)
            {
                int vy = y + r - vy0;
                if (vy < 0 || vy >= ht) continue;
                for (int c = 0; c < 4; c++)
                {
                    if (map[c] < 0) continue;
                    var ch = chans[map[c]];
                    int src = r * lineBytes + offsets[map[c]];
                    for (int x = 0; x < dw; x++)
                    {
                        int vx = dx0 + x - vx0;
                        if (vx < 0 || vx >= w) continue;
                        int s = src + x * ch.Bytes;
                        rgba[(vy * w + vx) * 4 + c] = ch.Type switch
                        {
                            1 => (float)BitConverter.UInt16BitsToHalf(BitConverter.ToUInt16(block, s)),
                            2 => BitConverter.ToSingle(block, s),
                            _ => BitConverter.ToUInt32(block, s),
                        };
                    }
                }
            }
        });
        var src = new Source([], rgba, true, w, ht);
        return src with { Bgra = ImageView.Adjust(src, 0, 0, 1) };
    }

    static byte[] Decompress(ReadOnlySpan<byte> data, int size, int compression)
    {
        var t = new byte[size];
        if (compression == 1)
        {
            // RLE: a negative count is a run of literal bytes, otherwise the next byte repeated count + 1 times.
            int o = 0;
            for (int i = 0; i < data.Length && o < size;)
            {
                int n = (sbyte)data[i++];
                if (n < 0) { data.Slice(i, -n).CopyTo(t.AsSpan(o)); i -= n; o -= n; }
                else { t.AsSpan(o, n + 1).Fill(data[i++]); o += n + 1; }
            }
        }
        else
        {
            using var z = new ZLibStream(new MemoryStream(data.ToArray()), CompressionMode.Decompress);
            z.ReadExactly(t);
        }
        // Undo the byte delta predictor, then the split into even and odd bytes.
        for (int i = 1; i < size; i++) t[i] = (byte)(t[i - 1] + t[i] - 128);
        var o2 = new byte[size];
        for (int i = 0, half = (size + 1) / 2; i < size; i++) o2[i] = t[(i & 1) == 0 ? i / 2 : half + i / 2];
        return o2;
    }

    static readonly string[] Compressions = ["None", "RLE", "ZIPS", "ZIP", "PIZ", "PXR24", "B44", "B44A", "DWAA", "DWAB", "HTJ2K256", "HTJ2K32"];

    // Every header attribute as text, in file order (the first part's header for multi-part files), for the info panel.
    public static List<(string, string)>? Metadata(string path)
    {
        if (!path.EndsWith(".exr", StringComparison.OrdinalIgnoreCase)) return null;
        try
        {
            using var f = new BinaryReader(File.OpenRead(path));
            if (f.ReadInt32() != 20000630) return null;
            int flags = f.ReadInt32();
            string Str()
            {
                var b = new List<byte>();
                for (byte c; (c = f.ReadByte()) != 0;) b.Add(c);
                return System.Text.Encoding.UTF8.GetString(b.ToArray());
            }
            static string F(float v) => v.ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
            string Fs(int n) => string.Join("  ", Enumerable.Range(0, n).Select(_ => F(f.ReadSingle())));
            string Is(int n) => string.Join("  ", Enumerable.Range(0, n).Select(_ => f.ReadInt32()));

            var kinds = new List<string>();
            if ((flags & 0x200) != 0) kinds.Add("tiled");
            if ((flags & 0x800) != 0) kinds.Add("deep");
            if ((flags & 0x1000) != 0) kinds.Add("multi-part");
            var rows = new List<(string, string)> { ("Version", $"{flags & 0xff}{(kinds.Count > 0 ? "  (" + string.Join(", ", kinds) + ")" : "")}") };
            for (string name; (name = Str()).Length > 0;)
            {
                var type = Str();
                int size = f.ReadInt32();
                long end = f.BaseStream.Position + size;
                string value = type switch
                {
                    "string" => System.Text.Encoding.UTF8.GetString(f.ReadBytes(size)),
                    "stringvector" => string.Join("\n", ReadStrings(f, end)),
                    "int" => $"{f.ReadInt32()}",
                    "float" => F(f.ReadSingle()),
                    "double" => f.ReadDouble().ToString("0.#######", System.Globalization.CultureInfo.InvariantCulture),
                    "v2i" => Is(2), "v3i" => Is(3), "v2f" => Fs(2), "v3f" => Fs(3), "box2f" => Fs(4),
                    "box2i" => Box(f.ReadInt32(), f.ReadInt32(), f.ReadInt32(), f.ReadInt32()),
                    "m33f" => string.Join("\n", Enumerable.Range(0, 3).Select(_ => Fs(3))),
                    "m44f" => string.Join("\n", Enumerable.Range(0, 4).Select(_ => Fs(4))),
                    "rational" => $"{f.ReadInt32()} / {f.ReadUInt32()}",
                    "compression" => Compressions.ElementAtOrDefault(f.ReadByte()) ?? "?",
                    "lineOrder" => f.ReadByte() switch { 0 => "Increasing Y", 1 => "Decreasing Y", _ => "Random Y" },
                    "envmap" => f.ReadByte() == 0 ? "Lat-long" : "Cube",
                    "tiledesc" => $"{f.ReadUInt32()} × {f.ReadUInt32()}",
                    "timecode" => Timecode(f.ReadUInt32()),
                    "chromaticities" => $"R {Fs(2)}\nG {Fs(2)}\nB {Fs(2)}\nW {Fs(2)}",
                    "preview" => $"{f.ReadUInt32()} × {f.ReadUInt32()} thumbnail",
                    "chlist" => string.Join(", ", ReadChannels(f, Str)),
                    _ => $"{type}, {size} bytes",
                };
                rows.Add((name, value));
                f.BaseStream.Position = end;
            }
            return rows;
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException or UnauthorizedAccessException) { return null; }
    }

    static string Box(int x0, int y0, int x1, int y1) => $"{x0} {y0} – {x1} {y1}  ({x1 - x0 + 1} × {y1 - y0 + 1})";

    // SMPTE time and flags: BCD hours, minutes, seconds, frames.
    static string Timecode(uint t)
    {
        static uint Bcd(uint v) => (v >> 4) * 10 + (v & 15);
        return $"{Bcd(t >> 24 & 0x3f):00}:{Bcd(t >> 16 & 0x7f):00}:{Bcd(t >> 8 & 0x7f):00}:{Bcd(t & 0x3f):00}";
    }

    static IEnumerable<string> ReadStrings(BinaryReader f, long end)
    {
        var list = new List<string>();
        while (f.BaseStream.Position < end) list.Add(System.Text.Encoding.UTF8.GetString(f.ReadBytes(f.ReadInt32())));
        return list;
    }

    static IEnumerable<string> ReadChannels(BinaryReader f, Func<string> str)
    {
        var list = new List<string>();
        for (string c; (c = str()).Length > 0;)
        {
            int type = f.ReadInt32();
            f.ReadBytes(12); // pLinear, reserved, sampling
            list.Add($"{c} {(type == 1 ? "half" : type == 2 ? "float" : "uint")}");
        }
        return list;
    }

    static Header ReadHeader(string path)
    {
        using var f = new BinaryReader(File.OpenRead(path));
        if (f.ReadInt32() != 20000630) throw new InvalidDataException("Not an EXR file");
        int flags = f.ReadInt32();
        if ((flags & 0x1a00) != 0) throw new NotSupportedException("Tiled, deep and multi-part EXRs aren't supported for AOVs");
        string Str()
        {
            var b = new List<byte>();
            for (byte c; (c = f.ReadByte()) != 0;) b.Add(c);
            return System.Text.Encoding.UTF8.GetString(b.ToArray());
        }
        (int, int, int, int) Box() => (f.ReadInt32(), f.ReadInt32(), f.ReadInt32(), f.ReadInt32());

        var chans = new List<Channel>();
        int compression = 0;
        (int, int, int, int) data = default, display = default;
        for (string name; (name = Str()).Length > 0;)
        {
            Str(); // type
            int size = f.ReadInt32();
            long end = f.BaseStream.Position + size;
            switch (name)
            {
                case "channels":
                    for (string c; (c = Str()).Length > 0;)
                    {
                        int type = f.ReadInt32();
                        f.ReadInt32(); // pLinear + reserved
                        chans.Add(new Channel(c, type, f.ReadInt32(), f.ReadInt32()));
                    }
                    break;
                case "compression": compression = f.ReadByte(); break;
                case "dataWindow": data = Box(); break;
                case "displayWindow": display = Box(); break;
            }
            f.BaseStream.Position = end;
        }
        return new Header(chans, compression, data, display, f.BaseStream.Position);
    }
}
