using System.Numerics;
using Avalonia;
using Avalonia.Input;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Rendering;
using Silk.NET.OpenGL;
using Ai = Silk.NET.Assimp;
using MagickImage = ImageMagick.MagickImage;
using MagickFormat = ImageMagick.MagickFormat;

namespace EzView;

class ModelView : OpenGlControlBase, ICustomHitTest, IPlayback
{
    // 8 floats per vertex: position, normal, uv.
    // Inst: placement matrices drawn with GPU instancing (one identity for ordinary meshes).
    record Part(float[] Verts, uint[] Idx, byte[]? Rgba, int TexW, int TexH, Vector4 Color)
    {
        public Matrix4x4[] Inst { get; set; } = [Matrix4x4.Identity];
        public string Name { get; init; } = "";
        public float[]? LmUv { get; init; } // baked lighting (Quake 3 maps): uv per vertex + 128x128 RGBA
        public byte[]? Lm { get; init; }
    }
    sealed class GpuPart(Part part, uint vao, uint vbo, uint ebo, uint tex)
    {
        public readonly Part Part = part;
        public readonly uint Vao = vao, Vbo = vbo, Ebo = ebo, Tex = tex;
        public uint InstVbo, LmVbo, LmTex;
        public uint LineVao, LineEbo; // wireframe edges, built the first time wireframe is shown
        public int LineCount;
    }

    const string Vs = """
        layout(location = 0) in vec3 p;
        layout(location = 1) in vec3 n;
        layout(location = 2) in vec2 t;
        layout(location = 3) in mat4 inst;  // per instance; identity for ordinary meshes
        layout(location = 7) in vec2 lmt;   // lightmap uv (only bound for lightmapped surfaces)
        uniform mat4 mvp;
        out vec3 vp; out vec3 vn; out vec2 vt; out vec2 vlm;
        void main() { vec4 wp = inst * vec4(p, 1.0); gl_Position = mvp * wp; gl_PointSize = 7.0; vp = wp.xyz; vn = mat3(inst) * n; vt = t; vlm = lmt; }
        """;
    const string Fs = """
        in vec3 vp; in vec3 vn; in vec2 vt; in vec2 vlm;
        uniform sampler2D tex; uniform sampler2D lm; uniform vec4 col; uniform vec3 eye; uniform int unlit; uniform int useLm;
        out vec4 o;
        void main() {
            if (unlit == 1) { o = col; return; }  // wireframe lines and grid
            vec4 c = texture(tex, vt) * col;
            if (c.a < 0.5) discard;
            if (useLm == 1)
            {
                // Baked light: Quake 3 brightens lightmaps 4x (2x on load, 2x via gamma) and scales an
                // over-bright texel down as a whole so it keeps its hue.
                vec3 l = texture(lm, vlm).rgb * 4.0;
                l /= max(1.0, max(l.r, max(l.g, l.b)));
                o = vec4(c.rgb * l, 1.0);
                return;
            }
            float d = abs(dot(normalize(vn), normalize(eye - vp)));  // headlight, two-sided
            o = vec4(c.rgb * (0.3 + 0.7 * d), 1.0);
        }
        """;

    GL? _gl;
    uint _prog, _white;
    List<GpuPart> _gpu = [];
    List<Part>? _parts, _pending;
    Vector3 _center, _target, _min;
    uint _gridVao, _gridVbo;
    int _gridCount;
    bool _gridDirty;

    // View toggles (W / G / T).
    public bool Wireframe { get; private set; }
    public bool ShowGrid { get; private set; }
    public bool Textures { get; private set; } = true;
    public void ToggleWireframe() { Wireframe = !Wireframe; RequestNextFrameRendering(); }
    public void ToggleGrid() { ShowGrid = !ShowGrid; RequestNextFrameRendering(); }
    public void ToggleTextures() { Textures = !Textures; RequestNextFrameRendering(); }
    float _radius = 1, _yaw, _pitch, _dist = 1;
    Point _last;
    int _loadId;

    public bool HitTest(Point p) => true;

    public readonly GlBlur Blur = new();

    public async Task Load(string path)
    {
        var id = ++_loadId;
        AnimScene? scene = null;
        List<Part> parts;
        Vector3 min, max;
        List<(string, string)> info;
        if (Quake3.Handles(Path.GetExtension(path).ToLowerInvariant()))
            (parts, min, max, info) = await Task.Run(() => FromQuake(path));
        else if (AnimScene.Handles(Path.GetExtension(path).ToLowerInvariant()))
        {
            scene = await Task.Run(() => AnimScene.Load(path));
            (parts, min, max, info) = await Task.Run(() => FromScene(scene));
        }
        else
        {
            (parts, min, max, info) = await Task.Run(() => Import(path));
            scene = await Task.Run(() => ImportSkeleton(path));
            if (parts.Count == 0 && scene == null) throw new Exception("The file has no geometry or skeleton");
            if (scene != null)
            {
                var (bmin, bmax) = BoneBounds(scene);
                if (parts.Count == 0) (min, max) = (bmin, bmax);
                else (min, max) = (Vector3.Min(min, bmin), Vector3.Max(max, bmax));
                info.Add(("Joints", scene.Bones.Count.ToString("N0")));
                info.Add(("Frames", scene.FrameCount > 1 ? $"{scene.FirstFrame}–{scene.FirstFrame + scene.FrameCount - 1} ({scene.FrameCount})" : "static"));
                info.Add(("Frame rate", $"{scene.Fps:0.###} fps"));
            }
        }
        if (id != _loadId) return;
        _scene = scene;
        if (scene != null && scene.Meshes.Count == parts.Count) Outline = scene.Outline;
        else
        {
            // Assimp flattens the hierarchy (PreTransformVertices), so its outliner is a flat list of meshes.
            Outline = new OutlineNode { Name = Path.GetFileName(path), Type = "Root", Expanded = true };
            for (int i = 0; i < parts.Count; i++) Outline.Add(parts[i].Name, "Mesh").Meshes.Add(i);
            if (scene != null)
            {
                var skel = Outline.Add("Skeleton", "Skeleton");
                foreach (var c in scene.Outline.Children) { c.Parent = skel; skel.Children.Add(c); }
            }
        }
        Solo = null;
        _visible = Enumerable.Repeat(true, parts.Count).ToArray();
        _frame = _clockFrame = 0;
        _playing = IsAnimated;
        _clock.Restart();
        if (parts.Count == 0 && scene?.Bones.Count is null or 0) throw new Exception("The model has no triangles");
        _parts = _pending = parts;
        Info = info;
        _center = (min + max) / 2;
        _min = min;
        _gridDirty = true;
        _radius = MathF.Max(Vector3.Distance(min, max) / 2, 1e-4f);
        Reset();
    }

