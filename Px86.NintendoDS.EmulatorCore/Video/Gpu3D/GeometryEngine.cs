namespace Px86.NintendoDS.EmulatorCore.Video.Gpu3D
{
    /// <summary>
    /// The 3D geometry engine: command FIFO, matrices, lighting, vertex
    /// transformation, polygon assembly and clipping.
    ///
    /// Commands arrive through GXFIFO (04000400h, packed: four command bytes
    /// then their parameters) or the command ports (04000440h–040005FFh, one
    /// port per command). Each command runs as soon as its parameters are
    /// complete. After SWAP_BUFFERS the engine stalls until VBlank, and later
    /// commands wait in the FIFO, as on hardware.
    ///
    ///   10h MTX_MODE  11h PUSH  12h POP  13h STORE  14h RESTORE  15h IDENTITY
    ///   16h LOAD_4x4  17h LOAD_4x3  18h MULT_4x4  19h MULT_4x3  1Ah MULT_3x3
    ///   1Bh SCALE  1Ch TRANS  20h COLOR  21h NORMAL  22h TEXCOORD  23h VTX_16
    ///   24h VTX_10  25h VTX_XY  26h VTX_XZ  27h VTX_YZ  28h VTX_DIFF
    ///   29h POLYGON_ATTR  2Ah TEXIMAGE_PARAM  2Bh PLTT_BASE  30h DIF_AMB
    ///   31h SPE_EMI  32h LIGHT_VECTOR  33h LIGHT_COLOR  34h SHININESS
    ///   40h BEGIN_VTXS  41h END_VTXS  50h SWAP_BUFFERS  60h VIEWPORT
    ///   70h BOX_TEST  71h POS_TEST  72h VEC_TEST
    ///
    /// Matrices are 4×4 of signed 20.12 fixed point, vectors are row vectors
    /// (v' = v × M), and MTX_MULT computes C = M × C.
    ///
    /// Reference: GBATEK "DS 3D Geometry Commands", "DS 3D Matrix …",
    /// "DS 3D Polygon Definitions by Vertices", "DS 3D Polygon Light Parameters",
    /// "DS 3D Texture Coordinates", "DS 3D Tests", "DS 3D Status".
    /// </summary>
    public sealed class GeometryEngine
    {
        public const int MaxPolygons = 2048;
        public const int MaxVertices = 6144;
        public const int FifoSize = 256;

        private static readonly int[] ParameterCount = BuildParameterCounts();

        private static int[] BuildParameterCounts()
        {
            var counts = new int[256];
            Array.Fill(counts, -1); // invalid
            counts[0x00] = 0;
            counts[0x10] = 1; counts[0x11] = 0; counts[0x12] = 1; counts[0x13] = 1; counts[0x14] = 1;
            counts[0x15] = 0; counts[0x16] = 16; counts[0x17] = 12; counts[0x18] = 16; counts[0x19] = 12;
            counts[0x1A] = 9; counts[0x1B] = 3; counts[0x1C] = 3;
            counts[0x20] = 1; counts[0x21] = 1; counts[0x22] = 1; counts[0x23] = 2; counts[0x24] = 1;
            counts[0x25] = 1; counts[0x26] = 1; counts[0x27] = 1; counts[0x28] = 1; counts[0x29] = 1;
            counts[0x2A] = 1; counts[0x2B] = 1;
            counts[0x30] = 1; counts[0x31] = 1; counts[0x32] = 1; counts[0x33] = 1; counts[0x34] = 32;
            counts[0x40] = 1; counts[0x41] = 0; counts[0x50] = 1; counts[0x60] = 1;
            counts[0x70] = 3; counts[0x71] = 2; counts[0x72] = 1;
            return counts;
        }

        // ── FIFO ──────────────────────────────────────────────────────────────

        private readonly Queue<(byte Command, uint Parameter)> _fifo = new();
        private readonly Queue<byte> _packed = new();
        private int _packedParamsLeft;

        private readonly uint[] _params = new uint[32];
        private int _paramCount;
        private byte _currentCommand;
        private bool _hasCurrentCommand;

        /// <summary>SWAP_BUFFERS issued: the engine is halted until VBlank.</summary>
        public bool SwapPending { get; private set; }

        public int FifoCount => _fifo.Count;

        /// <summary>Raised when the FIFO drains below half (for GXFIFO DMA and IRQ).</summary>
        public event Action? FifoDrained;

        // ── Matrices ──────────────────────────────────────────────────────────

        private int _matrixMode;
        private readonly int[] _projection = Identity();
        private readonly int[] _position = Identity();
        private readonly int[] _vector = Identity();
        private readonly int[] _texture = Identity();
        private readonly int[] _clip = Identity();
        private bool _clipDirty;

        private readonly int[] _projectionStack = new int[16];
        private readonly int[][] _positionStack = NewStack(32);
        private readonly int[][] _vectorStack = NewStack(32);
        private readonly int[] _textureStack = new int[16];
        private int _projectionSp;
        private int _positionSp;
        private bool _stackError;

        private static int[] Identity() => new[] { 4096, 0, 0, 0, 0, 4096, 0, 0, 0, 0, 4096, 0, 0, 0, 0, 4096 };

        private static int[][] NewStack(int depth)
        {
            var stack = new int[depth][];
            for (int i = 0; i < depth; i++) stack[i] = Identity();
            return stack;
        }

        // ── Vertex / polygon state ────────────────────────────────────────────

        private int _vx, _vy, _vz;            // last vertex (4.12)
        private int _colorR = 63, _colorG = 63, _colorB = 63;
        private int _rawS, _rawT, _s, _t;
        private uint _polygonAttrPending, _polygonAttr;
        private uint _textureParams, _paletteBase;
        private int _primitive;
        private bool _inList;
        private readonly Vertex3D[] _strip = new Vertex3D[4];
        private int _stripCount;
        private int _listVertexCount;
        private bool _stripOdd;

        private int _viewportX1, _viewportY1, _viewportX2 = 255, _viewportY2 = 191;

        // Lighting
        private readonly int[][] _lightVector = { new int[3], new int[3], new int[3], new int[3] };
        private readonly int[][] _halfVector = { new int[3], new int[3], new int[3], new int[3] };
        private readonly int[][] _lightColor = { new int[3], new int[3], new int[3], new int[3] };
        private readonly int[] _diffuse = new int[3], _ambient = new int[3], _specular = new int[3], _emission = new int[3];
        private bool _shininessTableEnabled;
        private readonly byte[] _shininess = new byte[128];

        // Tests
        public bool BoxTestResult { get; private set; }
        public int[] PositionTestResult { get; } = new int[4];
        public short[] VectorTestResult { get; } = new short[3];

        // Polygon RAM: one set filled by geometry, one set handed to the renderer.
        private List<Polygon3D> _building = new();
        private int _buildingVertices;
        private bool _swapWBuffer, _swapManualSort;

        /// <summary>The polygon list handed to the renderer at the last swap.</summary>
        public List<Polygon3D> RenderList { get; private set; } = new();
        public bool RenderWBuffer { get; private set; }
        public bool RenderManualSort { get; private set; }

        public int PolygonCount => _building.Count;
        public int VertexCount => _buildingVertices;
        public bool RamOverflow { get; set; }

        public int[] ClipMatrix
        {
            get
            {
                UpdateClip();
                return _clip;
            }
        }

        public int[] VectorMatrix => _vector;

        public int PositionStackPointer => _positionSp;
        public int ProjectionStackPointer => _projectionSp;
        public bool StackError
        {
            get => _stackError;
            set => _stackError = value;
        }

        public void Reset()
        {
            _fifo.Clear();
            _packed.Clear();
            _packedParamsLeft = 0;
            _hasCurrentCommand = false;
            _paramCount = 0;
            SwapPending = false;
            _matrixMode = 0;
            Identity().CopyTo(_projection, 0);
            Identity().CopyTo(_position, 0);
            Identity().CopyTo(_vector, 0);
            Identity().CopyTo(_texture, 0);
            _clipDirty = true;
            _projectionSp = _positionSp = 0;
            _stackError = false;
            _building = new List<Polygon3D>();
            RenderList = new List<Polygon3D>();
            _buildingVertices = 0;
            _inList = false;
            _viewportX1 = 0; _viewportY1 = 0; _viewportX2 = 255; _viewportY2 = 191;
            RamOverflow = false;
        }

        // ── Input ─────────────────────────────────────────────────────────────

        /// <summary>Write to GXFIFO (04000400h–0400043Fh): packed commands and their parameters.</summary>
        public void WriteFifo(uint value)
        {
            if (_packedParamsLeft == 0 && _packed.Count == 0)
            {
                for (int i = 0; i < 4; i++)
                {
                    byte command = (byte)(value >> (i * 8));
                    if (command == 0 && i > 0)
                        break;
                    if (command != 0)
                        _packed.Enqueue(command);
                }
                FlushZeroParameterCommands();
                return;
            }

            if (_packed.Count == 0)
                return;

            Enqueue(_packed.Peek(), value);
            if (--_packedParamsLeft == 0)
            {
                _packed.Dequeue();
                FlushZeroParameterCommands();
            }
        }

        private void FlushZeroParameterCommands()
        {
            while (_packed.Count > 0)
            {
                byte command = _packed.Peek();
                int count = ParameterCount[command];
                if (count > 0)
                {
                    _packedParamsLeft = count;
                    return;
                }
                _packed.Dequeue();
                if (count == 0)
                    Enqueue(command, 0);
            }
            _packedParamsLeft = 0;
        }

        /// <summary>Write to a command port (04000440h–040005FFh).</summary>
        public void WritePort(uint offset, uint value)
        {
            byte command = (byte)((offset - 0x400) >> 2);
            if (ParameterCount[command] < 0)
                return;
            Enqueue(command, value);
        }

        private void Enqueue(byte command, uint parameter)
        {
            _fifo.Enqueue((command, parameter));
            Process();
        }

        /// <summary>Execute queued commands until the FIFO is empty or a swap stalls the engine.</summary>
        private void Process()
        {
            bool wasHalfFull = _fifo.Count >= FifoSize / 2;
            while (!SwapPending && _fifo.Count > 0)
            {
                var (command, parameter) = _fifo.Dequeue();
                if (!_hasCurrentCommand || _currentCommand != command)
                {
                    _currentCommand = command;
                    _hasCurrentCommand = true;
                    _paramCount = 0;
                }

                int needed = ParameterCount[command];
                if (needed > 0)
                    _params[_paramCount++] = parameter;
                if (_paramCount >= needed)
                {
                    _hasCurrentCommand = false;
                    _paramCount = 0;
                    Execute(command);
                }
            }
            if (wasHalfFull || _fifo.Count < FifoSize / 2)
                FifoDrained?.Invoke();
        }

        /// <summary>VBlank: perform a pending swap and resume command processing.</summary>
        public void OnVBlank()
        {
            if (!SwapPending)
                return;
            RenderList = _building;
            RenderWBuffer = _swapWBuffer;
            RenderManualSort = _swapManualSort;
            _building = new List<Polygon3D>(RenderList.Count);
            _buildingVertices = 0;
            SwapPending = false;
            Process();
        }

        // ── Command execution ─────────────────────────────────────────────────

        private void Execute(byte command)
        {
            uint p0 = _params[0];
            switch (command)
            {
                case 0x10: _matrixMode = (int)(p0 & 3); break;
                case 0x11: Push(); break;
                case 0x12: Pop((int)(p0 << 26) >> 26); break;
                case 0x13: Store((int)(p0 & 0x1F)); break;
                case 0x14: Restore((int)(p0 & 0x1F)); break;
                case 0x15: LoadMatrix(Identity()); break;
                case 0x16: LoadMatrix(ReadMatrix4x4()); break;
                case 0x17: LoadMatrix(ReadMatrix4x3()); break;
                case 0x18: MultiplyMatrix(ReadMatrix4x4(), scale: false); break;
                case 0x19: MultiplyMatrix(ReadMatrix4x3(), scale: false); break;
                case 0x1A: MultiplyMatrix(ReadMatrix3x3(), scale: false); break;
                case 0x1B:
                {
                    var m = Identity();
                    m[0] = (int)_params[0]; m[5] = (int)_params[1]; m[10] = (int)_params[2];
                    MultiplyMatrix(m, scale: true);
                    break;
                }
                case 0x1C:
                {
                    var m = Identity();
                    m[12] = (int)_params[0]; m[13] = (int)_params[1]; m[14] = (int)_params[2];
                    MultiplyMatrix(m, scale: false);
                    break;
                }
                case 0x20: SetColor(p0); break;
                case 0x21: Normal(p0); break;
                case 0x22: TexCoord(p0); break;
                case 0x23: SubmitVertex((short)p0, (short)(p0 >> 16), (short)_params[1]); break;
                case 0x24: SubmitVertex(Signed10(p0) << 6, Signed10(p0 >> 10) << 6, Signed10(p0 >> 20) << 6); break;
                case 0x25: SubmitVertex((short)p0, (short)(p0 >> 16), _vz); break;
                case 0x26: SubmitVertex((short)p0, _vy, (short)(p0 >> 16)); break;
                case 0x27: SubmitVertex(_vx, (short)p0, (short)(p0 >> 16)); break;
                case 0x28:
                    SubmitVertex((short)(_vx + (Signed10(p0) << 3)),
                                 (short)(_vy + (Signed10(p0 >> 10) << 3)),
                                 (short)(_vz + (Signed10(p0 >> 20) << 3)));
                    break;
                case 0x29: _polygonAttrPending = p0; break;
                case 0x2A: _textureParams = p0; break;
                case 0x2B: _paletteBase = p0 & 0x1FFF; break;
                case 0x30:
                    _diffuse[0] = (int)(p0 & 0x1F); _diffuse[1] = (int)(p0 >> 5) & 0x1F; _diffuse[2] = (int)(p0 >> 10) & 0x1F;
                    _ambient[0] = (int)(p0 >> 16) & 0x1F; _ambient[1] = (int)(p0 >> 21) & 0x1F; _ambient[2] = (int)(p0 >> 26) & 0x1F;
                    if ((p0 & 0x8000) != 0)
                        SetColor(p0);
                    break;
                case 0x31:
                    _specular[0] = (int)(p0 & 0x1F); _specular[1] = (int)(p0 >> 5) & 0x1F; _specular[2] = (int)(p0 >> 10) & 0x1F;
                    _shininessTableEnabled = (p0 & 0x8000) != 0;
                    _emission[0] = (int)(p0 >> 16) & 0x1F; _emission[1] = (int)(p0 >> 21) & 0x1F; _emission[2] = (int)(p0 >> 26) & 0x1F;
                    break;
                case 0x32: LightVector(p0); break;
                case 0x33:
                {
                    int light = (int)(p0 >> 30);
                    _lightColor[light][0] = (int)(p0 & 0x1F);
                    _lightColor[light][1] = (int)(p0 >> 5) & 0x1F;
                    _lightColor[light][2] = (int)(p0 >> 10) & 0x1F;
                    break;
                }
                case 0x34:
                    for (int i = 0; i < 32; i++)
                        for (int b = 0; b < 4; b++)
                            _shininess[i * 4 + b] = (byte)(_params[i] >> (b * 8));
                    break;
                case 0x40: BeginVertices(p0); break;
                case 0x41: break; // END_VTXS has no effect
                case 0x50:
                    _swapManualSort = (p0 & 1) != 0;
                    _swapWBuffer = (p0 & 2) != 0;
                    _inList = false;
                    SwapPending = true;
                    break;
                case 0x60:
                    _viewportX1 = (int)(p0 & 0xFF);
                    _viewportY1 = (int)(p0 >> 8) & 0xFF;
                    _viewportX2 = (int)(p0 >> 16) & 0xFF;
                    _viewportY2 = (int)(p0 >> 24) & 0xFF;
                    break;
                case 0x70: BoxTest(); break;
                case 0x71: PositionTest((short)p0, (short)(p0 >> 16), (short)_params[1]); break;
                case 0x72: VectorTest(p0); break;
            }
        }

        private static int Signed10(uint value) => ((int)(value << 22)) >> 22;

        // ── Matrix operations ─────────────────────────────────────────────────

        private int[] ReadMatrix4x4()
        {
            var m = new int[16];
            for (int i = 0; i < 16; i++) m[i] = (int)_params[i];
            return m;
        }

        private int[] ReadMatrix4x3()
        {
            var m = Identity();
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 3; col++)
                    m[row * 4 + col] = (int)_params[row * 3 + col];
            return m;
        }

        private int[] ReadMatrix3x3()
        {
            var m = Identity();
            for (int row = 0; row < 3; row++)
                for (int col = 0; col < 3; col++)
                    m[row * 4 + col] = (int)_params[row * 3 + col];
            return m;
        }

        /// <summary>result = a × b (both 4×4, 20.12).</summary>
        public static void Multiply(int[] a, int[] b, int[] result)
        {
            Span<int> tmp = stackalloc int[16];
            for (int row = 0; row < 4; row++)
                for (int col = 0; col < 4; col++)
                {
                    long sum = 0;
                    for (int k = 0; k < 4; k++)
                        sum += (long)a[row * 4 + k] * b[k * 4 + col];
                    tmp[row * 4 + col] = (int)(sum >> 12);
                }
            tmp.CopyTo(result);
        }

        private void LoadMatrix(int[] m)
        {
            switch (_matrixMode)
            {
                case 0: m.CopyTo(_projection, 0); break;
                case 1: m.CopyTo(_position, 0); break;
                case 2: m.CopyTo(_position, 0); m.CopyTo(_vector, 0); break;
                default: m.CopyTo(_texture, 0); break;
            }
            _clipDirty = true;
        }

        private void MultiplyMatrix(int[] m, bool scale)
        {
            switch (_matrixMode)
            {
                case 0: Multiply(m, _projection, _projection); break;
                case 1: Multiply(m, _position, _position); break;
                case 2:
                    Multiply(m, _position, _position);
                    if (!scale) Multiply(m, _vector, _vector);
                    break;
                default: Multiply(m, _texture, _texture); break;
            }
            _clipDirty = true;
        }

        private void UpdateClip()
        {
            if (!_clipDirty) return;
            Multiply(_position, _projection, _clip);
            _clipDirty = false;
        }

        private void Push()
        {
            switch (_matrixMode)
            {
                case 0:
                    if (_projectionSp > 0) _stackError = true;
                    _projection.CopyTo(_projectionStack, 0);
                    _projectionSp = 1;
                    break;
                case 1:
                case 2:
                    if (_positionSp >= 31) _stackError = true;
                    _position.CopyTo(_positionStack[_positionSp & 31], 0);
                    _vector.CopyTo(_vectorStack[_positionSp & 31], 0);
                    _positionSp = (_positionSp + 1) & 63;
                    break;
                default:
                    _texture.CopyTo(_textureStack, 0);
                    break;
            }
        }

        private void Pop(int offset)
        {
            switch (_matrixMode)
            {
                case 0:
                    if (_projectionSp == 0) _stackError = true;
                    _projectionSp = 0;
                    _projectionStack.CopyTo(_projection, 0);
                    break;
                case 1:
                case 2:
                    _positionSp = (_positionSp - offset) & 63;
                    if (_positionSp >= 31) _stackError = true;
                    _positionStack[_positionSp & 31].CopyTo(_position, 0);
                    _vectorStack[_positionSp & 31].CopyTo(_vector, 0);
                    break;
                default:
                    _textureStack.CopyTo(_texture, 0);
                    break;
            }
            _clipDirty = true;
        }

        private void Store(int index)
        {
            switch (_matrixMode)
            {
                case 0: _projection.CopyTo(_projectionStack, 0); break;
                case 1:
                case 2:
                    if (index == 31) _stackError = true;
                    _position.CopyTo(_positionStack[index], 0);
                    _vector.CopyTo(_vectorStack[index], 0);
                    break;
                default: _texture.CopyTo(_textureStack, 0); break;
            }
        }

        private void Restore(int index)
        {
            switch (_matrixMode)
            {
                case 0: _projectionStack.CopyTo(_projection, 0); break;
                case 1:
                case 2:
                    if (index == 31) _stackError = true;
                    _positionStack[index].CopyTo(_position, 0);
                    _vectorStack[index].CopyTo(_vector, 0);
                    break;
                default: _textureStack.CopyTo(_texture, 0); break;
            }
            _clipDirty = true;
        }

        // ── Colour, lighting, texture coordinates ─────────────────────────────

        private static int Expand5To6(int value) => value == 0 ? 0 : value * 2 + 1;

        private void SetColor(uint value)
        {
            _colorR = Expand5To6((int)(value & 0x1F));
            _colorG = Expand5To6((int)(value >> 5) & 0x1F);
            _colorB = Expand5To6((int)(value >> 10) & 0x1F);
        }

        private void TexCoord(uint value)
        {
            _rawS = (short)value;
            _rawT = (short)(value >> 16);
            if ((_textureParams >> 30) == 1)
            {
                // (S, T, 1/16, 1/16) × texture matrix, first two columns.
                _s = (int)(((long)_rawS * _texture[0] + (long)_rawT * _texture[4] + _texture[8] + _texture[12]) >> 12);
                _t = (int)(((long)_rawS * _texture[1] + (long)_rawT * _texture[5] + _texture[9] + _texture[13]) >> 12);
            }
            else
            {
                _s = _rawS;
                _t = _rawT;
            }
        }

        private void Normal(uint value)
        {
            int nx = Signed10(value), ny = Signed10(value >> 10), nz = Signed10(value >> 20); // 1.9

            if ((_textureParams >> 30) == 2)
            {
                _s = (int)(((long)nx * _texture[0] + (long)ny * _texture[4] + (long)nz * _texture[8]) >> 21) + _rawS;
                _t = (int)(((long)nx * _texture[1] + (long)ny * _texture[5] + (long)nz * _texture[9]) >> 21) + _rawT;
            }

            // Normal × directional matrix (upper-left 3×3), in 1.12.
            int x = nx << 3, y = ny << 3, z = nz << 3;
            int tx = (int)(((long)x * _vector[0] + (long)y * _vector[4] + (long)z * _vector[8]) >> 12);
            int ty = (int)(((long)x * _vector[1] + (long)y * _vector[5] + (long)z * _vector[9]) >> 12);
            int tz = (int)(((long)x * _vector[2] + (long)y * _vector[6] + (long)z * _vector[10]) >> 12);

            int r = _emission[0], g = _emission[1], b = _emission[2];
            int lights = (int)(_polygonAttr & 0xF);
            for (int i = 0; i < 4; i++)
            {
                if ((lights & (1 << i)) == 0)
                    continue;
                var l = _lightVector[i];
                int diffuse = Math.Clamp(-(l[0] * tx + l[1] * ty + l[2] * tz) >> 12, 0, 4096);

                var h = _halfVector[i];
                long dot = Math.Max(0, -(h[0] * (long)tx + h[1] * (long)ty + h[2] * (long)tz) >> 12);
                int shine = (int)Math.Min(4096, (dot * dot) >> 12);
                if (_shininessTableEnabled)
                    shine = _shininess[Math.Min(127, shine >> 5)] << 4;

                var c = _lightColor[i];
                r += (_specular[0] * c[0] * shine >> 17) + (_diffuse[0] * c[0] * diffuse >> 17) + (_ambient[0] * c[0] >> 5);
                g += (_specular[1] * c[1] * shine >> 17) + (_diffuse[1] * c[1] * diffuse >> 17) + (_ambient[1] * c[1] >> 5);
                b += (_specular[2] * c[2] * shine >> 17) + (_diffuse[2] * c[2] * diffuse >> 17) + (_ambient[2] * c[2] >> 5);
            }

            _colorR = Expand5To6(Math.Min(r, 31));
            _colorG = Expand5To6(Math.Min(g, 31));
            _colorB = Expand5To6(Math.Min(b, 31));
        }

        private void LightVector(uint value)
        {
            int light = (int)(value >> 30);
            int x = Signed10(value) << 3, y = Signed10(value >> 10) << 3, z = Signed10(value >> 20) << 3;
            var l = _lightVector[light];
            l[0] = (int)(((long)x * _vector[0] + (long)y * _vector[4] + (long)z * _vector[8]) >> 12);
            l[1] = (int)(((long)x * _vector[1] + (long)y * _vector[5] + (long)z * _vector[9]) >> 12);
            l[2] = (int)(((long)x * _vector[2] + (long)y * _vector[6] + (long)z * _vector[10]) >> 12);
            var h = _halfVector[light];
            h[0] = l[0] / 2;
            h[1] = l[1] / 2;
            h[2] = (l[2] - 4096) / 2; // line of sight is fixed at (0, 0, −1)
        }

        // ── Vertices and polygons ─────────────────────────────────────────────

        private void BeginVertices(uint primitive)
        {
            _primitive = (int)(primitive & 3);
            _polygonAttr = _polygonAttrPending;
            _inList = true;
            _stripCount = 0;
            _listVertexCount = 0;
            _stripOdd = false;
        }

        private void SubmitVertex(int x, int y, int z)
        {
            _vx = x; _vy = y; _vz = z;
            if (!_inList)
                return;

            UpdateClip();
            var v = new Vertex3D
            {
                X = (int)(((long)x * _clip[0] + (long)y * _clip[4] + (long)z * _clip[8] + 4096L * _clip[12]) >> 12),
                Y = (int)(((long)x * _clip[1] + (long)y * _clip[5] + (long)z * _clip[9] + 4096L * _clip[13]) >> 12),
                Z = (int)(((long)x * _clip[2] + (long)y * _clip[6] + (long)z * _clip[10] + 4096L * _clip[14]) >> 12),
                W = (int)(((long)x * _clip[3] + (long)y * _clip[7] + (long)z * _clip[11] + 4096L * _clip[15]) >> 12),
                R = _colorR, G = _colorG, B = _colorB,
            };

            if ((_textureParams >> 30) == 3)
            {
                v.S = (int)(((long)x * _texture[0] + (long)y * _texture[4] + (long)z * _texture[8]) >> 24) + _rawS;
                v.T = (int)(((long)x * _texture[1] + (long)y * _texture[5] + (long)z * _texture[9]) >> 24) + _rawT;
            }
            else
            {
                v.S = _s;
                v.T = _t;
            }

            _listVertexCount++;
            switch (_primitive)
            {
                case 0: // separate triangles
                    _strip[_stripCount++] = v;
                    if (_stripCount == 3)
                    {
                        EmitPolygon(_strip[0], _strip[1], _strip[2]);
                        _stripCount = 0;
                    }
                    break;
                case 1: // separate quads
                    _strip[_stripCount++] = v;
                    if (_stripCount == 4)
                    {
                        EmitPolygon(_strip[0], _strip[1], _strip[2], _strip[3]);
                        _stripCount = 0;
                    }
                    break;
                case 2: // triangle strip
                    if (_stripCount < 2)
                    {
                        _strip[_stripCount++] = v;
                        break;
                    }
                    if (_stripOdd)
                        EmitPolygon(_strip[1], _strip[0], v);
                    else
                        EmitPolygon(_strip[0], _strip[1], v);
                    _stripOdd = !_stripOdd;
                    _strip[0] = _strip[1];
                    _strip[1] = v;
                    break;
                default: // quad strip: v0 v1 v3 v2 per quad
                    if (_stripCount < 3)
                    {
                        _strip[_stripCount++] = v;
                        break;
                    }
                    EmitPolygon(_strip[0], _strip[1], v, _strip[2]);
                    _strip[0] = _strip[2];
                    _strip[1] = v;
                    _stripCount = 2;
                    break;
            }
        }

        private void EmitPolygon(params Vertex3D[] input)
        {
            uint attr = _polygonAttr;

            // Facing from the clip-space (x, y, w) determinant.
            double det =
                (double)input[0].X * ((double)input[1].Y * input[2].W - (double)input[2].Y * input[1].W) -
                (double)input[0].Y * ((double)input[1].X * input[2].W - (double)input[2].X * input[1].W) +
                (double)input[0].W * ((double)input[1].X * input[2].Y - (double)input[2].X * input[1].Y);
            bool front = det >= 0;
            bool lineSegment = det == 0;
            if (!lineSegment)
            {
                if (front && (attr & 0x80) == 0) return;
                if (!front && (attr & 0x40) == 0) return;
            }

            // Far-plane intersecting polygons are hidden unless bit 12 is set.
            if ((attr & 0x1000) == 0)
                foreach (var v in input)
                    if (v.Z > v.W) return;

            var clipped = Clipper.Clip(input);
            if (clipped.Count < 3)
                return;

            if (_building.Count >= MaxPolygons || _buildingVertices + clipped.Count > MaxVertices)
            {
                RamOverflow = true;
                return;
            }

            var polygon = new Polygon3D
            {
                Vertices = clipped.ToArray(),
                VertexCount = clipped.Count,
                Attributes = attr,
                TextureParams = _textureParams,
                PaletteBase = _paletteBase,
                WBuffer = _swapWBuffer,
                FrontFacing = front,
            };

            int alpha = polygon.Alpha;
            int format = (int)(_textureParams >> 26) & 7;
            polygon.Translucent = (alpha != 0 && alpha != 31) || format == 1 || format == 6;

            ProjectToScreen(polygon);
            _building.Add(polygon);
            _buildingVertices += clipped.Count;
        }

        private void ProjectToScreen(Polygon3D polygon)
        {
            int width = _viewportX2 - _viewportX1 + 1;
            int height = _viewportY2 - _viewportY1 + 1;
            polygon.MinY = int.MaxValue;
            polygon.MaxY = int.MinValue;

            for (int i = 0; i < polygon.VertexCount; i++)
            {
                ref var v = ref polygon.Vertices[i];
                long w = v.W == 0 ? 1 : v.W;
                long sx = ((v.X + w) * width) / (2 * w) + _viewportX1;
                long syUp = ((v.Y + w) * height) / (2 * w) + _viewportY1;
                v.ScreenX = (int)Math.Clamp(sx, 0, 256);
                v.ScreenY = (int)Math.Clamp(191 - syUp, 0, 192);

                if (polygon.WBuffer)
                {
                    v.Depth = (int)Math.Clamp(w, 0, 0xFFFFFF);
                }
                else
                {
                    long z = ((v.Z * 0x4000L) / w + 0x3FFF) * 0x200;
                    v.Depth = (int)Math.Clamp(z, 0, 0xFFFFFF);
                }

                polygon.MinY = Math.Min(polygon.MinY, v.ScreenY);
                polygon.MaxY = Math.Max(polygon.MaxY, v.ScreenY);
            }
        }

        // ── Tests ─────────────────────────────────────────────────────────────

        private void BoxTest()
        {
            int x = (short)_params[0], y = (short)(_params[0] >> 16), z = (short)_params[1];
            int w = (short)(_params[1] >> 16), h = (short)_params[2], d = (short)(_params[2] >> 16);
            UpdateClip();

            Span<(int X, int Y, int Z, int W)> corners = stackalloc (int, int, int, int)[8];
            for (int i = 0; i < 8; i++)
            {
                int cx = x + ((i & 1) != 0 ? w : 0), cy = y + ((i & 2) != 0 ? h : 0), cz = z + ((i & 4) != 0 ? d : 0);
                corners[i] = (Transform(cx, cy, cz, 0), Transform(cx, cy, cz, 1), Transform(cx, cy, cz, 2), Transform(cx, cy, cz, 3));
            }

            // Inside if not all corners are outside the same plane (conservative, like the hardware's face test).
            bool outside = false;
            for (int plane = 0; plane < 6 && !outside; plane++)
            {
                bool allOut = true;
                foreach (var c in corners)
                {
                    int value = plane switch { 0 or 1 => c.X, 2 or 3 => c.Y, _ => c.Z };
                    bool isOut = (plane & 1) == 0 ? value > c.W : value < -c.W;
                    if (!isOut) { allOut = false; break; }
                }
                outside = allOut;
            }
            BoxTestResult = !outside;
        }

        private int Transform(int x, int y, int z, int column) =>
            (int)(((long)x * _clip[column] + (long)y * _clip[4 + column] + (long)z * _clip[8 + column] + 4096L * _clip[12 + column]) >> 12);

        private void PositionTest(int x, int y, int z)
        {
            _vx = x; _vy = y; _vz = z;
            UpdateClip();
            for (int i = 0; i < 4; i++)
                PositionTestResult[i] = Transform(x, y, z, i);
        }

        private void VectorTest(uint value)
        {
            int x = Signed10(value) << 3, y = Signed10(value >> 10) << 3, z = Signed10(value >> 20) << 3;
            for (int i = 0; i < 3; i++)
            {
                int result = (int)(((long)x * _vector[i] + (long)y * _vector[4 + i] + (long)z * _vector[8 + i]) >> 12);
                // 4-bit sign + 12-bit fraction: values ≥ 1.0 wrap.
                VectorTestResult[i] = (short)(((result & 0x1FFF) << 3) >> 3);
            }
        }
    }
}
