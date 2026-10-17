using System.Text;
using Px86.NintendoDS.Common.Core;

namespace Px86.NintendoDS.EmulatorCore.Video.Gpu3D
{
    /// <summary>
    /// GX frame capture: everything needed to re-render one 3D frame without
    /// the game.
    ///
    ///   header        magic "PX86GX01", game code
    ///   start state   geometry engine + 3D registers (incl. the half-built polygon list and FIFO)
    ///   commands      every GX command word queued until the next swap happens
    ///   end state     VRAM and the 3D rendering registers at that swap
    ///
    /// <see cref="Replay"/> loads it into a blank console, feeds the commands
    /// back and renders, so 3D bugs from a game can be reproduced in a test
    /// or a debugger. A capture holds one frame's geometry and the textures in
    /// VRAM, not the ROM.
    /// </summary>
    public static class GxCapture
    {
        public const string Magic = "PX86GX01";
        public const string FileExtension = ".gxcap";

        /// <summary>The result of a replay.</summary>
        public sealed record Result(uint[] Pixels, IReadOnlyList<Polygon3D> Polygons, IReadOnlyList<(byte Command, uint Parameter)> Commands, string GameCode)
        {
            /// <summary>Human-readable dump: commands with names and every polygon with its vertices.</summary>
            public string Describe()
            {
                var sb = new StringBuilder();
                sb.AppendLine($"GX capture of {GameCode}: {Commands.Count} command words, {Polygons.Count} polygons");
                sb.AppendLine();
                sb.AppendLine("Polygons (attributes, texture params, then vertices as clip x y z w → screen x y depth, colour, s t):");
                for (int i = 0; i < Polygons.Count; i++)
                {
                    var p = Polygons[i];
                    sb.AppendLine($"#{i} attr={p.Attributes:X8} tex={p.TextureParams:X8} pal={p.PaletteBase:X4} mode={p.Mode} id={p.PolygonId} " +
                                  $"alpha={p.Alpha} {(p.Translucent ? "translucent" : "opaque")} {(p.FrontFacing ? "front" : "back")} y={p.MinY}..{p.MaxY}");
                    for (int v = 0; v < p.VertexCount; v++)
                    {
                        var x = p.Vertices[v];
                        sb.AppendLine($"    {x.X,9} {x.Y,9} {x.Z,9} {x.W,9} → {x.ScreenX,4} {x.ScreenY,4} {x.Depth,8:X6}  rgb {x.R,2} {x.G,2} {x.B,2}  st {x.S} {x.T}");
                    }
                }
                sb.AppendLine();
                sb.AppendLine("Commands:");
                foreach (var (command, parameter) in Commands)
                    sb.AppendLine($"  {command:X2} {CommandName(command),-16} {parameter:X8}");
                return sb.ToString();
            }
        }

        // ── Replay ────────────────────────────────────────────────────────────

        public static Result Replay(byte[] capture)
        {
            using var stream = new MemoryStream(capture, writable: false);
            using var reader = new BinaryReader(stream);
            if (Encoding.ASCII.GetString(reader.ReadBytes(Magic.Length)) != Magic)
                throw new InvalidDataException("Not a Px86 GX capture.");
            string gameCode = reader.ReadString();

            var nds = new NdsEmulator();
            nds.Reset();
            var s = StateSerializer.ForReading(stream);
            nds.Gpu3D.Serialize(s);

            int count = reader.ReadInt32();
            var commands = new List<(byte, uint)>(count);
            for (int i = 0; i < count; i++)
                commands.Add((reader.ReadByte(), reader.ReadUInt32()));

            nds.Vram.Serialize(s);
            nds.Gpu3D.SerializeRenderSettings(s);

            foreach (var (command, parameter) in commands)
                nds.Gpu3D.Geometry.Replay(command, parameter);
            nds.Gpu3D.OnVBlank(renderEnabled: true);

            var pixels = new uint[256 * 192];
            for (int y = 0; y < 192; y++)
            {
                var line = nds.Gpu3D.GetLine(y)!;
                for (int x = 0; x < 256; x++)
                {
                    ushort c = line[x];
                    pixels[y * 256 + x] = (c & 0x8000) == 0 ? 0xFF000000 : Gpu2DEngine.ToArgb(Expand(c));
                }
            }
            return new Result(pixels, nds.Gpu3D.Geometry.RenderList.ToList(), commands, gameCode);
        }