    // ---- Animated scenes (Alembic / USD): points and normals are recomputed per frame and streamed to the GPU.
    AnimScene? _scene;
    int _frame, _clockFrame;
    bool _playing, _frameDirty;
    readonly System.Diagnostics.Stopwatch _clock = new();

    public bool IsAnimated => _scene is { FrameCount: > 1 };
    int N => _scene?.FrameCount ?? 1;
    double SceneFps => _scene?.Fps ?? 24;
    public double Position => _frame / SceneFps;
    public double Duration => (N - 1) / SceneFps;
    public bool Paused => !_playing;
    public int FirstFrame => _scene?.FirstFrame ?? 0;
    public int FrameCount => IsAnimated ? N : 0;
    public int Frame => FirstFrame + _frame;

    public void TogglePause()
    {
        _playing = !_playing;
        _clockFrame = _frame;
        _clock.Restart();
        RequestNextFrameRendering();
    }

    public void Seek(double seconds) => SetFrame(_frame + (int)Math.Round(seconds * SceneFps));
    public void SeekFraction(double fraction, bool exact) => SetFrame((int)Math.Round(fraction * (N - 1)));
    public void StepFrame(int delta) { _playing = false; SetFrame(((_frame + delta) % N + N) % N); }

    void SetFrame(int k)
    {
        _frame = _clockFrame = Math.Clamp(k, 0, N - 1);
        _clock.Restart();
        _frameDirty = true;
        RequestNextFrameRendering();
    }

