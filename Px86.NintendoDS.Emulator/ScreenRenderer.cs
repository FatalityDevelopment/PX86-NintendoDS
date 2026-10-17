using Silk.NET.OpenGL;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// Draws the two DS screens from the 256×384 frame texture (top screen in
    /// the upper half) as two quads placed by a <see cref="ScreenLayout"/>.
    /// Nearest-neighbour or linear filtering. OpenGL 3.3 core: one VAO, one
    /// shader, one BGRA texture; the quad vertices are rebuilt per draw.
    /// </summary>
    public sealed unsafe class ScreenRenderer : IDisposable
    {
        private const int TextureWidth = 256;
        private const int TextureHeight = 384;

        private const string VertexShader = """
            #version 330 core
            layout(location = 0) in vec2 position;
            layout(location = 1) in vec2 uv;
            out vec2 vUv;
            void main() { vUv = uv; gl_Position = vec4(position, 0.0, 1.0); }
            """;

        private const string FragmentShader = """
            #version 330 core
            in vec2 vUv;
            out vec4 color;
            uniform sampler2D screen;
            void main() { color = vec4(texture(screen, vUv).rgb, 1.0); }
            """;

        private readonly GL _gl;
        private readonly uint _vao;
        private readonly uint _vbo;
        private readonly uint _program;
        private readonly uint _texture;
        private readonly float[] _vertices = new float[2 * 4 * 4];
        private bool _linear;

        public ScreenRenderer(GL gl)
        {
            _gl = gl;
            _program = CreateProgram();

            _vao = _gl.GenVertexArray();
            _gl.BindVertexArray(_vao);
            _vbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(_vertices.Length * sizeof(float)), null, BufferUsageARB.DynamicDraw);
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
            _gl.EnableVertexAttribArray(1);

            _texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
            SetFilter(false);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, (int)TextureWrapMode.ClampToEdge);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, (int)TextureWrapMode.ClampToEdge);
            _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, TextureWidth, TextureHeight, 0,
                PixelFormat.Bgra, PixelType.UnsignedByte, null);
        }

        private uint CreateProgram()
        {
            uint Compile(ShaderType type, string source)
            {
                uint shader = _gl.CreateShader(type);
                _gl.ShaderSource(shader, source);
                _gl.CompileShader(shader);
                string log = _gl.GetShaderInfoLog(shader);
                if (!string.IsNullOrWhiteSpace(log))
                    Console.Error.WriteLine($"[GL] {type}: {log}");
                return shader;
            }

            uint vs = Compile(ShaderType.VertexShader, VertexShader);
            uint fs = Compile(ShaderType.FragmentShader, FragmentShader);
            uint program = _gl.CreateProgram();
            _gl.AttachShader(program, vs);
            _gl.AttachShader(program, fs);
            _gl.LinkProgram(program);
            _gl.DeleteShader(vs);
            _gl.DeleteShader(fs);
            return program;
        }

        /// <summary>Linear (smooth) or nearest-neighbour (sharp pixels) scaling.</summary>
        public void SetFilter(bool linear)
        {
            _linear = linear;
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
            int filter = (int)(linear ? TextureMinFilter.Linear : TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, filter);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, filter);
        }

        public bool LinearFilter => _linear;

        public void Upload(uint[] frame)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
            fixed (uint* data = frame)
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, TextureWidth, TextureHeight,
                    PixelFormat.Bgra, PixelType.UnsignedByte, data);
        }

        /// <summary>Clear the window and draw the screens where <paramref name="layout"/> puts them inside <paramref name="area"/>.</summary>
        public void Draw(int windowWidth, int windowHeight, ScreenLayout layout, ScreenRect area)
        {
            // ImGui leaves scissoring/blending on after drawing its windows.
            _gl.Disable(EnableCap.ScissorTest);
            _gl.Disable(EnableCap.Blend);
            _gl.Viewport(0, 0, (uint)Math.Max(windowWidth, 1), (uint)Math.Max(windowHeight, 1));
            _gl.ClearColor(0f, 0f, 0f, 1f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            var (top, bottom) = layout.Compute(area);
            int quads = 0;
            if (!top.IsEmpty) AddQuad(quads++, top, layout.CornerUvs(0f, 0.5f), windowWidth, windowHeight);
            if (!bottom.IsEmpty) AddQuad(quads++, bottom, layout.CornerUvs(0.5f, 1f), windowWidth, windowHeight);
            if (quads == 0)
                return;

            _gl.UseProgram(_program);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
            _gl.BindVertexArray(_vao);
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (float* data = _vertices)
                _gl.BufferSubData(BufferTargetARB.ArrayBuffer, 0, (nuint)(quads * 16 * sizeof(float)), data);
            for (int q = 0; q < quads; q++)
                _gl.DrawArrays(PrimitiveType.TriangleStrip, q * 4, 4);
        }

        /// <summary>Quad corners as a triangle strip: top-left, bottom-left, top-right, bottom-right.</summary>
        private void AddQuad(int index, ScreenRect r, (float U, float V)[] uv, int windowWidth, int windowHeight)
        {
            float NdcX(float x) => x / windowWidth * 2 - 1;
            float NdcY(float y) => 1 - y / windowHeight * 2;
            float l = NdcX(r.X), rt = NdcX(r.X + r.Width), t = NdcY(r.Y), b = NdcY(r.Y + r.Height);
            int o = index * 16;
            void Put(int i, float x, float y, (float U, float V) c)
            {
                _vertices[o + i * 4] = x;
                _vertices[o + i * 4 + 1] = y;
                _vertices[o + i * 4 + 2] = c.U;
                _vertices[o + i * 4 + 3] = c.V;
            }
            Put(0, l, t, uv[0]);
            Put(1, l, b, uv[2]);
            Put(2, rt, t, uv[1]);
            Put(3, rt, b, uv[3]);
        }

        public void Dispose()
        {
            _gl.DeleteTexture(_texture);
            _gl.DeleteBuffer(_vbo);
            _gl.DeleteVertexArray(_vao);
            _gl.DeleteProgram(_program);
        }
    }
}
