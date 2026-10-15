using System.Buffers.Binary;
using System.Globalization;
using System.IO.Compression;
using System.Numerics;
using System.Text;

namespace EzView;

// Minimal USD reader for viewing: .usda (text), .usdc (Crate binary), .usdz (zip).
// Both parsers produce the same flat spec table (path -> fields); one converter turns it into an AnimScene.
// Supports Xform ops (animated), Mesh points (point caches), material diffuse color, upAxis,
// references / payloads / sublayers (composed by grafting the referenced prim tree), PointInstancer.
// ponytail: no variants, layer offsets, inherits/specializes or skinning (UsdSkel meshes show their rest pose)
static class Usd
{
    public sealed class Spec
    {
        public string Kind = "prim"; // prim, attr, rel, root
        public readonly Dictionary<string, object?> Fields = [];

        public Spec Clone()
        {
            var c = new Spec { Kind = Kind };
            foreach (var (k, v) in Fields) c.Fields[k] = v;
            return c;
        }
    }

    // A reference or payload: an asset file (empty = this layer) and an optional prim in it (null = its defaultPrim).
    public sealed record Ref(string Asset, string? Prim);

    public static AnimScene Load(string path)
    {
        path = Path.GetFullPath(path);
        var (_, crate) = ReadLayer(path);
        return Build(Compose(path, [], 0), crate ? "USD (binary)" : "USD (text)");
    }

    static (Dictionary<string, Spec> Specs, bool Crate) ReadLayer(string path)
    {
        var bytes = File.ReadAllBytes(path);
        if (path.EndsWith(".usdz", StringComparison.OrdinalIgnoreCase))
        {
            // ponytail: only the first layer inside a .usdz; references between files inside the zip aren't followed
            using var zip = new ZipArchive(new MemoryStream(bytes));
            var entry = zip.Entries.FirstOrDefault(e => e.Name.EndsWith(".usdc") || e.Name.EndsWith(".usda") || e.Name.EndsWith(".usd"))
                        ?? throw new Exception("No USD layer inside the .usdz");
            using var s = entry.Open();
            var ms = new MemoryStream();
            s.CopyTo(ms);
            bytes = ms.ToArray();
        }
        bool crate = bytes.Length > 8 && Encoding.ASCII.GetString(bytes, 0, 8) == "PXR-USDC";
        return (crate ? new Crate(bytes).Specs() : new Usda(Encoding.UTF8.GetString(bytes)).Parse(), crate);
    }

    // ---------------------------------------------------------------- composition

    // Resolves sublayers, references and payloads of one layer. Composed layers are cached, so an asset
    // referenced a hundred times (Kitchen Set's spoons) is read once; the cache also stops reference cycles.
    static Dictionary<string, Spec> Compose(string file, Dictionary<string, Dictionary<string, Spec>> cache, int depth)
    {
        if (cache.TryGetValue(file, out var done)) return done;
        var specs = ReadLayer(file).Specs;
        cache[file] = specs;
        if (depth > 32) return specs;
        var dir = Path.GetDirectoryName(file)!;

        Dictionary<string, Spec>? Layer(string asset)
        {
            if (asset == "") return specs;
            var f = Path.GetFullPath(Path.Combine(dir, asset));
            return File.Exists(f) ? Compose(f, cache, depth + 1) : null;
        }

        if (specs.GetValueOrDefault("/") is { } root)
            foreach (var sub in Refs(root.Fields.GetValueOrDefault("subLayers")))
                if (Layer(sub.Asset) is { } s) Graft(specs, s, "/", "/");

        var prims = specs.Where(kv => kv.Value.Kind == "prim").Select(kv => kv.Key).OrderBy(p => p.Count(c => c == '/')).ToList();
        foreach (var prim in prims)
        {
            var fields = specs[prim].Fields;
            foreach (var r in Refs(fields.GetValueOrDefault("references")).Concat(Refs(fields.GetValueOrDefault("payload"))))
                if (Layer(r.Asset) is { } src && (r.Prim ?? DefaultPrim(src)) is { } target)
                    Graft(specs, src, target, prim);
        }
        return specs;
    }

    static IEnumerable<Ref> Refs(object? v) => v switch
    {
        Ref r => [r],
        string s when s.StartsWith('/') => [new Ref("", s)],
        string s => [new Ref(s, null)],
        System.Collections.IEnumerable e => e.Cast<object?>().SelectMany(Refs),
        _ => [],
    };

    static string? DefaultPrim(Dictionary<string, Spec> specs)
    {
        if (Strings(specs.GetValueOrDefault("/")?.Fields.GetValueOrDefault("defaultPrim")).FirstOrDefault() is { Length: > 0 } d) return "/" + d;
        return specs.Keys.FirstOrDefault(p => p.LastIndexOf('/') == 0 && p.Length > 1 && !p.Contains('.'));
    }

    // Copies the subtree at `from` in src to `to` in dst as weaker opinions: existing fields win.
    static void Graft(Dictionary<string, Spec> dst, Dictionary<string, Spec> src, string from, string to)
    {
        foreach (var (p, spec) in src.ToList())
        {
            string target;
            if (from == "/") target = p;
            else if (p == from) target = to;
            else if (p.StartsWith(from + "/") || p.StartsWith(from + ".")) target = to + p[from.Length..];
            else continue;
            if (!dst.TryGetValue(target, out var existing)) dst[target] = spec.Clone();
            else if (!ReferenceEquals(existing, spec))
                foreach (var (k, v) in spec.Fields) existing.Fields.TryAdd(k, v);
        }
    }