    static (List<Part>, Vector3, Vector3, List<(string, string)>) FromQuake(string path)
    {
        var (surfaces, info) = Quake3.Load(path);
        var parts = surfaces.Select(s => new Part(s.Verts, s.Idx, s.Rgba, s.W, s.H, Vector4.One) { Name = s.Texture, LmUv = s.LmUv, Lm = s.Lm }).ToList();
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var p in parts)
            for (int i = 0; i < p.Verts.Length; i += 8)
            {
                var v = new Vector3(p.Verts[i], p.Verts[i + 1], p.Verts[i + 2]);
                min = Vector3.Min(min, v);
                max = Vector3.Max(max, v);
            }
        return (parts, min, max, info);
    }

    static (List<Part>, Vector3, Vector3, List<(string, string)>) FromScene(AnimScene scene)
    {
        var parts = scene.Meshes.Select(m => new Part(new float[m.PointsAt(scene.Start).Length * 8], Array.ConvertAll(m.Tris, i => (uint)i), null, 0, 0, m.Color)).ToList();
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        foreach (var f in new[] { scene.FrameCount - 1, scene.FrameCount / 2, 0 }) // bounds over a few frames; frame 0 last so the parts start there
        {
            Evaluate(scene, f, parts);
            foreach (var p in parts)
            {
                Vector3 lo = new(float.MaxValue), hi = new(float.MinValue);
                for (int i = 0; i < p.Verts.Length; i += 8)
                {
                    var v = new Vector3(p.Verts[i], p.Verts[i + 1], p.Verts[i + 2]);
                    lo = Vector3.Min(lo, v);
                    hi = Vector3.Max(hi, v);
                }
                if (lo.X > hi.X) continue;
                foreach (var m in p.Inst) // corners of the local box under every placement
                    for (int c = 0; c < 8; c++)
                    {
                        var v = Vector3.Transform(new Vector3((c & 1) == 0 ? lo.X : hi.X, (c & 2) == 0 ? lo.Y : hi.Y, (c & 4) == 0 ? lo.Z : hi.Z), m);
                        min = Vector3.Min(min, v);
                        max = Vector3.Max(max, v);
                    }
            }
        }
        var info = new List<(string, string)>
        {
            ("Format", scene.Format),
            ("Meshes", parts.Count.ToString("N0")),
            ("Vertices", parts.Sum(p => p.Verts.Length / 8).ToString("N0")),
            ("Triangles", parts.Sum(p => p.Idx.Length / 3).ToString("N0")),
            ("Frames", scene.FrameCount > 1 ? $"{scene.FirstFrame}–{scene.FirstFrame + scene.FrameCount - 1} ({scene.FrameCount})" : "static"),
            ("Frame rate", $"{scene.Fps:0.###} fps"),
            ("Deforming", scene.Meshes.Count(m => m.Animated).ToString()),
            ("Instances", parts.Sum(p => p.Inst.Length).ToString("N0")),
        };
        return (parts, min, max, info);
    }

    // World-space points and smooth normals of every mesh at one frame, written into the parts' vertex arrays.
    // ponytail: CPU normals every frame; move to a compute pass if million-point caches stutter
    static void Evaluate(AnimScene scene, int frame, List<Part> parts)
    {
        double t = scene.TimeOf(frame);
        for (int m = 0; m < Math.Min(parts.Count, scene.Meshes.Count); m++)
        {
            var mesh = scene.Meshes[m];
            var pts = mesh.PointsAt(t);
            var v = parts[m].Verts;
            if (pts.Length * 8 != v.Length) continue; // point count changed mid-cache: keep the last good frame
            var world = mesh.Node.WorldAt(t) * scene.Root;
            var pos = new Vector3[pts.Length];
            if (mesh.Instances != null)
            {
                // Instanced: points stay in prototype space, the GPU applies each placement.
                pts.CopyTo(pos, 0);
                parts[m].Inst = Array.ConvertAll(mesh.Instances, p => p * world);
            }
            else
                for (int i = 0; i < pts.Length; i++) pos[i] = Vector3.Transform(pts[i], world);
            var nrm = new Vector3[pts.Length];
            var tris = mesh.Tris;
            for (int i = 0; i + 2 < tris.Length; i += 3)
            {
                var n = Vector3.Cross(pos[tris[i + 1]] - pos[tris[i]], pos[tris[i + 2]] - pos[tris[i]]);
                nrm[tris[i]] += n; nrm[tris[i + 1]] += n; nrm[tris[i + 2]] += n;
            }
            for (int i = 0; i < pts.Length; i++)
            {
                var n = nrm[i].LengthSquared() > 0 ? Vector3.Normalize(nrm[i]) : Vector3.UnitY;
                pos[i].CopyTo(v, i * 8);
                n.CopyTo(v, i * 8 + 3);
            }
        }
    }

    // ---- Outliner: per-part visibility from the tree's eye toggles and an optional solo subtree.
    public OutlineNode? Outline { get; private set; }
    public OutlineNode? Solo;
    bool[] _visible = [];

    public void UpdateVisibility()
    {
        if (Outline == null) return;
        var vis = new bool[_visible.Length];
        void Walk(OutlineNode n, bool shown, bool inSolo)
        {
            shown &= n.Visible;
            inSolo |= n == Solo;
            foreach (var m in n.Meshes) if (m < vis.Length) vis[m] = shown && (Solo == null || inSolo);
            foreach (var c in n.Children) Walk(c, shown, inSolo);
        }
        Walk(Outline, true, false);
        _visible = vis;
        RequestNextFrameRendering();
    }

    bool Shown(int part) => part >= _visible.Length || _visible[part];

    // ---- Skeletons (FBX, glTF, MD5 …): the raw node tree + first animation, sampled per frame into AnimNodes.
    public bool ShowSkeleton { get; private set; } = true;
    public void ToggleSkeleton() { ShowSkeleton = !ShowSkeleton; RequestNextFrameRendering(); }
    public bool HasSkeleton => _scene is { Bones.Count: > 0 };
    public bool ShowGeometry { get; private set; } = true;
    public void ToggleGeometry() { ShowGeometry = !ShowGeometry; RequestNextFrameRendering(); }
    uint _boneVao, _boneVbo;

    public static string DumpSkeleton(string path)
    {
        var sc = ImportSkeleton(path);
        if (sc == null) return "no skeleton";
        return string.Join(Environment.NewLine, sc.Outline.Descendants().Select(n => $"{n.Type,-6} {n.Name}")) + Environment.NewLine +
               string.Join(Environment.NewLine, sc.Bones.Select(b => $"bone {b.Parent.WorldAt(0).Translation} -> {b.Joint.WorldAt(0).Translation}"));
    }

    static unsafe AnimScene? ImportSkeleton(string path)
    {
        var ai = Ai.Assimp.GetApi();
        var s = ai.ImportFile(path, 0); // no post-processing: keep the node tree and animations
        if (s == null || s->MRootNode == null) return null;
        try
        {
            bool skinned = false;
            for (int i = 0; i < s->MNumMeshes; i++) skinned |= s->MMeshes[i]->MNumBones > 0;
            // Plain static models keep their flat mesh list; skeletons appear for animated, skinned or mesh-less files.
            if (s->MNumAnimations == 0 && !skinned && s->MNumMeshes > 0) return null;

            var scene = new AnimScene { Format = Path.GetExtension(path).TrimStart('.').ToUpperInvariant() };
            var anim = s->MNumAnimations > 0 ? s->MAnimations[0] : null;
            double tps = anim != null && anim->MTicksPerSecond > 0 ? anim->MTicksPerSecond : 24;
            double seconds = anim != null ? anim->MDuration / tps : 0;
            scene.Fps = tps is >= 1 and <= 240 ? tps : 30; // some exporters use huge tick rates: resample at 30
            int frames = Math.Max(1, (int)Math.Round(seconds * scene.Fps) + 1);
            scene.Start = 0;
            scene.End = (frames - 1) / scene.Fps;
            var times = Enumerable.Range(0, frames).Select(f => f / scene.Fps).ToArray();

            var channels = new Dictionary<string, nint>();
            for (int c = 0; anim != null && c < anim->MNumChannels; c++)
                channels[anim->MChannels[c]->MNodeName.AsString] = (nint)anim->MChannels[c];
            // Assimp splits each FBX joint into "<name>_$AssimpFbx$_Translation/_PreRotation/_Rotation…" helper nodes,
            // and the animation channels sit on those helpers. A joint is therefore known by its base name.
            static string Base(string n) => n.IndexOf("_$AssimpFbx$_", StringComparison.Ordinal) is var i and >= 0 ? n[..i] : n;
            var skin = new HashSet<string>();
            for (int i = 0; i < s->MNumMeshes; i++)
                for (int b = 0; b < s->MMeshes[i]->MNumBones; b++) skin.Add(s->MMeshes[i]->MBones[b]->MName.AsString);
            var joints = new HashSet<string>(channels.Keys.Select(Base).Concat(skin));
            var bones = new List<(AnimNode Joint, AnimNode Parent)>();
            var topmost = new HashSet<AnimNode>();

            // jointAbove: the nearest real (non-helper) joint up the tree; bones connect real joints, skipping helpers.
            void Walk(Ai.Node* n, AnimNode? parent, OutlineNode outParent, bool isRoot, AnimNode? jointAbove)
            {
                var name = n->MName.AsString;
                var node = new AnimNode { Parent = parent };
                if (channels.TryGetValue(name, out var ch))
                {
                    node.Times = times;
                    node.Locals = times.Select(t => Sample((Ai.NodeAnim*)ch, t * tps)).ToArray();
                }
                else node.Locals = [Matrix4x4.Transpose(n->MTransformation)]; // assimp matrices are column-vector
                bool helper = name.Contains("_$AssimpFbx$_");
                // End joints (Mixamo "HeadTop_End") have no channel but finish the chain they hang off.
                bool isJoint = !isRoot && !helper && (joints.Count == 0 || joints.Contains(name) || (n->MNumChildren == 0 && jointAbove != null));
                var outline = isRoot || helper ? outParent : outParent.Add(name, isJoint ? "Joint" : "Xform");
                if (isJoint)
                {
                    if (jointAbove != null) bones.Add((node, jointAbove));
                    else topmost.Add(node);
                }
                for (int i = 0; i < n->MNumChildren; i++) Walk(n->MChildren[i], node, outline, false, isJoint ? node : jointAbove);
            }
            Walk(s->MRootNode, null, scene.Outline, true, null);

            // A topmost joint at the origin with a single child is a rig container (Blender's armature object), not a bone.
            foreach (var top in topmost)
            {
                var kids = bones.Where(b => b.Parent == top).ToList();
                if (kids.Count == 1 && top.WorldAt(0).Translation.Length() < 1e-3f * (1 + kids[0].Joint.WorldAt(0).Translation.Length()))
                    bones.Remove(kids[0]);
            }
            scene.Bones = bones;
            return scene.Bones.Count > 0 || anim != null ? scene : null;
        }
        finally { ai.ReleaseImport(s); }
    }

    // Local matrix of an animated node at a time in ticks: lerp translation/scale, slerp rotation.
    static unsafe Matrix4x4 Sample(Ai.NodeAnim* ch, double tick)
    {
        Vector3 Vec(Ai.VectorKey* keys, uint n, Vector3 fallback)
        {
            if (n == 0) return fallback;
            if (n == 1 || tick <= keys[0].MTime) return keys[0].MValue;
            for (uint i = 0; i + 1 < n; i++)
                if (tick < keys[i + 1].MTime)
                {
                    var f = (float)((tick - keys[i].MTime) / (keys[i + 1].MTime - keys[i].MTime));
                    return Vector3.Lerp(keys[i].MValue, keys[i + 1].MValue, f);
                }
            return keys[n - 1].MValue;
        }
        Quaternion Rot(Ai.QuatKey* keys, uint n)
        {
            static Quaternion Q<T>(T q) where T : unmanaged => q switch
            {
                Quaternion x => x,
                _ => Unsafe(q),
            };
            if (n == 0) return Quaternion.Identity;
            if (n == 1 || tick <= keys[0].MTime) return Q(keys[0].MValue);
            for (uint i = 0; i + 1 < n; i++)
                if (tick < keys[i + 1].MTime)
                {
                    var f = (float)((tick - keys[i].MTime) / (keys[i + 1].MTime - keys[i].MTime));
                    return Quaternion.Slerp(Q(keys[i].MValue), Q(keys[i + 1].MValue), f);
                }
            return Q(keys[n - 1].MValue);
        }
        var t = Vec(ch->MPositionKeys, ch->MNumPositionKeys, Vector3.Zero);
        var r = Rot(ch->MRotationKeys, ch->MNumRotationKeys);
        var sc = Vec(ch->MScalingKeys, ch->MNumScalingKeys, Vector3.One);
        return Matrix4x4.CreateScale(sc) * Matrix4x4.CreateFromQuaternion(Quaternion.Normalize(r)) * Matrix4x4.CreateTranslation(t);
    }

    // AssimpQuaternion is (w, x, y, z) in memory.
    static unsafe Quaternion Unsafe<T>(T q) where T : unmanaged
    {
        var f = (float*)&q;
        return new Quaternion(f[1], f[2], f[3], f[0]);
    }

    static (Vector3, Vector3) BoneBounds(AnimScene scene)
    {
        Vector3 min = new(float.MaxValue), max = new(float.MinValue);
        // Joints are cheap, so frame the whole animation (up to 300 evenly spaced frames).
        int step = Math.Max(1, scene.FrameCount / 300);
        for (int f = 0; f < scene.FrameCount; f += step)
            foreach (var (j, _) in scene.Bones)
            {
                var p = (j.WorldAt(scene.TimeOf(f)) * scene.Root).Translation;
                min = Vector3.Min(min, p);
                max = Vector3.Max(max, p);
            }
        return (min, max);
    }

    // Bones as lines plus joints as points, drawn on top of everything (x-ray) in the accent color.
    unsafe void DrawSkeleton(GL gl, int colLoc, int unlitLoc)
    {
        if (!ShowSkeleton || _scene is not { Bones.Count: > 0 } sc) return;
        double t = sc.TimeOf(_frame);
        var v = new float[sc.Bones.Count * 6];
        for (int i = 0; i < sc.Bones.Count; i++)
        {
            var (j, p) = sc.Bones[i];
            (j.WorldAt(t) * sc.Root).Translation.CopyTo(v, i * 6);
            (p.WorldAt(t) * sc.Root).Translation.CopyTo(v, i * 6 + 3);
        }
        if (_boneVao == 0) { _boneVao = gl.GenVertexArray(); _boneVbo = gl.GenBuffer(); }
        gl.BindVertexArray(_boneVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _boneVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, v, BufferUsageARB.DynamicDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, (void*)0);
        gl.Disable(EnableCap.DepthTest);
        gl.Uniform1(unlitLoc, 1);
        gl.Uniform4(colLoc, 1f, 0.36f, 0.18f, 1f);
        gl.DrawArrays(PrimitiveType.Lines, 0, (uint)(v.Length / 3));
        gl.Uniform4(colLoc, 1f, 1f, 1f, 1f);
        gl.DrawArrays(PrimitiveType.Points, 0, (uint)(v.Length / 3));
        gl.Enable(EnableCap.DepthTest);
    }

    // Shown in the info panel.
    public List<(string Key, string Value)> Info { get; private set; } = [];

    public void Reset()
    {
        _target = _center;
        _yaw = 0.6f;
        _pitch = -0.35f;
        _dist = _radius * 3.2f; // the whole bounding sphere plus room for the bars
        RequestNextFrameRendering();
    }

    static unsafe (List<Part>, Vector3, Vector3, List<(string, string)>) Import(string path)
    {
        var ai = Ai.Assimp.GetApi();
        var flags = Ai.PostProcessSteps.Triangulate | Ai.PostProcessSteps.GenerateSmoothNormals |
                    Ai.PostProcessSteps.JoinIdenticalVertices | Ai.PostProcessSteps.PreTransformVertices |
                    Ai.PostProcessSteps.FlipUVs | Ai.PostProcessSteps.SortByPrimitiveType;
        var s = ai.ImportFile(path, (uint)flags);
        if (s == null) throw new Exception(ai.GetErrorStringS());
        try
        {
            var dir = Path.GetDirectoryName(path)!;
            var texCache = new Dictionary<string, (byte[], int, int)?>();
            var parts = new List<Part>();
            Vector3 min = new(float.MaxValue), max = new(float.MinValue);
            for (int i = 0; i < s->MNumMeshes; i++)
            {
                var m = s->MMeshes[i];
                if ((m->MPrimitiveTypes & (uint)Ai.PrimitiveType.Triangle) == 0) continue;
                var uv = m->MTextureCoords[0];
                var v = new float[m->MNumVertices * 8];
                for (int j = 0; j < m->MNumVertices; j++)
                {
                    var p = m->MVertices[j];
                    var n = m->MNormals != null ? m->MNormals[j] : Vector3.UnitY;
                    var t = uv != null ? uv[j] : default;
                    min = Vector3.Min(min, p);
                    max = Vector3.Max(max, p);
                    p.CopyTo(v, j * 8);
                    n.CopyTo(v, j * 8 + 3);
                    v[j * 8 + 6] = t.X;
                    v[j * 8 + 7] = t.Y;
                }
                var idx = new List<uint>((int)m->MNumFaces * 3);
                for (int f = 0; f < m->MNumFaces; f++)
                {
                    var face = m->MFaces[f];
                    if (face.MNumIndices == 3) idx.AddRange([face.MIndices[0], face.MIndices[1], face.MIndices[2]]);
                }

                var mat = s->MMaterials[m->MMaterialIndex];
                var color = Vector4.One;
                ai.GetMaterialColor(mat, "$clr.diffuse", 0, 0, &color);
                (byte[], int, int)? tex = null;
                Ai.AssimpString texPath;
                if (ai.GetMaterialTexture(mat, Ai.TextureType.Diffuse, 0, &texPath, null, null, null, null, null, null) == Ai.Return.Success ||
                    ai.GetMaterialTexture(mat, Ai.TextureType.BaseColor, 0, &texPath, null, null, null, null, null, null) == Ai.Return.Success)
                {
                    var key = texPath.AsString;
                    if (!texCache.TryGetValue(key, out tex)) texCache[key] = tex = LoadTexture(s, dir, key);
                }
                if (tex != null) color = Vector4.One;
                color.W = 1;
                parts.Add(new Part(v, [.. idx], tex?.Item1, tex?.Item2 ?? 0, tex?.Item3 ?? 0, color) { Name = m->MName.AsString is { Length: > 0 } nm ? nm : $"mesh_{i}" });
            }
            var size = max - min;
            var info = new List<(string, string)>
            {
                ("Format", Path.GetExtension(path).TrimStart('.').ToUpperInvariant()),
                ("Meshes", parts.Count.ToString("N0")),
                ("Vertices", parts.Sum(p => p.Verts.Length / 8).ToString("N0")),
                ("Triangles", parts.Sum(p => p.Idx.Length / 3).ToString("N0")),
                ("Material", s->MNumMaterials.ToString("N0")),
                ("Textures", $"{texCache.Count(t => t.Value != null)} found, {texCache.Count(t => t.Value == null)} missing"),
                ("Size", $"{size.X:0.###} × {size.Y:0.###} × {size.Z:0.###}"),
                ("Animations", s->MNumAnimations.ToString()),
            };
            return (parts, min, max, info);
        }
        finally { ai.ReleaseImport(s); }
    }

    static unsafe (byte[], int, int)? LoadTexture(Ai.Scene* s, string dir, string path)
    {
        try
        {
            // Embedded texture: "*N", or a file name that matches one stored inside the model.
            int embedded = path.StartsWith('*') ? int.Parse(path[1..]) : -1;
            for (int k = 0; embedded < 0 && k < s->MNumTextures; k++)
                if (Path.GetFileName(s->MTextures[k]->MFilename.AsString) is { Length: > 0 } fn && fn == Path.GetFileName(path)) embedded = k;
            if (embedded >= 0)
            {
                var t = s->MTextures[embedded];
                if (t->MHeight == 0) return Decode(new MagickImage(new ReadOnlySpan<byte>(t->PcData, (int)t->MWidth).ToArray()));
                int n = (int)(t->MWidth * t->MHeight);
                var src = (byte*)t->PcData;
                var rgba = new byte[n * 4];
                for (int i = 0; i < n; i++)
                {
                    rgba[i * 4] = src[i * 4 + 2];
                    rgba[i * 4 + 1] = src[i * 4 + 1];
                    rgba[i * 4 + 2] = src[i * 4];
                    rgba[i * 4 + 3] = src[i * 4 + 3];
                }
                return (rgba, (int)t->MWidth, (int)t->MHeight);
            }
            var file = FindFile(dir, path);
            return file == null ? null : Decode(new MagickImage(file));
        }
        catch { return null; }
    }

    // Game models often point at paths relative to the game root, or at .tga when the file is .pcx.
    static string? FindFile(string dir, string path)
    {
        path = path.Replace('/', '\\');
        foreach (var c in new[] { Path.Combine(dir, path), Path.Combine(dir, Path.GetFileName(path)) })
        {
            if (File.Exists(c)) return c;
            foreach (var ext in new[] { ".tga", ".png", ".jpg", ".pcx", ".dds", ".bmp" })
                if (File.Exists(Path.ChangeExtension(c, ext))) return Path.ChangeExtension(c, ext);
        }
        return null;
    }

    static (byte[], int, int) Decode(MagickImage img)
    {
        using (img) return (img.ToByteArray(MagickFormat.Rgba), (int)img.Width, (int)img.Height);
    }

    protected override void OnOpenGlInit(GlInterface glif)
    {
        var gl = _gl = GL.GetApi(glif.GetProcAddress);
        var head = glif.ContextInfo.Version.Type == GlProfileType.OpenGLES
            ? "#version 300 es\nprecision highp float;\n"
            : "#version 330 core\n";
        _prog = gl.CreateProgram();
        gl.AttachShader(_prog, Shader(gl, ShaderType.VertexShader, head + Vs));
        gl.AttachShader(_prog, Shader(gl, ShaderType.FragmentShader, head + Fs));
        gl.LinkProgram(_prog);
        _white = Texture(gl, [255, 255, 255, 255], 1, 1);
        if (glif.ContextInfo.Version.Type != GlProfileType.OpenGLES) gl.Enable((EnableCap)0x8642); // GL_PROGRAM_POINT_SIZE
        Blur.Init(gl, glif.ContextInfo.Version.Type == GlProfileType.OpenGLES);
        // The grid draws without instance attributes: give locations 3..6 an identity default (context state, set once).
        for (uint k = 0; k < 4; k++) gl.VertexAttrib4(3 + k, k == 0 ? 1f : 0, k == 1 ? 1f : 0, k == 2 ? 1f : 0, k == 3 ? 1f : 0);
        _pending = _parts;
    }

    static uint Shader(GL gl, ShaderType type, string src)
    {
        var sh = gl.CreateShader(type);
        gl.ShaderSource(sh, src);
        gl.CompileShader(sh);
        gl.GetShader(sh, ShaderParameterName.CompileStatus, out int ok);
        if (ok == 0) throw new Exception(gl.GetShaderInfoLog(sh));
        return sh;
    }

    static uint Texture(GL gl, byte[] rgba, int w, int h)
    {
        var t = gl.GenTexture();
        gl.BindTexture(TextureTarget.Texture2D, t);
        gl.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        gl.TexImage2D<byte>(TextureTarget.Texture2D, 0, InternalFormat.Rgba, (uint)w, (uint)h, 0, PixelFormat.Rgba, PixelType.UnsignedByte, rgba);
        gl.GenerateMipmap(TextureTarget.Texture2D);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.LinearMipmapLinear);
        gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
        return t;
    }

    static unsafe GpuPart Upload(GL gl, Part p)
    {
        var vao = gl.GenVertexArray();
        gl.BindVertexArray(vao);
        var vbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, vbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, p.Verts, BufferUsageARB.StaticDraw);
        var ebo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, ebo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, p.Idx, BufferUsageARB.StaticDraw);
        for (uint a = 0; a < 3; a++)
        {
            gl.EnableVertexAttribArray(a);
            gl.VertexAttribPointer(a, a == 2 ? 2 : 3, VertexAttribPointerType.Float, false, 32, (void*)(a * 12));
        }
        var g = new GpuPart(p, vao, vbo, ebo, p.Rgba != null ? Texture(gl, p.Rgba, p.TexW, p.TexH) : 0) { InstVbo = gl.GenBuffer() };
        UploadInstances(gl, g);
        InstanceAttribs(gl, g.InstVbo);
        if (p.LmUv != null && p.Lm != null)
        {
            g.LmVbo = gl.GenBuffer();
            gl.BindBuffer(BufferTargetARB.ArrayBuffer, g.LmVbo);
            gl.BufferData<float>(BufferTargetARB.ArrayBuffer, p.LmUv, BufferUsageARB.StaticDraw);
            gl.EnableVertexAttribArray(7);
            gl.VertexAttribPointer(7, 2, VertexAttribPointerType.Float, false, 8, (void*)0);
            g.LmTex = Texture(gl, p.Lm, 128, 128);
        }
        gl.BindVertexArray(0);
        return g;
    }

    static void UploadInstances(GL gl, GpuPart g)
    {
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, g.InstVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, System.Runtime.InteropServices.MemoryMarshal.Cast<Matrix4x4, float>(g.Part.Inst), BufferUsageARB.DynamicDraw);
    }

    // Locations 3..6 are the per-instance mat4. Numerics' row-major memory reads as its transpose, which is what the shader wants.
    static unsafe void InstanceAttribs(GL gl, uint instVbo)
    {
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, instVbo);
        for (uint k = 0; k < 4; k++)
        {
            gl.EnableVertexAttribArray(3 + k);
            gl.VertexAttribPointer(3 + k, 4, VertexAttribPointerType.Float, false, 64, (void*)(k * 16));
            gl.VertexAttribDivisor(3 + k, 1);
        }
    }

    // Unique triangle edges as a GL_LINES index list.
    static uint[] Edges(uint[] tri)
    {
        var seen = new HashSet<ulong>();
        var lines = new List<uint>();
        for (int i = 0; i + 2 < tri.Length; i += 3)
            for (int k = 0; k < 3; k++)
            {
                uint a = tri[i + k], b = tri[i + (k + 1) % 3];
                if (seen.Add(a < b ? (ulong)a << 32 | b : (ulong)b << 32 | a)) { lines.Add(a); lines.Add(b); }
            }
        return [.. lines];
    }

    static unsafe void BuildLines(GL gl, GpuPart g)
    {
        var idx = Edges(g.Part.Idx);
        g.LineVao = gl.GenVertexArray();
        gl.BindVertexArray(g.LineVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, g.Vbo);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 32, (void*)0);
        InstanceAttribs(gl, g.InstVbo);
        g.LineEbo = gl.GenBuffer();
        gl.BindBuffer(BufferTargetARB.ElementArrayBuffer, g.LineEbo);
        gl.BufferData<uint>(BufferTargetARB.ElementArrayBuffer, idx, BufferUsageARB.StaticDraw);
        gl.BindVertexArray(0);
        g.LineCount = idx.Length;
    }

    // Ground grid under the model, with a round step size that fits its scale.
    unsafe void BuildGrid(GL gl)
    {
        var step = MathF.Pow(10, MathF.Floor(MathF.Log10(_radius / 4)));
        int n = (int)MathF.Ceiling(_radius * 1.5f / step);
        float cx = MathF.Round(_center.X / step) * step, cz = MathF.Round(_center.Z / step) * step, y = _min.Y, half = n * step;
        var v = new List<float>();
        for (int i = -n; i <= n; i++)
        {
            v.AddRange([cx + i * step, y, cz - half, cx + i * step, y, cz + half]);
            v.AddRange([cx - half, y, cz + i * step, cx + half, y, cz + i * step]);
        }
        if (_gridVao == 0) { _gridVao = gl.GenVertexArray(); _gridVbo = gl.GenBuffer(); }
        gl.BindVertexArray(_gridVao);
        gl.BindBuffer(BufferTargetARB.ArrayBuffer, _gridVbo);
        gl.BufferData<float>(BufferTargetARB.ArrayBuffer, v.ToArray(), BufferUsageARB.StaticDraw);
        gl.EnableVertexAttribArray(0);
        gl.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, 12, (void*)0);
        gl.BindVertexArray(0);
        _gridCount = v.Count / 3;
        _gridDirty = false;
    }

    void FreeGpu(GL gl)
    {
        foreach (var g in _gpu)
        {
            if (g.LineVao != 0) { gl.DeleteVertexArray(g.LineVao); gl.DeleteBuffer(g.LineEbo); }
            gl.DeleteBuffer(g.InstVbo);
            if (g.LmVbo != 0) { gl.DeleteBuffer(g.LmVbo); gl.DeleteTexture(g.LmTex); }
            gl.DeleteVertexArray(g.Vao);
            gl.DeleteBuffer(g.Vbo);
            gl.DeleteBuffer(g.Ebo);
            if (g.Tex != 0) gl.DeleteTexture(g.Tex);
        }
        _gpu = [];
    }

    protected override void OnOpenGlDeinit(GlInterface glif)
    {
        if (_gl == null) return;
        FreeGpu(_gl);
        _gl.DeleteProgram(_prog);
        _gl.DeleteTexture(_white);
        if (_gridVao != 0) { _gl.DeleteVertexArray(_gridVao); _gl.DeleteBuffer(_gridVbo); _gridVao = 0; _gridDirty = true; }
        if (_boneVao != 0) { _gl.DeleteVertexArray(_boneVao); _gl.DeleteBuffer(_boneVbo); _boneVao = 0; }
        Blur.Deinit();
        _gl = null;
    }

    protected override unsafe void OnOpenGlRender(GlInterface glif, int fb)
    {
        var gl = _gl!;
        if (_pending != null)
        {
            FreeGpu(gl);
            _gpu = _pending.Select(p => Upload(gl, p)).ToList();
            _pending = null;
        }
        if (_scene != null && IsAnimated && _parts != null)
        {
            if (_playing)
            {
                int k = (_clockFrame + (int)(_clock.Elapsed.TotalSeconds * _scene.Fps)) % N;
                if (k != _frame) { _frame = k; _frameDirty = true; }
            }
            if (_frameDirty)
            {
                Evaluate(_scene, _frame, _parts);
                foreach (var g in _gpu)
                {
                    gl.BindBuffer(BufferTargetARB.ArrayBuffer, g.Vbo);
                    gl.BufferSubData<float>(BufferTargetARB.ArrayBuffer, 0, g.Part.Verts);
                    if (g.Part.Inst.Length > 1) UploadInstances(gl, g);
                }
                _frameDirty = false;
            }
            if (_playing) RequestNextFrameRendering();
        }
        var sc = VisualRoot?.RenderScaling ?? 1;
        int w = Math.Max(1, (int)(Bounds.Width * sc)), h = Math.Max(1, (int)(Bounds.Height * sc));
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        gl.Viewport(0, 0, (uint)w, (uint)h);
        gl.ClearColor(0.055f, 0.055f, 0.055f, 1);
        gl.Enable(EnableCap.DepthTest);
        gl.DepthMask(true);
        gl.Disable(EnableCap.CullFace);
        gl.Disable(EnableCap.Blend);
        gl.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);

        var rot = Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0);
        var eye = _target + Vector3.Transform(new Vector3(0, 0, _dist), rot);
        var mvp = Matrix4x4.CreateLookAt(eye, _target, Vector3.UnitY) *
                  Matrix4x4.CreatePerspectiveFieldOfView(0.8f, w / (float)h, _dist * 0.01f, _dist + _radius * 4);
        gl.UseProgram(_prog);
        gl.UniformMatrix4(gl.GetUniformLocation(_prog, "mvp"), 1, false, (float*)&mvp);
        gl.Uniform3(gl.GetUniformLocation(_prog, "eye"), eye.X, eye.Y, eye.Z);
        gl.Uniform1(gl.GetUniformLocation(_prog, "tex"), 0);
        var colLoc = gl.GetUniformLocation(_prog, "col");
        var unlitLoc = gl.GetUniformLocation(_prog, "unlit");
        var useLmLoc = gl.GetUniformLocation(_prog, "useLm");
        gl.Uniform1(gl.GetUniformLocation(_prog, "lm"), 1);
        gl.Uniform1(useLmLoc, 0);
        gl.ActiveTexture(TextureUnit.Texture0);

        if (ShowGrid && _gpu.Count > 0)
        {
            if (_gridDirty) BuildGrid(gl);
            gl.Uniform1(unlitLoc, 1);
            gl.Uniform4(colLoc, 0.22f, 0.22f, 0.22f, 1f);
            gl.BindVertexArray(_gridVao);
            gl.DrawArrays(PrimitiveType.Lines, 0, (uint)_gridCount);
        }

        if (!ShowGeometry) { } // hidden: only grid and skeleton
        else if (Wireframe)
        {
            gl.Uniform1(unlitLoc, 1);
            gl.Uniform4(colLoc, 0.78f, 0.78f, 0.78f, 1f);
            for (int i = 0; i < _gpu.Count; i++)
            {
                var g = _gpu[i];
                if (!Shown(i)) continue;
                if (g.LineVao == 0) BuildLines(gl, g);
                gl.BindVertexArray(g.LineVao);
                gl.DrawElementsInstanced(PrimitiveType.Lines, (uint)g.LineCount, DrawElementsType.UnsignedInt, null, (uint)g.Part.Inst.Length);
            }
        }
        else
        {
            gl.Uniform1(unlitLoc, 0);
            for (int i = 0; i < _gpu.Count; i++)
            {
                var g = _gpu[i];
                if (!Shown(i)) continue;
                // Textures off: plain white clay, so the shape reads without materials.
                var c = Textures ? g.Part.Color : Vector4.One;
                gl.Uniform1(useLmLoc, g.LmTex != 0 ? 1 : 0);
                if (g.LmTex != 0) { gl.ActiveTexture(TextureUnit.Texture1); gl.BindTexture(TextureTarget.Texture2D, g.LmTex); gl.ActiveTexture(TextureUnit.Texture0); }
                gl.BindTexture(TextureTarget.Texture2D, Textures && g.Tex != 0 ? g.Tex : _white);
                gl.Uniform4(colLoc, c.X, c.Y, c.Z, c.W);
                gl.BindVertexArray(g.Vao);
                gl.DrawElementsInstanced(PrimitiveType.Triangles, (uint)g.Part.Idx.Length, DrawElementsType.UnsignedInt, null, (uint)g.Part.Inst.Length);
            }
        }
        DrawSkeleton(gl, colLoc, unlitLoc);
        gl.BindVertexArray(0);
        Blur.Apply(fb, w, h, sc);
    }

    // Only a single press starts camera navigation: the second press of a double-click toggles fullscreen,
    // and the window resizing under it would otherwise read as a huge drag (camera flipping to look straight down).
    bool _dragging;

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        _last = e.GetPosition(this);
        _dragging = e.ClickCount < 2;
        if (_dragging) e.Pointer.Capture(this);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        _dragging = false;
        e.Pointer.Capture(null);
    }

    protected override void OnSizeChanged(Avalonia.Controls.SizeChangedEventArgs e)
    {
        base.OnSizeChanged(e);
        _dragging = false;
    }

    // Set when the view is dragged, so a Space press used for Houdini-style navigation doesn't also toggle playback.
    public bool Navigated;

    float MinDist => _radius * 0.05f;

    // Dolly by a factor (< 1 = in). Steps never shrink below those at MinDist, and dollying past it carries
    // the pivot forward along the view, so you can travel through big scenes (Quake maps) instead of stalling.
    void Dolly(float factor)
    {
        float step = MathF.Max(_dist, MinDist) * (1 - factor);
        float dist = _dist - step;
        if (dist < MinDist)
        {
            var forward = -Vector3.Transform(Vector3.UnitZ, Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0));
            _target += forward * (MinDist - dist);
            dist = MinDist;
        }
        _dist = dist;
    }

    // Houdini viewport: LMB tumble, MMB track (pan), RMB dolly. Works with or without Space/Alt held.
    protected override void OnPointerMoved(PointerEventArgs e)
    {
        var pt = e.GetCurrentPoint(this);
        float dx = (float)(pt.Position.X - _last.X), dy = (float)(pt.Position.Y - _last.Y);
        _last = pt.Position;
        if (!_dragging) return;
        if (pt.Properties.IsLeftButtonPressed)
        {
            _yaw -= dx * 0.01f;
            _pitch = Math.Clamp(_pitch - dy * 0.01f, -1.55f, 1.55f);
        }
        else if (pt.Properties.IsMiddleButtonPressed)
        {
            var rot = Matrix4x4.CreateFromYawPitchRoll(_yaw, _pitch, 0);
            var k = MathF.Max(_dist, MinDist) * 0.0015f; // never slower than at the minimum distance
            _target += (Vector3.Transform(Vector3.UnitY, rot) * dy - Vector3.Transform(Vector3.UnitX, rot) * dx) * k;
        }
        else if (pt.Properties.IsRightButtonPressed)
            Dolly(MathF.Pow(0.998f, dx - dy)); // drag right/up = dolly in
        else return;
        Navigated = true;
        RequestNextFrameRendering();
    }

    protected override void OnPointerWheelChanged(PointerWheelEventArgs e)
    {
        Dolly(MathF.Pow(0.85f, (float)e.Delta.Y));
        RequestNextFrameRendering();
        e.Handled = true;
    }
}