        /// <summary>15-bit BGR555 → the 6-bit-per-channel framebuffer format.</summary>
        private static uint Expand(ushort c)
        {
            uint r = (uint)(c & 0x1F) * 2, g = (uint)((c >> 5) & 0x1F) * 2, b = (uint)((c >> 10) & 0x1F) * 2;
            return r | (g << 8) | (b << 16);
        }

        public static string CommandName(byte command) => command switch
        {
            0x10 => "MTX_MODE", 0x11 => "MTX_PUSH", 0x12 => "MTX_POP", 0x13 => "MTX_STORE", 0x14 => "MTX_RESTORE",
            0x15 => "MTX_IDENTITY", 0x16 => "MTX_LOAD_4x4", 0x17 => "MTX_LOAD_4x3", 0x18 => "MTX_MULT_4x4",
            0x19 => "MTX_MULT_4x3", 0x1A => "MTX_MULT_3x3", 0x1B => "MTX_SCALE", 0x1C => "MTX_TRANS",
            0x20 => "COLOR", 0x21 => "NORMAL", 0x22 => "TEXCOORD", 0x23 => "VTX_16", 0x24 => "VTX_10",
            0x25 => "VTX_XY", 0x26 => "VTX_XZ", 0x27 => "VTX_YZ", 0x28 => "VTX_DIFF", 0x29 => "POLYGON_ATTR",
            0x2A => "TEXIMAGE_PARAM", 0x2B => "PLTT_BASE", 0x30 => "DIF_AMB", 0x31 => "SPE_EMI", 0x32 => "LIGHT_VECTOR",
            0x33 => "LIGHT_COLOR", 0x34 => "SHININESS", 0x40 => "BEGIN_VTXS", 0x41 => "END_VTXS", 0x50 => "SWAP_BUFFERS",
            0x60 => "VIEWPORT", 0x70 => "BOX_TEST", 0x71 => "POS_TEST", 0x72 => "VEC_TEST",
            _ => "?",
        };
    }

    /// <summary>
    /// Records a <see cref="GxCapture"/> from a running console: <see cref="Start"/>
    /// takes the start snapshot (call it between slices, e.g. inside
    /// <c>EmulatorHost.Invoke</c>), and <see cref="Completed"/> delivers the
    /// file at the next buffer swap, on the emulation thread.
    /// </summary>
    public sealed class GxCaptureRecorder
    {
        private readonly NdsEmulator _nds;
        private MemoryStream? _start;
        private List<(byte, uint)>? _commands;

        public GxCaptureRecorder(NdsEmulator nds)
        {
            _nds = nds;
            nds.Gpu3D.Geometry.BuffersSwapped += OnSwapped;
            nds.Gpu3D.Geometry.CommandQueued += OnCommand;
        }

        public bool IsBusy => _commands != null;

        /// <summary>The capture file. Raised on the emulation thread.</summary>
        public event Action<byte[]>? Completed;

        public void Start()
        {
            _start = new MemoryStream();
            _nds.Gpu3D.Serialize(StateSerializer.ForWriting(_start));
            _commands = new List<(byte, uint)>();
        }

        public void Detach()
        {
            _nds.Gpu3D.Geometry.BuffersSwapped -= OnSwapped;
            _nds.Gpu3D.Geometry.CommandQueued -= OnCommand;
        }

        private void OnCommand(byte command, uint parameter) => _commands?.Add((command, parameter));

        private void OnSwapped()
        {
            if (_commands != null)
                Finish();
        }

        private void Finish()
        {
            using var file = new MemoryStream();
            using (var writer = new BinaryWriter(file, Encoding.UTF8, leaveOpen: true))
            {
                writer.Write(Encoding.ASCII.GetBytes(GxCapture.Magic));
                writer.Write(_nds.Rom?.Header.GameCode ?? "");
                writer.Write(_start!.ToArray());
                writer.Write(_commands!.Count);
                foreach (var (command, parameter) in _commands)
                {
                    writer.Write(command);
                    writer.Write(parameter);
                }
            }
            var s = StateSerializer.ForWriting(file);
            _nds.Vram.Serialize(s);
            _nds.Gpu3D.SerializeRenderSettings(s);

            _start = null;
            _commands = null;
            Completed?.Invoke(file.ToArray());
        }
    }
}