    // Debug view of a layer's spec table (ezview --specs file).
    public static string DumpSpecs(string path)
    {
        var specs = ReadLayer(path).Specs;
        return string.Join(Environment.NewLine, specs.Select(kv => $"{kv.Value.Kind,-5} {kv.Key}  [{string.Join(", ", kv.Value.Fields.Select(f => f.Key + "=" + Short(f.Value)))}]"));
    }

    static string Short(object? v) => v switch
    {
        null => "null",
        string s => s,
        System.Collections.IEnumerable e => $"<{e.Cast<object>().Count()} items>",
        _ => v.ToString() ?? "",
    };

    // ---------------------------------------------------------------- value helpers

    static IEnumerable<double> Flatten(object? v)
    {
        switch (v)
        {
            case null: yield break;
            case double d: yield return d; break;
            case float f: yield return f; break;
            case int i: yield return i; break;
            case bool b: yield return b ? 1 : 0; break;
            case double[] a: foreach (var x in a) yield return x; break;
            case float[] a: foreach (var x in a) yield return x; break;
            case int[] a: foreach (var x in a) yield return x; break;
            case System.Collections.IEnumerable e when v is not string:
                foreach (var o in e) foreach (var x in Flatten(o)) yield return x;
                break;
        }
    }

    static Vector3[] Vec3s(object? v)
    {
        var f = Flatten(v).ToArray();
        var r = new Vector3[f.Length / 3];
        for (int i = 0; i < r.Length; i++) r[i] = new((float)f[i * 3], (float)f[i * 3 + 1], (float)f[i * 3 + 2]);
        return r;
    }

    static int[] Ints(object? v) => Flatten(v).Select(x => (int)x).ToArray();

    static IEnumerable<string> Strings(object? v) => v switch
    {
        string s => [s],
        System.Collections.IEnumerable e => e.Cast<object>().SelectMany(Strings),
        _ => [],
    };

    // ---------------------------------------------------------------- spec table -> scene

