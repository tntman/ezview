using Silk.NET.OpenGL;

namespace EzView;

// Real frosted glass for the GL views: after the scene is drawn into Avalonia's framebuffer,
// downsample it to 1/4, blur it (separable gaussian, two rounds) and draw it back only in the
// strips under the top bar and player bar. All on the GPU, no readback.
unsafe class GlBlur
{
    // Strip heights in DIPs and the overlay's current opacity; set by MainWindow.
    public double Top, Bottom, Opacity;

    const int Down = 4;
    const string Vs = """
        out vec2 uv;
        void main() {
            vec2 p = vec2(float((gl_VertexID << 1) & 2), float(gl_VertexID & 2));  // fullscreen triangle
            uv = p;
            gl_Position = vec4(p * 2.0 - 1.0, 0.0, 1.0);
        }
        """;
    const string Fs = """
        in vec2 uv;
        uniform sampler2D tex;
        uniform vec2 dir;       // texel step for a blur pass, (0,0) for the final composite
        uniform float alpha;
        out vec4 o;
        void main() {
            if (dir == vec2(0.0)) { o = vec4(texture(tex, uv).rgb, alpha); return; }
            vec3 c = texture(tex, uv).rgb * 0.227027;
            c += (texture(tex, uv + dir * 1.384615).rgb + texture(tex, uv - dir * 1.384615).rgb) * 0.316216;
            c += (texture(tex, uv + dir * 3.230769).rgb + texture(tex, uv - dir * 3.230769).rgb) * 0.070270;
            o = vec4(c, 1.0);
        }
        """;

    GL? _gl;
    uint _prog, _vao;
    readonly uint[] _fbo = new uint[2], _tex = new uint[2];
    int _sw, _sh, _locDir, _locAlpha;

    public void Init(GL gl, bool es)
    {
        _gl = gl;
        var head = es ? "#version 300 es\nprecision highp float;\n" : "#version 330 core\n";
        _prog = gl.CreateProgram();
        foreach (var (type, src) in new[] { (ShaderType.VertexShader, Vs), (ShaderType.FragmentShader, Fs) })
        {
            var sh = gl.CreateShader(type);
            gl.ShaderSource(sh, head + src);
            gl.CompileShader(sh);
            gl.GetShader(sh, ShaderParameterName.CompileStatus, out int ok);
            if (ok == 0) throw new Exception(gl.GetShaderInfoLog(sh));
            gl.AttachShader(_prog, sh);
        }
        gl.LinkProgram(_prog);
        _locDir = gl.GetUniformLocation(_prog, "dir");
        _locAlpha = gl.GetUniformLocation(_prog, "alpha");
        _vao = gl.GenVertexArray();
        for (int i = 0; i < 2; i++)
        {
            _fbo[i] = gl.GenFramebuffer();
            _tex[i] = gl.GenTexture();
        }
    }

    public void Deinit()
    {
        if (_gl is not { } gl) return;
        gl.DeleteProgram(_prog);
        gl.DeleteVertexArray(_vao);
        gl.DeleteFramebuffers(_fbo);
        gl.DeleteTextures(_tex);
        _gl = null;
        _sw = _sh = 0;
    }

    void Resize(int sw, int sh)
    {
        var gl = _gl!;
        for (int i = 0; i < 2; i++)
        {
            gl.BindTexture(TextureTarget.Texture2D, _tex[i]);
            gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, (uint)sw, (uint)sh, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Linear);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo[i]);
            gl.FramebufferTexture2D(FramebufferTarget.Framebuffer, FramebufferAttachment.ColorAttachment0, TextureTarget.Texture2D, _tex[i], 0);
        }
        (_sw, _sh) = (sw, sh);
    }

    // fb is the framebuffer that already holds the rendered scene (w x h physical pixels).
    public void Apply(int fb, int w, int h, double scaling)
    {
        if (_gl is not { } gl || Opacity < 0.01 || w < Down || h < Down) return;
        int top = (int)Math.Ceiling(Top * scaling), bottom = (int)Math.Ceiling(Bottom * scaling);
        if (top <= 0 && bottom <= 0) return;
        int sw = w / Down, sh = h / Down;
        if (sw != _sw || sh != _sh) Resize(sw, sh);

        gl.Disable(EnableCap.DepthTest);
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.Blend);

        // Downsample the scene with linear filtering.
        gl.BindFramebuffer(FramebufferTarget.ReadFramebuffer, (uint)fb);
        gl.BindFramebuffer(FramebufferTarget.DrawFramebuffer, _fbo[0]);
        gl.BlitFramebuffer(0, 0, w, h, 0, 0, sw, sh, ClearBufferMask.ColorBufferBit, BlitFramebufferFilter.Linear);

        gl.UseProgram(_prog);
        gl.BindVertexArray(_vao);
        gl.ActiveTexture(TextureUnit.Texture0);
        gl.Uniform1(gl.GetUniformLocation(_prog, "tex"), 0);
        gl.Viewport(0, 0, (uint)sw, (uint)sh);
        for (int round = 0; round < 2; round++)
        {
            Pass(gl, 0, 1, 1f / sw, 0);   // horizontal: tex0 -> fbo1
            Pass(gl, 1, 0, 0, 1f / sh);   // vertical:   tex1 -> fbo0
        }

        // Composite the blurred image into the strips only. GL's origin is bottom-left, so the top bar strip is at y = h - top.
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, (uint)fb);
        gl.Viewport(0, 0, (uint)w, (uint)h);
        gl.BindTexture(TextureTarget.Texture2D, _tex[0]);
        gl.Uniform2(_locDir, 0f, 0f);
        gl.Uniform1(_locAlpha, (float)Opacity);
        gl.Enable(EnableCap.Blend);
        gl.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        gl.Enable(EnableCap.ScissorTest);
        if (top > 0) { gl.Scissor(0, h - top, (uint)w, (uint)top); gl.DrawArrays(PrimitiveType.Triangles, 0, 3); }
        if (bottom > 0) { gl.Scissor(0, 0, (uint)w, (uint)bottom); gl.DrawArrays(PrimitiveType.Triangles, 0, 3); }
        gl.Disable(EnableCap.ScissorTest);
        gl.Disable(EnableCap.Blend);
        gl.BindVertexArray(0);
        gl.UseProgram(0);
    }

    void Pass(GL gl, int src, int dst, float dx, float dy)
    {
        gl.BindFramebuffer(FramebufferTarget.Framebuffer, _fbo[dst]);
        gl.BindTexture(TextureTarget.Texture2D, _tex[src]);
        gl.Uniform2(_locDir, dx, dy);
        gl.Uniform1(_locAlpha, 1f);
        gl.DrawArrays(PrimitiveType.Triangles, 0, 3);
    }
}
