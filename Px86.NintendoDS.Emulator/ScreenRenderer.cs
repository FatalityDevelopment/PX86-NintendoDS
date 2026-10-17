using Silk.NET.OpenGL;

namespace Px86.NintendoDS.Emulator
{
    /// <summary>
    /// Draws the 256×384 frame (both screens) as one texture, scaled to fit the
    /// window with the aspect ratio kept (nearest-neighbour, letterboxed).
    /// OpenGL 3.3 core: one VAO, one shader, one BGRA texture.
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
            void main() { color = texture(screen, vUv); }
            """;

        private readonly GL _gl;
        private readonly uint _vao;
        private readonly uint _vbo;
        private readonly uint _program;
        private readonly uint _texture;
        private int _windowWidth = 1, _windowHeight = 1;
        private (int X, int Y, int W, int H) _viewport;

        public ScreenRenderer(GL gl)
        {
            _gl = gl;

            _program = CreateProgram();

            float[] quad =
            {
                // x, y, u, v (v = 0 at the top row of the frame)
                -1f,  1f, 0f, 0f,
                -1f, -1f, 0f, 1f,
                 1f,  1f, 1f, 0f,
                 1f, -1f, 1f, 1f,
            };
            _vao = _gl.GenVertexArray();
            _gl.BindVertexArray(_vao);
            _vbo = _gl.GenBuffer();
            _gl.BindBuffer(BufferTargetARB.ArrayBuffer, _vbo);
            fixed (float* data = quad)
                _gl.BufferData(BufferTargetARB.ArrayBuffer, (nuint)(quad.Length * sizeof(float)), data, BufferUsageARB.StaticDraw);
            _gl.VertexAttribPointer(0, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)0);
            _gl.EnableVertexAttribArray(0);
            _gl.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, 4 * sizeof(float), (void*)(2 * sizeof(float)));
            _gl.EnableVertexAttribArray(1);

            _texture = _gl.GenTexture();
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
            _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
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

        public void Resize(int width, int height)
        {
            _windowWidth = Math.Max(width, 1);
            _windowHeight = Math.Max(height, 1);
            double scale = Math.Min(_windowWidth / (double)TextureWidth, _windowHeight / (double)TextureHeight);
            int w = (int)(TextureWidth * scale), h = (int)(TextureHeight * scale);
            _viewport = ((_windowWidth - w) / 2, (_windowHeight - h) / 2, w, h);
        }

        /// <summary>Window pixel → DS frame pixel (0–255, 0–383), or null outside the picture.</summary>
        public (int X, int Y)? WindowToScreen(float x, float y)
        {
            var (vx, vy, vw, vh) = _viewport;
            // Window coordinates have y down; the viewport is in GL coordinates (y up).
            float topY = _windowHeight - (vy + vh);
            int sx = (int)((x - vx) * TextureWidth / vw);
            int sy = (int)((y - topY) * TextureHeight / vh);
            if (sx < 0 || sx >= TextureWidth || sy < 0 || sy >= TextureHeight)
                return null;
            return (sx, sy);
        }

        public void Upload(uint[] frame)
        {
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
            fixed (uint* data = frame)
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, TextureWidth, TextureHeight,
                    PixelFormat.Bgra, PixelType.UnsignedByte, data);
        }

        public void Draw()
        {
            _gl.Viewport(0, 0, (uint)_windowWidth, (uint)_windowHeight);
            _gl.ClearColor(0f, 0f, 0f, 1f);
            _gl.Clear(ClearBufferMask.ColorBufferBit);

            var (x, y, w, h) = _viewport;
            _gl.Viewport(x, y, (uint)w, (uint)h);
            _gl.UseProgram(_program);
            _gl.ActiveTexture(TextureUnit.Texture0);
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
            _gl.BindVertexArray(_vao);
            _gl.DrawArrays(PrimitiveType.TriangleStrip, 0, 4);
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