    static AnimScene Build(Dictionary<string, Spec> specs, string format)
    {
        var scene = new AnimScene { Format = format };
        var root = specs.GetValueOrDefault("/")?.Fields ?? [];
        double tcps = Flatten(root.GetValueOrDefault("timeCodesPerSecond") ?? root.GetValueOrDefault("framesPerSecond")).DefaultIfEmpty(24).First();
        scene.Fps = Flatten(root.GetValueOrDefault("framesPerSecond")).DefaultIfEmpty(tcps).First();
        if (Strings(root.GetValueOrDefault("upAxis")).FirstOrDefault() == "Z") scene.Root = Matrix4x4.CreateRotationX(-MathF.PI / 2);

        var children = new Dictionary<string, List<string>>();
        foreach (var (path, spec) in specs)
        {
            if (spec.Kind != "prim" || path == "/") continue;
            var parent = path[..path.LastIndexOf('/')];
            if (parent == "") parent = "/";
            if (!children.TryGetValue(parent, out var list)) children[parent] = list = [];
            list.Add(path);
        }

        Spec? Attr(string prim, string name) => specs.GetValueOrDefault(prim + "." + name);

        // Value of an attribute at its default and/or time samples (times in seconds).
        (double[] Times, object?[] Values) Samples(Spec? a)
        {
            if (a == null) return ([0], [null]);
            if (a.Fields.GetValueOrDefault("timeSamples") is SortedList<double, object?> ts && ts.Count > 0)
                return (ts.Keys.Select(t => t / tcps).ToArray(), ts.Values.ToArray());
            return ([0], [a.Fields.GetValueOrDefault("default")]);
        }

        // Shader colors indexed once: material path -> first diffuseColor found under it.
        var shaderColors = specs
            .Where(kv => kv.Key.EndsWith(".inputs:diffuseColor"))
            .Select(kv => (Path: kv.Key, C: Flatten(kv.Value.Fields.GetValueOrDefault("default")).ToArray()))
            .Where(x => x.C.Length >= 3)
            .ToList();

        Vector4? MaterialColor(string prim)
        {
            var rel = specs.GetValueOrDefault(prim + ".material:binding");
            var target = Strings(rel?.Fields.GetValueOrDefault("targetPaths")).FirstOrDefault();
            if (target == null) return null;
            foreach (var (path, c) in shaderColors)
                if (path.StartsWith(target + "/")) return new((float)c[0], (float)c[1], (float)c[2], 1);
            return null;
        }

        static string Leaf(string path) => path[(path.LastIndexOf('/') + 1)..];

        void Visit(string path, AnimNode? parent, OutlineNode outParent, List<AnimMesh> into)
        {
            var spec = specs[path];
            if (spec.Fields.GetValueOrDefault("active") is false or 0.0) return;
            var type = Strings(spec.Fields.GetValueOrDefault("typeName")).FirstOrDefault() ?? "";
            var outline = outParent.Add(Leaf(path), type);
            var node = parent;
            if (Attr(path, "xformOpOrder") is { } order)
                node = BuildXform(Strings(order.Fields.GetValueOrDefault("default")).ToList(), path, parent, Attr, Samples);
            if (type == "Mesh")
            {
                var (times, pts) = Samples(Attr(path, "points"));
                var counts = Ints(Samples(Attr(path, "faceVertexCounts")).Values[0]);
                var idx = Ints(Samples(Attr(path, "faceVertexIndices")).Values[0]);
                if (pts[0] != null && counts.Length > 0)
                {
                    var color = MaterialColor(path)
                        ?? (Flatten(Attr(path, "primvars:displayColor")?.Fields.GetValueOrDefault("default")).ToArray() is { Length: >= 3 } dc ? new Vector4((float)dc[0], (float)dc[1], (float)dc[2], 1) : Vector4.One);
                    into.Add(new AnimMesh
                    {
                        Name = path, Node = node ?? new AnimNode(), Tris = AnimScene.Triangulate(counts, idx),
                        Times = times, Points = pts.Select(Vec3s).ToArray(), Color = color, Outline = outline,
                    });
                }
            }
            if (type == "PointInstancer") { Instance(path, node, outline, into); return; } // prototypes are drawn only through it
            foreach (var c in children.GetValueOrDefault(path) ?? []) Visit(c, node, outline, into);
        }

        // Each prototype mesh is drawn once per placement with GPU instancing (40k buildings = 8 draw calls).
        // ponytail: instance placement is static (first sample); animated instancers play their prototypes frozen
        void Instance(string path, AnimNode? node, OutlineNode outline, List<AnimMesh> into)
        {
            var protos = Strings(specs.GetValueOrDefault(path + ".prototypes")?.Fields.GetValueOrDefault("targetPaths")).ToList();
            var ids = Ints(Samples(Attr(path, "protoIndices")).Values[0]);
            var pos = Vec3s(Samples(Attr(path, "positions")).Values[0]);
            var rot = Flatten(Samples(Attr(path, "orientations")).Values[0]).ToArray(); // (real, i, j, k) per instance
            var scl = Vec3s(Samples(Attr(path, "scales")).Values[0]);
            var place = new Matrix4x4[ids.Length];
            for (int i = 0; i < ids.Length; i++)
            {
                var s = i < scl.Length ? Matrix4x4.CreateScale(scl[i]) : Matrix4x4.Identity;
                var r = rot.Length >= 4 * (i + 1)
                    ? Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(new((float)rot[4 * i + 1], (float)rot[4 * i + 2], (float)rot[4 * i + 3], (float)rot[4 * i])))
                    : Matrix4x4.Identity;
                place[i] = s * r * Matrix4x4.CreateTranslation(i < pos.Length ? pos[i] : Vector3.Zero);
            }
            for (int k = 0; k < protos.Count; k++)
            {
                if (!specs.ContainsKey(protos[k])) continue;
                var protoMeshes = new List<AnimMesh>();
                Visit(protos[k], null, outline, protoMeshes);
                var instances = Enumerable.Range(0, ids.Length).Where(i => ids[i] == k).ToList();
                foreach (var pm in protoMeshes)
                {
                    var local = pm.Node.WorldAt(0);
                    into.Add(new AnimMesh
                    {
                        Name = $"{pm.Name} (x{instances.Count})", Node = node ?? new AnimNode(), Tris = pm.Tris,
                        Points = [Array.ConvertAll(pm.Points[0], v => Vector3.Transform(v, local))],
                        Instances = instances.Select(i => place[i]).ToArray(), Color = pm.Color, Outline = pm.Outline,
                    });
                }
            }
        }

        scene.Outline.Name = "Stage";
        foreach (var c in children.GetValueOrDefault("/") ?? []) Visit(c, null, scene.Outline, scene.Meshes);
        scene.IndexOutline();

