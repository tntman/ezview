using System.Numerics;

namespace EzView;

// Gallery thumbnails for 3D files: the model loads as for viewing, then a small software rasterizer
// draws it from the viewport's default angle (2× supersampled, z-buffered, textured, headlight shading).
partial class ModelView
{
    static readonly object ThumbLock = new(); // one model at a time: importers are heavy and not all thread-safe

    public static byte[]? Thumbnail(string path, int w, int h)
    {
        lock (ThumbLock)
        {
            List<Part> parts;
            Vector3 min, max;
            var ext = Path.GetExtension(path).ToLowerInvariant();
            if (Quake3.Handles(ext)) (parts, min, max, _) = FromQuake(path);
            else if (AnimScene.Handles(ext)) (parts, min, max, _) = FromScene(AnimScene.Load(path));
            else (_, parts, min, max, _) = ImportAssimp(path);
            if (parts.Count == 0 || min.X > max.X) return null;
            return Rasterize(parts, (min + max) / 2, MathF.Max(Vector3.Distance(min, max) / 2, 1e-4f), w, h);
        }
    }

    static byte[] Rasterize(List<Part> parts, Vector3 center, float radius, int outW, int outH)
    {
        const int S = 2;
        int W = outW * S, H = outH * S;
        float dist = radius * 2.0f; // tighter than the viewport: no bars to leave room for
        var eye = center + Vector3.Transform(new Vector3(0, 0, dist), Matrix4x4.CreateFromYawPitchRoll(0.6f, -0.35f, 0));
        var viewProj = Matrix4x4.CreateLookAt(eye, center, Vector3.UnitY) *
                       Matrix4x4.CreatePerspectiveFieldOfView(0.8f, W / (float)H, dist * 0.01f, dist + radius * 4);
        var light = Vector3.Normalize(eye - center + new Vector3(0, radius, 0));
        var depth = new float[W * H];
        Array.Fill(depth, float.MaxValue);
        var color = new byte[W * H * 4];

        foreach (var part in parts)
        {
            int n = part.Verts.Length / 8;
            var screen = new Vector3[n];
            var normal = new Vector3[n];
            // ponytail: at most 2000 instances per part; enough to read a scattered scene at thumbnail size
            foreach (var inst in part.Inst.Take(2000))
            {
                var m = inst * viewProj;
                for (int i = 0; i < n; i++)
                {
                    var p = new Vector3(part.Verts[i * 8], part.Verts[i * 8 + 1], part.Verts[i * 8 + 2]);
                    var c = Vector4.Transform(new Vector4(p, 1), m);
                    screen[i] = c.W <= 1e-6f ? new Vector3(float.NaN) : new Vector3((c.X / c.W * 0.5f + 0.5f) * W, (0.5f - c.Y / c.W * 0.5f) * H, c.Z / c.W);
                    var nv = Vector3.TransformNormal(new Vector3(part.Verts[i * 8 + 3], part.Verts[i * 8 + 4], part.Verts[i * 8 + 5]), inst);
                    normal[i] = nv.LengthSquared() > 0 ? Vector3.Normalize(nv) : Vector3.UnitY;
                }
                for (int t = 0; t + 2 < part.Idx.Length; t += 3)
                    Triangle(part, (int)part.Idx[t], (int)part.Idx[t + 1], (int)part.Idx[t + 2], screen, normal, light, depth, color, W, H);
            }
        }

        // 2×2 box filter down to the output size.
        var result = new byte[outW * outH * 4];
        for (int y = 0; y < outH; y++)
            for (int x = 0; x < outW; x++)
                for (int c = 0; c < 4; c++)
                {
                    int sum = 0;
                    for (int dy = 0; dy < S; dy++)
                        for (int dx = 0; dx < S; dx++) sum += color[((y * S + dy) * W + x * S + dx) * 4 + c];
                    result[(y * outW + x) * 4 + c] = (byte)(sum / (S * S));
                }
        return result;
    }

    static void Triangle(Part part, int i0, int i1, int i2, Vector3[] s, Vector3[] nrm, Vector3 light, float[] depth, byte[] color, int W, int H)
    {
        Vector3 a = s[i0], b = s[i1], c = s[i2];
        if (float.IsNaN(a.X) || float.IsNaN(b.X) || float.IsNaN(c.X)) return;
        float area = (b.X - a.X) * (c.Y - a.Y) - (b.Y - a.Y) * (c.X - a.X);
        if (MathF.Abs(area) < 1e-9f) return;
        int x0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)))), x1 = Math.Min(W - 1, (int)MathF.Ceiling(MathF.Max(a.X, MathF.Max(b.X, c.X))));
        int y0 = Math.Max(0, (int)MathF.Floor(MathF.Min(a.Y, MathF.Min(b.Y, c.Y)))), y1 = Math.Min(H - 1, (int)MathF.Ceiling(MathF.Max(a.Y, MathF.Max(b.Y, c.Y))));
        if (x0 > x1 || y0 > y1) return;
        var v = part.Verts;
        for (int y = y0; y <= y1; y++)
            for (int x = x0; x <= x1; x++)
            {
                float px = x + 0.5f, py = y + 0.5f;
                float w0 = ((b.X - px) * (c.Y - py) - (b.Y - py) * (c.X - px)) / area;
                float w1 = ((c.X - px) * (a.Y - py) - (c.Y - py) * (a.X - px)) / area;
                float w2 = 1 - w0 - w1;
                if (w0 < 0 || w1 < 0 || w2 < 0) continue;
                float z = w0 * a.Z + w1 * b.Z + w2 * c.Z;
                int pi = y * W + x;
                if (z >= depth[pi] || z < 0) continue;
                depth[pi] = z;
                var albedo = part.Color;
                if (part.Rgba != null && part.TexW > 0)
                {
                    float u = w0 * v[i0 * 8 + 6] + w1 * v[i1 * 8 + 6] + w2 * v[i2 * 8 + 6];
                    float t = w0 * v[i0 * 8 + 7] + w1 * v[i1 * 8 + 7] + w2 * v[i2 * 8 + 7];
                    int tx = (int)((u - MathF.Floor(u)) * part.TexW) % part.TexW, ty = (int)((t - MathF.Floor(t)) * part.TexH) % part.TexH;
                    int ti = (ty * part.TexW + tx) * 4;
                    albedo *= new Vector4(part.Rgba[ti], part.Rgba[ti + 1], part.Rgba[ti + 2], 255) / 255f;
                }
                var n = Vector3.Normalize(w0 * nrm[i0] + w1 * nrm[i1] + w2 * nrm[i2]);
                float shade = 0.3f + 0.7f * MathF.Abs(Vector3.Dot(n, light)); // two-sided headlight
                int ci = pi * 4;
                color[ci] = (byte)Math.Clamp(albedo.Z * shade * 255, 0, 255);
                color[ci + 1] = (byte)Math.Clamp(albedo.Y * shade * 255, 0, 255);
                color[ci + 2] = (byte)Math.Clamp(albedo.X * shade * 255, 0, 255);
                color[ci + 3] = 255;
            }
    }
}