        double start = Flatten(root.GetValueOrDefault("startTimeCode")).DefaultIfEmpty(double.NaN).First() / tcps;
        double end = Flatten(root.GetValueOrDefault("endTimeCode")).DefaultIfEmpty(double.NaN).First() / tcps;
        scene.FitRange(scene.Meshes.Select(m => m.Times).Concat(scene.Meshes.SelectMany(m => Chain(m.Node).Select(n => n.Times))));
        if (!double.IsNaN(start) && !double.IsNaN(end) && end > start) (scene.Start, scene.End) = (start, end);
        return scene;
    }

    static IEnumerable<AnimNode> Chain(AnimNode? n) { for (; n != null; n = n.Parent) yield return n; }

    // xformOpOrder: first op outermost, so M = op[n-1] * ... * op[0] for row vectors.
    static AnimNode BuildXform(List<string> order, string prim, AnimNode? parent,
        Func<string, string, Spec?> attr, Func<Spec?, (double[] Times, object?[] Values)> samples)
    {
        var node = new AnimNode { Parent = parent, Inherits = !order.Contains("!resetXformStack!") };
        var ops = order.Where(o => o.StartsWith("xformOp:") || o.StartsWith("!invert!"))
            .Select(o => (Name: o.Replace("!invert!", ""), Invert: o.StartsWith("!invert!")))
            .Select(o => (o.Name, o.Invert, S: samples(attr(prim, o.Name))))
            .ToList();
        var times = ops.SelectMany(o => o.S.Times).Distinct().Order().DefaultIfEmpty(0).ToArray();
        node.Times = times;
        node.Locals = times.Select(t =>
        {
            var m = Matrix4x4.Identity;
            foreach (var (name, invert, s) in ops)
            {
                var op = Op(name, Flatten(s.Values[AnimScene.SampleIndex(s.Times, t)]).ToArray());
                if (invert) Matrix4x4.Invert(op, out op);
                m = op * m;
            }
            return m;
        }).ToArray();
        return node;
    }

    static Matrix4x4 Op(string name, double[] v)
    {
        float F(int i) => i < v.Length ? (float)v[i] : 0;
        float R(int i) => F(i) * MathF.PI / 180;
        var kind = name.Split(':')[1];
        if (kind == "transform" && v.Length >= 16)
            return new(F(0), F(1), F(2), F(3), F(4), F(5), F(6), F(7), F(8), F(9), F(10), F(11), F(12), F(13), F(14), F(15));
        if (kind == "translate") return Matrix4x4.CreateTranslation(F(0), F(1), F(2));
        if (kind == "scale") return v.Length == 1 ? Matrix4x4.CreateScale(F(0)) : Matrix4x4.CreateScale(F(0), F(1), F(2));
        if (kind == "rotateX") return Matrix4x4.CreateRotationX(R(0));
        if (kind == "rotateY") return Matrix4x4.CreateRotationY(R(0));
        if (kind == "rotateZ") return Matrix4x4.CreateRotationZ(R(0));
        if (kind == "orient" && v.Length >= 4) return Matrix4x4.CreateFromQuaternion(new(F(1), F(2), F(3), F(0))); // USD quat is (real, i, j, k)
        if (kind.StartsWith("rotate") && kind.Length == 9)
        {
            // rotateXYZ etc: angles per axis, the first letter's rotation is applied first.
            var m = Matrix4x4.Identity;
            foreach (var axis in kind[6..])
            {
                float a = R("XYZ".IndexOf(axis));
                m *= axis switch { 'X' => Matrix4x4.CreateRotationX(a), 'Y' => Matrix4x4.CreateRotationY(a), _ => Matrix4x4.CreateRotationZ(a) };
            }
            return m;
        }
        return Matrix4x4.Identity;
    }

    // ================================================================ .usda text

    sealed class Usda(string text)
    {
        int _p;
        readonly Dictionary<string, Spec> _specs = [];

        public Dictionary<string, Spec> Parse()
        {
            if (text.StartsWith("#usda")) _p = text.IndexOf('\n');
            var root = new Spec { Kind = "root" };
            _specs["/"] = root;
            if (Peek() == "(") Metadata(root);
            while (Peek() is "def" or "over" or "class") Prim("/");
            return _specs;
        }

        // -------- tokenizer
        string? _peeked;

        string? Peek() => _peeked ??= NextToken();
        string Next() { var t = Peek() ?? throw new Exception("Unexpected end of .usda"); _peeked = null; return t; }
        void Expect(string t) { var n = Next(); if (n != t) throw new Exception($".usda: expected '{t}' but found '{n}'"); }

        string? NextToken()
        {
            while (_p < text.Length)
            {
                char c = text[_p];
                if (char.IsWhiteSpace(c)) { _p++; continue; }
                if (c == '#') { while (_p < text.Length && text[_p] != '\n') _p++; continue; }
                break;
            }
            if (_p >= text.Length) return null;
            char ch = text[_p];
            if (ch == '"' || ch == '\'')
            {
                bool triple = _p + 2 < text.Length && text[_p + 1] == ch && text[_p + 2] == ch;
                var q = triple ? new string(ch, 3) : ch.ToString();
                int s = _p + q.Length, e = s;
                while (e < text.Length && !string.CompareOrdinal(text, e, q, 0, q.Length).Equals(0)) e += text[e] == '\\' ? 2 : 1;
                _p = e + q.Length;
                return "\"" + text[s..e];
            }
            if (ch == '<') { int e = text.IndexOf('>', _p); var t = "<" + text[(_p + 1)..e]; _p = e + 1; return t; }
            if (ch == '@')
            {
                bool triple = text.AsSpan(_p).StartsWith("@@@");
                var q = triple ? "@@@" : "@";
                int e = text.IndexOf(q, _p + q.Length);
                var t = "@" + text[(_p + q.Length)..e];
                _p = e + q.Length;
                return t;
            }
            if ("()[]{}=,:".Contains(ch)) { _p++; return ch.ToString(); }
            int st = _p;
            while (_p < text.Length && !char.IsWhiteSpace(text[_p]) && !"()[]{}=,\"'<@#".Contains(text[_p])
                   && !(text[_p] == ':' && IsTimeKeyColon(st))) _p++;
            return text[st.._p];
        }

        // In "1: value" the colon ends the number; in "xformOp:translate" it's part of the name.
        bool IsTimeKeyColon(int start) => double.TryParse(text.AsSpan(start, _p - start), NumberStyles.Float, CultureInfo.InvariantCulture, out _);

        // -------- grammar
        void Metadata(Spec spec)
        {
            Expect("(");
            while (Peek() != ")")
            {
                var t = Next();
                if (t.StartsWith('"')) continue; // doc string
                if (t is "prepend" or "append" or "add" or "delete" or "reorder") t = Next();
                if (Peek() != "=") continue;
                Next();
                var value = Value();
                // A selected variant can add references on top of the prim's own.
                spec.Fields[t] = t is "references" or "payload" && spec.Fields.GetValueOrDefault(t) is { } old ? new List<object?> { old, value } : value;
            }
            Expect(")");
        }

        // variantSet "lod" = { "low" (meta) { members } "high" { ... } }: only the selected variant (or the first) is applied.
        void VariantSet(string prim)
        {
            Next();
            var set = Next()[1..];
            Expect("=");
            var spec = _specs[prim];
            var selection = (spec.Fields.GetValueOrDefault("variants") as Dictionary<string, object?>)?.GetValueOrDefault(set) as string;
            Expect("{");
            bool first = true, taken = false;
            while (Peek() != "}")
            {
                var variant = Next()[1..];
                bool use = !taken && (string.IsNullOrEmpty(selection) ? first : variant == selection);
                first = false;
                if (Peek() == "(") { if (use) Metadata(spec); else Skip(); }
                if (!use) { Skip(); continue; }
                taken = true;
                Expect("{");
                while (Peek() != "}") Member(prim);
                Expect("}");
            }
            Expect("}");
        }

        void Prim(string parent)
        {
            var specifier = Next();
            string type = "";
            var t = Next();
            if (!t.StartsWith('"')) { type = t; t = Next(); }
            var path = (parent == "/" ? "/" : parent + "/") + t[1..];
            var spec = new Spec();
            spec.Fields["specifier"] = specifier;
            if (type != "") spec.Fields["typeName"] = type; // untyped "def" mustn't hide the referenced prim's type
            _specs[path] = spec;
            if (Peek() == "(") Metadata(spec);
            Expect("{");
            while (Peek() != "}") Member(path);
            Expect("}");
        }

        void Member(string prim)
        {
            var t = Peek();
            if (t is "def" or "over" or "class") { Prim(prim); return; }
            if (t == "variantSet") { VariantSet(prim); return; }
            t = Next();
            if (t is "prepend" or "append" or "add" or "delete" or "reorder") t = Next();
            if (t == "custom") t = Next();
            if (t is "uniform" or "varying" or "config") t = Next();
            if (t == "rel")
            {
                var name = Next();
                var spec = new Spec { Kind = "rel" };
                if (Peek() == "=") { Next(); spec.Fields["targetPaths"] = Value(); }
                if (Peek() == "(") Skip();
                _specs[prim + "." + name] = spec;
                return;
            }
            var typeName = t;
            if (Peek() == "[") { Next(); Expect("]"); typeName += "[]"; }
            var attrName = Next();
            string field = "default";
            if (attrName.EndsWith(".timeSamples")) { attrName = attrName[..^12]; field = "timeSamples"; }
            else if (attrName.EndsWith(".connect")) { attrName = attrName[..^8]; field = "connect"; }
            var key = prim + "." + attrName;
            if (!_specs.TryGetValue(key, out var a)) _specs[key] = a = new Spec { Kind = "attr" };
            a.Fields["typeName"] = typeName;
            if (Peek() == "=")
            {
                Next();
                a.Fields[field] = field == "timeSamples" ? TimeSamples() : Value();
            }
            if (Peek() == "(") Skip();
        }

        SortedList<double, object?> TimeSamples()
        {
            var r = new SortedList<double, object?>();
            Expect("{");
            while (Peek() != "}")
            {
                var time = double.Parse(Next(), CultureInfo.InvariantCulture);
                Expect(":");
                r[time] = Value();
                if (Peek() == ",") Next();
            }
            Expect("}");
            return r;
        }

        object? Value()
        {
            var t = Next();
            switch (t)
            {
                case "(":
                case "[":
                    var close = t == "(" ? ")" : "]";
                    var list = new List<object?>();
                    while (Peek() != close)
                    {
                        list.Add(Value());
                        if (Peek() == ",") Next();
                    }
                    Next();
                    return list;
                case "{":
                    // Dictionary: "type name = value" entries (variant selections, customData, assetInfo).
                    var dict = new Dictionary<string, object?>();
                    while (Peek() != "}")
                    {
                        Next(); // value type
                        if (Peek() == "[") { Next(); Expect("]"); }
                        var key = Next();
                        if (key.StartsWith('"')) key = key[1..];
                        Expect("=");
                        dict[key] = Value();
                    }
                    Next();
                    return dict;
                case "None": return null;
                case "true": return true;
                case "false": return false;
            }
            if (t[0] == '@') return new Ref(t[1..], Peek() is { } n && n.StartsWith('<') ? Next()[1..] : null); // @asset@</Prim>
            if (t[0] is '"' or '<') return t[1..];
            if (double.TryParse(t, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) return d;
            if (t is "inf" or "+inf") return double.PositiveInfinity;
            if (t == "-inf") return double.NegativeInfinity;
            return t; // token / identifier
        }

        // Skip a balanced (...), [...] or {...} block.
        void Skip()
        {
            int depth = 0;
            do
            {
                var t = Next();
                if (t is "(" or "[" or "{") depth++;
                else if (t is ")" or "]" or "}") depth--;
            } while (depth > 0);
        }
    }

    // ================================================================ .usdc (Crate)

    sealed class Crate
    {
        readonly byte[] _d;
        readonly int _major, _minor;
        readonly string[] _tokens = [];
        readonly int[] _strings = [];
        readonly (int Token, ulong Rep)[] _fields = [];
        readonly int[] _fieldSets = [];
        readonly string[] _paths = [];
        readonly (int Path, int FieldSet, int Type)[] _specs = [];

        public Crate(byte[] d)
        {
            _d = d;
            (_major, _minor) = (d[8], d[9]);
            if (_major == 0 && _minor < 4) throw new Exception($"USD crate version 0.{_minor} is too old");
            long toc = I64(16);
            int n = (int)U64(toc);
            var sections = new Dictionary<string, long>();
            for (int i = 0; i < n; i++)
            {
                long s = toc + 8 + i * 32L;
                sections[Encoding.ASCII.GetString(d, (int)s, 16).TrimEnd('\0')] = I64(s + 16);
            }

            long p = sections["TOKENS"];
            int numTokens = (int)U64(p);
            var tokBytes = Fast(d.AsSpan((int)p + 24, (int)U64(p + 16)), (int)U64(p + 8));
            _tokens = Encoding.UTF8.GetString(tokBytes).Split('\0')[..numTokens];

            p = sections["STRINGS"];
            _strings = new int[U64(p)];
            for (int i = 0; i < _strings.Length; i++) _strings[i] = (int)U32(p + 8 + 4L * i);

            p = sections["FIELDS"];
            int numFields = (int)U64(p); p += 8;
            var fieldTokens = CompressedInts(ref p, numFields);
            var repBytes = Fast(d.AsSpan((int)p + 8, (int)U64(p)), numFields * 8);
            _fields = new (int, ulong)[numFields];
            for (int i = 0; i < numFields; i++) _fields[i] = (fieldTokens[i], BinaryPrimitives.ReadUInt64LittleEndian(repBytes.AsSpan(i * 8)));

            p = sections["FIELDSETS"];
            int numSets = (int)U64(p); p += 8;
            _fieldSets = CompressedInts(ref p, numSets);

            p = sections["PATHS"];
            _paths = new string[U64(p)]; p += 8;
            int numEncoded = (int)U64(p); p += 8;
            var pathIdx = CompressedInts(ref p, numEncoded);
            var elemTok = CompressedInts(ref p, numEncoded);
            var jumps = CompressedInts(ref p, numEncoded);
            BuildPaths(pathIdx, elemTok, jumps, 0, null);

            p = sections["SPECS"];
            int numSpecs = (int)U64(p); p += 8;
            var sp = CompressedInts(ref p, numSpecs);
            var sf = CompressedInts(ref p, numSpecs);
            var st = CompressedInts(ref p, numSpecs);
            _specs = new (int, int, int)[numSpecs];
            for (int i = 0; i < numSpecs; i++) _specs[i] = (sp[i], sf[i], st[i]);
        }

        ulong U64(long p) => BinaryPrimitives.ReadUInt64LittleEndian(_d.AsSpan((int)p));
        long I64(long p) => BinaryPrimitives.ReadInt64LittleEndian(_d.AsSpan((int)p));
        uint U32(long p) => BinaryPrimitives.ReadUInt32LittleEndian(_d.AsSpan((int)p));

        // Paths are stored as a pre-order tree walk: jump > 0 means a sibling at +jump; -1 child only; -2 leaf.
        void BuildPaths(int[] pathIdx, int[] elemTok, int[] jumps, int cur, string? parent)
        {
            bool hasChild, hasSibling;
            do
            {
                int i = cur++;
                string path;
                if (parent == null) path = "/";
                else
                {
                    int tok = elemTok[i];
                    var name = _tokens[Math.Abs(tok)];
                    path = tok < 0 ? parent + "." + name : (parent == "/" ? "/" : parent + "/") + name;
                }
                _paths[pathIdx[i]] = path;
                hasChild = jumps[i] > 0 || jumps[i] == -1;
                hasSibling = jumps[i] >= 0;
                if (hasChild)
                {
                    if (hasSibling) BuildPaths(pathIdx, elemTok, jumps, i + jumps[i], parent);
                    parent = path;
                }
            } while (hasChild || hasSibling);
        }

        public Dictionary<string, Spec> Specs()
        {
            var result = new Dictionary<string, Spec>();
            foreach (var (pathIndex, set, type) in _specs)
            {
                var spec = new Spec { Kind = type switch { 1 => "attr", 7 => "root", 8 => "rel", _ => "prim" } };
                for (int i = set; i < _fieldSets.Length && _fieldSets[i] != -1; i++)
                {
                    var (tok, rep) = _fields[_fieldSets[i]];
                    var name = _tokens[tok];
                    try { spec.Fields[name] = Unpack(rep); }
                    catch { /* unsupported value type: not needed for viewing */ }
                }
                result[_paths[pathIndex]] = spec;
            }
            return result;
        }

        // ---- ValueRep: [isArray:1][inlined:1][compressed:1][unused:5][type:8][payload:48]
        object? Unpack(ulong rep)
        {
            bool isArray = (rep >> 63 & 1) != 0, inlined = (rep >> 62 & 1) != 0, compressed = (rep >> 61 & 1) != 0;
            int type = (int)(rep >> 48 & 0xff);
            ulong payload = rep & 0xFFFFFFFFFFFF;
            if (inlined) return Inline(type, payload);
            long p = (long)payload;
            if (isArray) return Array(type, p, compressed);
            return type switch
            {
                3 => (double)(int)U32(p),
                4 => (double)U32(p),
                5 or 6 => (double)I64(p),
                8 => (double)BitConverter.Int32BitsToSingle((int)U32(p)),
                9 => BitConverter.Int64BitsToDouble(I64(p)),
                10 => _tokens[_strings[U32(p)]],
                11 or 12 => _tokens[U32(p)],
                13 => Doubles(p, 4), 14 => Doubles(p, 9), 15 => Doubles(p, 16),
                16 => Quats(Doubles(p, 4)), 17 => Quats(Floats(p, 4).Select(x => (double)x).ToArray()), 18 => Quats(Halves(p, 4)),
                19 => Doubles(p, 2), 20 => Floats(p, 2), 22 => Ints32(p, 2),
                23 => Doubles(p, 3), 24 => Floats(p, 3), 26 => Ints32(p, 3),
                27 => Doubles(p, 4), 28 => Floats(p, 4), 30 => Ints32(p, 4),
                34 => PathListOp(p),
                35 => RefListOp(p, reference: true),
                47 => ReadRef(ref p, reference: false),
                55 => RefListOp(p, reference: false),
                40 => Enumerable.Range(0, (int)U64(p)).Select(i => _paths[U32(p + 8 + 4L * i)]).ToList(),
                41 => Enumerable.Range(0, (int)U64(p)).Select(i => _tokens[U32(p + 8 + 4L * i)]).ToList(),
                46 => TimeSamples(p),
                48 => Doubles(p + 8, (int)U64(p)),
                _ => null,
            };
        }

        object? Inline(int type, ulong v)
        {
            sbyte B(int i) => (sbyte)(v >> (8 * i) & 0xff);
            return type switch
            {
                1 => v != 0,
                2 or 3 or 4 or 42 or 43 or 44 => (double)(int)(uint)v,
                8 or 9 or 56 => (double)BitConverter.Int32BitsToSingle((int)(uint)v), // doubles inline as floats
                10 => _tokens[_strings[(int)v]],
                11 or 12 => _tokens[(int)v],
                19 or 20 or 22 => new double[] { B(0), B(1) },
                23 or 24 or 26 => new double[] { B(0), B(1), B(2) },
                27 or 28 or 30 => new double[] { B(0), B(1), B(2), B(3) },
                13 => new double[] { B(0), 0, 0, B(1) },
                14 => new double[] { B(0), 0, 0, 0, B(1), 0, 0, 0, B(2) },
                15 => new double[] { B(0), 0, 0, 0, 0, B(1), 0, 0, 0, 0, B(2), 0, 0, 0, 0, B(3) },
                _ => null,
            };
        }

        double[] Halves(long p, int n) { var r = new double[n]; for (int i = 0; i < n; i++) r[i] = (double)BitConverter.UInt16BitsToHalf(BinaryPrimitives.ReadUInt16LittleEndian(_d.AsSpan((int)(p + 2L * i)))); return r; }

        // Crate stores quaternions as (i, j, k, real); .usda writes (real, i, j, k). Normalize to the text order.
        static double[] Quats(double[] q)
        {
            for (int i = 0; i + 3 < q.Length; i += 4) (q[i], q[i + 1], q[i + 2], q[i + 3]) = (q[i + 3], q[i], q[i + 1], q[i + 2]);
            return q;
        }

        double[] Doubles(long p, int n) { var r = new double[n]; for (int i = 0; i < n; i++) r[i] = BitConverter.Int64BitsToDouble(I64(p + 8L * i)); return r; }
        float[] Floats(long p, int n) { var r = new float[n]; for (int i = 0; i < n; i++) r[i] = BitConverter.Int32BitsToSingle((int)U32(p + 4L * i)); return r; }
        int[] Ints32(long p, int n) { var r = new int[n]; for (int i = 0; i < n; i++) r[i] = (int)U32(p + 4L * i); return r; }

        object? Array(int type, long p, bool compressed)
        {
            bool wideCount = _major > 0 || _minor >= 7;
            long n = wideCount ? (long)U64(p) : U32(p);
            p += wideCount ? 8 : 4;
            if (n == 0) return new double[0];
            if (compressed)
            {
                if (type is 3 or 4) return CompressedInts(ref p, (int)n);
                if (type is 8 or 9)
                {
                    char code = (char)_d[p++];
                    if (code == 'i') return CompressedInts(ref p, (int)n).Select(x => (double)x).ToArray();
                    if (code == 't')
                    {
                        int lutSize = (int)U32(p); p += 4;
                        var lut = type == 8 ? Floats(p, lutSize).Select(x => (double)x).ToArray() : Doubles(p, lutSize);
                        p += lutSize * (type == 8 ? 4 : 8);
                        return CompressedInts(ref p, (int)n).Select(i => lut[i]).ToArray();
                    }
                }
                return null;
            }
            int m = (int)n;
            return type switch
            {
                3 or 4 => Ints32(p, m),
                8 => Floats(p, m),
                9 => Doubles(p, m),
                10 => Enumerable.Range(0, m).Select(i => _tokens[_strings[U32(p + 4L * i)]]).ToList(),
                11 or 12 => Enumerable.Range(0, m).Select(i => _tokens[U32(p + 4L * i)]).ToList(),
                15 => Doubles(p, 16 * m),
                19 => Doubles(p, 2 * m), 20 => Floats(p, 2 * m), 22 => Ints32(p, 2 * m),
                23 => Doubles(p, 3 * m), 24 => Floats(p, 3 * m), 26 => Ints32(p, 3 * m),
                27 => Doubles(p, 4 * m), 28 => Floats(p, 4 * m), 30 => Ints32(p, 4 * m),
                16 => Quats(Doubles(p, 4 * m)), 17 => Quats(Floats(p, 4 * m).Select(x => (double)x).ToArray()), 18 => Quats(Halves(p, 4 * m)),
                _ => null,
            };
        }

        // Reference: asset (string index), prim path, layer offset (2 doubles), customData dictionary.
        // Payload: the same without customData; the layer offset only exists from crate 0.8.
        Ref ReadRef(ref long p, bool reference)
        {
            var asset = _tokens[_strings[U32(p)]];
            var prim = _paths[U32(p + 4)];
            p += 8;
            if (reference || _major > 0 || _minor >= 8) p += 16;
            if (reference)
            {
                long n = (long)U64(p);
                p += 8;
                if (n != 0) throw new NotSupportedException("reference customData"); // ponytail: not needed to locate the asset
            }
            return new Ref(asset, prim is null or "" or "/" ? null : prim);
        }

        List<Ref> RefListOp(long p, bool reference)
        {
            byte h = _d[p++];
            var all = new List<Ref>();
            foreach (var bit in new[] { 1 << 1, 1 << 2, 1 << 5, 1 << 6, 1 << 3, 1 << 4 }) // explicit, added, prepended, appended, deleted, ordered
            {
                if ((h & bit) == 0) continue;
                long n = (long)U64(p); p += 8;
                for (int i = 0; i < n; i++)
                {
                    var r = ReadRef(ref p, reference);
                    if (bit != 1 << 3) all.Add(r);
                }
            }
            return all;
        }

        // Header byte of flags, then each present list as (count, path indices).
        List<string> PathListOp(long p)
        {
            byte h = _d[p++];
            var all = new List<string>();
            foreach (var bit in new[] { 1 << 1, 1 << 2, 1 << 5, 1 << 6, 1 << 3, 1 << 4 }) // explicit, added, prepended, appended, deleted, ordered
            {
                if ((h & bit) == 0) continue;
                long n = (long)U64(p); p += 8;
                for (int i = 0; i < n; i++, p += 4) if (bit != 1 << 3) all.Add(_paths[U32(p)]);
            }
            return all;
        }

        // Two recursively-written blocks; each starts with an int64 jump, and its ValueRep is the block's last 8 bytes
        // (any array data it points to is written in front of it):
        // [jump1][..times data..][times rep] [jump2][..value data..][count][value reps...]
        SortedList<double, object?> TimeSamples(long p)
        {
            long jump1 = I64(p);
            var times = Flatten(Unpack(U64(p + jump1))).ToArray();
            long s2 = p + jump1 + 8;
            long c = s2 + I64(s2);
            long count = (long)U64(c);
            var r = new SortedList<double, object?>();
            for (int i = 0; i < count && i < times.Length; i++) r[times[i]] = Unpack(U64(c + 8 + 8L * i));
            return r;
        }

        // ---- integer compression: LZ4 ("fast compression") around a delta/variable-width code stream
        int[] CompressedInts(ref long p, int n)
        {
            int size = (int)U64(p);
            var raw = Fast(_d.AsSpan((int)p + 8, size), 4 + (n * 2 + 7) / 8 + n * 4);
            p += 8 + size;
            return DecodeInts(raw, n);
        }

        static int[] DecodeInts(byte[] b, int n)
        {
            var r = new int[n];
            int common = BinaryPrimitives.ReadInt32LittleEndian(b);
            int codes = 4, v = 4 + (n * 2 + 7) / 8, prev = 0;
            for (int i = 0; i < n; i++)
            {
                int code = b[codes + i / 4] >> (2 * (i % 4)) & 3;
                int delta;
                switch (code)
                {
                    case 0: delta = common; break;
                    case 1: delta = (sbyte)b[v]; v += 1; break;
                    case 2: delta = BinaryPrimitives.ReadInt16LittleEndian(b.AsSpan(v)); v += 2; break;
                    default: delta = BinaryPrimitives.ReadInt32LittleEndian(b.AsSpan(v)); v += 4; break;
                }
                r[i] = prev += delta;
            }
            return r;
        }

        // TfFastCompression: first byte = chunk count (0 = one chunk), chunks are int32 size + LZ4 block.
        static byte[] Fast(ReadOnlySpan<byte> src, int maxOut)
        {
            var output = new byte[maxOut];
            int chunks = src[0], o = 0;
            if (chunks == 0) o = Lz4(src[1..], output, 0);
            else
                for (int i = 0, p = 1; i < chunks; i++)
                {
                    int size = BinaryPrimitives.ReadInt32LittleEndian(src[p..]); p += 4;
                    o = Lz4(src.Slice(p, size), output, o);
                    p += size;
                }
            return output.Length == o ? output : output[..o];
        }

        static int Lz4(ReadOnlySpan<byte> s, byte[] dst, int o)
        {
            int p = 0;
            while (p < s.Length)
            {
                int token = s[p++];
                int lit = token >> 4;
                if (lit == 15) { int b; do { b = s[p++]; lit += b; } while (b == 255); }
                s.Slice(p, lit).CopyTo(dst.AsSpan(o));
                p += lit; o += lit;
                if (p >= s.Length) break;
                int offset = s[p] | s[p + 1] << 8; p += 2;
                int len = token & 15;
                if (len == 15) { int b; do { b = s[p++]; len += b; } while (b == 255); }
                len += 4;
                for (int i = 0; i < len; i++, o++) dst[o] = dst[o - offset]; // may overlap
            }
            return o;
        }
    }
}
