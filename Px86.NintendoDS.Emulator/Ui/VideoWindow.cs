using System.Numerics;
using ImGuiNET;
using Px86.NintendoDS.EmulatorCore.Debugging;
using Px86.NintendoDS.EmulatorCore.Hosting;
using Silk.NET.OpenGL;

namespace Px86.NintendoDS.Emulator.Ui
{
    /// <summary>
    /// Tools → Video viewers: palettes, tiles, sprites (OAM) and the current
    /// 3D polygon list, read live from the ARM9's view of palette RAM, VRAM
    /// and OAM.
    /// </summary>
    public sealed unsafe class VideoWindow : IDisposable
    {
        private static readonly string[] Sources = { "Engine A BG", "Engine A OBJ", "Engine B BG", "Engine B OBJ" };
        private static readonly uint[] VramBase = { 0x06000000, 0x06400000, 0x06200000, 0x06600000 };
        private static readonly uint[] PaletteBase = { 0x05000000, 0x05000200, 0x05000400, 0x05000600 };
        private static readonly uint[] OamBase = { 0x07000000, 0x07000400 };
        private static readonly string[,] SpriteSizes =
        {
            { "8x8", "16x16", "32x32", "64x64" }, { "16x8", "32x8", "32x16", "64x32" }, { "8x16", "8x32", "16x32", "32x64" }, { "?", "?", "?", "?" },
        };

        private readonly UiContext _ui;
        private readonly GL _gl;
        private uint _texture;
        private readonly uint[] _tilePixels = new uint[256 * 256];
        private bool _open;
        private int _source;
        private bool _eightBpp;
        private int _palette;
        private int _page;
        private int _oamEngine;
        private bool _showHidden;
        private int _polygon = -1;

        public VideoWindow(UiContext ui, GL gl)
        {
            _ui = ui;
            _gl = gl;
        }

        public void Open() => _open = true;

        public void Draw()
        {
            if (!_open)
                return;
            var viewport = ImGui.GetMainViewport();
            ImGui.SetNextWindowSize(new Vector2(Math.Min(600, viewport.Size.X - 20), Math.Min(560, viewport.Size.Y - 40)), ImGuiCond.FirstUseEver);
            ImGui.SetNextWindowPos(viewport.GetCenter(), ImGuiCond.FirstUseEver, new Vector2(0.5f, 0.5f));
            if (ImGui.Begin("Video viewers", ref _open, ImGuiWindowFlags.NoCollapse))
            {
                if (_ui.Host.Status == HostStatus.Shutdown)
                {
                    ImGui.TextDisabled("Start a game first.");
                }
                else if (ImGui.BeginTabBar("##video"))
                {
                    if (ImGui.BeginTabItem("Palettes")) { Palettes(); ImGui.EndTabItem(); }
                    if (ImGui.BeginTabItem("Tiles")) { Tiles(); ImGui.EndTabItem(); }
                    if (ImGui.BeginTabItem("Sprites (OAM)")) { Sprites(); ImGui.EndTabItem(); }
                    if (ImGui.BeginTabItem("3D polygons")) { Polygons(); ImGui.EndTabItem(); }
                    ImGui.EndTabBar();
                }
            }
            ImGui.End();
        }

        private static uint ToAbgr(ushort bgr555)
        {
            uint r = (uint)(bgr555 & 0x1F) * 255 / 31, g = (uint)((bgr555 >> 5) & 0x1F) * 255 / 31, b = (uint)((bgr555 >> 10) & 0x1F) * 255 / 31;
            return 0xFF000000 | (b << 16) | (g << 8) | r; // ImGui colours are ABGR
        }

        private ushort[] ReadPalette(int source) => _ui.Host.Invoke(() =>
        {
            var nds = _ui.Host.Emulator!;
            var colors = new ushort[256];
            for (int i = 0; i < 256; i++)
                colors[i] = DebugMemory.Read16(nds, DebugCpu.Arm9, PaletteBase[source] + (uint)i * 2);
            return colors;
        });

        // ── Palettes ──────────────────────────────────────────────────────────

        private void Palettes()
        {
            var draw = ImGui.GetWindowDrawList();
            for (int source = 0; source < 4; source++)
            {
                ImGui.TextUnformatted(Sources[source]);
                var colors = ReadPalette(source);
                var origin = ImGui.GetCursorScreenPos();
                const float cell = 12;
                for (int i = 0; i < 256; i++)
                {
                    var p = origin + new Vector2(i % 32 * cell, i / 32 * cell);
                    draw.AddRectFilled(p, p + new Vector2(cell - 1, cell - 1), ToAbgr(colors[i]));
                    if (ImGui.IsMouseHoveringRect(p, p + new Vector2(cell, cell)))
                        ImGui.SetTooltip($"{Sources[source]} #{i}: {colors[i]:X4}");
                }
                ImGui.Dummy(new Vector2(32 * cell, 8 * cell + 4));
            }
        }

        // ── Tiles ─────────────────────────────────────────────────────────────

        private void Tiles()
        {
            ImGui.SetNextItemWidth(140);
            ImGui.Combo("Source", ref _source, Sources, Sources.Length);
            ImGui.SameLine();
            ImGui.Checkbox("8 bpp", ref _eightBpp);
            if (!_eightBpp)
            {
                ImGui.SameLine();
                ImGui.SetNextItemWidth(120);
                ImGui.SliderInt("Palette", ref _palette, 0, 15);
            }
            int pageSize = _eightBpp ? 0x10000 : 0x8000; // 32×32 tiles
            int pages = (_source % 2 == 0 ? 0x80000 : 0x40000) / pageSize;
            _page = Math.Clamp(_page, 0, pages - 1);
            ImGui.SetNextItemWidth(200);
            ImGui.SliderInt("Page", ref _page, 0, pages - 1, $"%d (offset {_page * pageSize:X5})");

            var colors = ReadPalette(_source);
            uint vram = VramBase[_source] + (uint)(_page * pageSize);
            int bytes = pageSize;
            var data = _ui.Host.Invoke(() =>
            {
                var buffer = new byte[bytes];
                DebugMemory.ReadBlock(_ui.Host.Emulator!, DebugCpu.Arm9, vram, buffer);
                return buffer;
            });

            int tileBytes = _eightBpp ? 64 : 32;
            for (int tile = 0; tile < 1024; tile++)
            {
                int tx = tile % 32 * 8, ty = tile / 32 * 8;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        int index;
                        if (_eightBpp)
                        {
                            index = data[tile * tileBytes + y * 8 + x];
                        }
                        else
                        {
                            byte pair = data[tile * tileBytes + y * 4 + x / 2];
                            int nibble = (x & 1) == 0 ? pair & 0xF : pair >> 4;
                            index = nibble == 0 ? 0 : _palette * 16 + nibble;
                        }
                        uint c = ToAbgr(colors[index]);
                        _tilePixels[(ty + y) * 256 + tx + x] = index == 0 ? 0xFF202020 : c;
                    }
                }
            }
            Upload();
            ImGui.Image((IntPtr)_texture, new Vector2(512, 512));
            if (ImGui.IsItemHovered())
            {
                var local = (ImGui.GetMousePos() - ImGui.GetItemRectMin()) / 2;
                int tile = (int)(local.Y / 8) * 32 + (int)(local.X / 8);
                if (tile is >= 0 and < 1024)
                    ImGui.SetTooltip($"tile {_page * 1024 + tile} at {vram + (uint)(tile * tileBytes):X8}");
            }
        }

        private void Upload()
        {
            if (_texture == 0)
            {
                _texture = _gl.GenTexture();
                _gl.BindTexture(TextureTarget.Texture2D, _texture);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
                _gl.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
                _gl.TexImage2D(TextureTarget.Texture2D, 0, InternalFormat.Rgba8, 256, 256, 0, PixelFormat.Rgba, PixelType.UnsignedByte, null);
            }
            _gl.BindTexture(TextureTarget.Texture2D, _texture);
            fixed (uint* p = _tilePixels)
                _gl.TexSubImage2D(TextureTarget.Texture2D, 0, 0, 0, 256, 256, PixelFormat.Rgba, PixelType.UnsignedByte, p);
        }

        // ── Sprites ───────────────────────────────────────────────────────────

        private void Sprites()
        {
            ImGui.SetNextItemWidth(120);
            ImGui.Combo("Engine", ref _oamEngine, new[] { "A", "B" }, 2);
            ImGui.SameLine();
            ImGui.Checkbox("Show hidden", ref _showHidden);
            var oam = _ui.Host.Invoke(() =>
            {
                var buffer = new byte[1024];
                DebugMemory.ReadBlock(_ui.Host.Emulator!, DebugCpu.Arm9, OamBase[_oamEngine], buffer);
                return buffer;
            });
            if (!ImGui.BeginTable("##oam", 8, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerV))
                return;
            foreach (var h in new[] { "#", "X", "Y", "Size", "Tile", "Pal", "Prio", "Mode" })
                ImGui.TableSetupColumn(h);
            ImGui.TableHeadersRow();
            for (int i = 0; i < 128; i++)
            {
                ushort a0 = (ushort)(oam[i * 8] | oam[i * 8 + 1] << 8);
                ushort a1 = (ushort)(oam[i * 8 + 2] | oam[i * 8 + 3] << 8);
                ushort a2 = (ushort)(oam[i * 8 + 4] | oam[i * 8 + 5] << 8);
                bool affine = (a0 & 0x100) != 0;
                bool hidden = !affine && (a0 & 0x200) != 0;
                if (hidden && !_showHidden)
                    continue;
                int x = a1 & 0x1FF;
                if (x >= 256) x -= 512;
                ImGui.TableNextRow();
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{i}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{x}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{a0 & 0xFF}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted(SpriteSizes[a0 >> 14, a1 >> 14]);
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{a2 & 0x3FF}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted((a0 & 0x2000) != 0 ? "256" : $"{a2 >> 12}");
                ImGui.TableNextColumn(); ImGui.TextUnformatted($"{(a2 >> 10) & 3}");
                ImGui.TableNextColumn();
                ImGui.TextUnformatted((hidden ? "hidden " : "") + (affine ? $"affine {(a1 >> 9) & 31}" : "") +
                                      ((a0 >> 10 & 3) switch { 1 => " semi-transparent", 2 => " window", 3 => " bitmap", _ => "" }));
            }
            ImGui.EndTable();
        }

        // ── 3D ────────────────────────────────────────────────────────────────

        private void Polygons()
        {
            var polygons = _ui.Host.Invoke(() => _ui.Host.Emulator!.Gpu3D.Geometry.RenderList.Select(p =>
                (p.Attributes, p.TextureParams, p.VertexCount, p.MinY, p.MaxY, p.Translucent, p.Mode, p.PolygonId, p.Alpha,
                 Vertices: p.Vertices.Take(p.VertexCount).Select(v => $"clip {v.X} {v.Y} {v.Z} {v.W}  screen {v.ScreenX},{v.ScreenY}  rgb {v.R},{v.G},{v.B}  st {v.S},{v.T}").ToArray())).ToList());
            ImGui.TextUnformatted($"{polygons.Count} polygons in the last frame. F9 records the frame for a bug report.");
            if (ImGui.BeginChild("##polys", new Vector2(0, 260), true))
            {
                for (int i = 0; i < polygons.Count; i++)
                {
                    var p = polygons[i];
                    string label = $"#{i,-4} {p.VertexCount}v y {p.MinY}-{p.MaxY}  id {p.PolygonId} a {p.Alpha} mode {p.Mode} {(p.Translucent ? "transl." : "opaque")}  tex {p.TextureParams:X8}";
                    if (ImGui.Selectable(label, _polygon == i))
                        _polygon = i;
                }
            }
            ImGui.EndChild();
            if (_polygon >= 0 && _polygon < polygons.Count)
            {
                var p = polygons[_polygon];
                ImGui.TextUnformatted($"Polygon {_polygon}: attr {p.Attributes:X8}");
                foreach (var v in p.Vertices)
                    ImGui.TextUnformatted("  " + v);
            }
        }

        public void Dispose()
        {
            if (_texture != 0)
                _gl.DeleteTexture(_texture);
            _texture = 0;
        }
    }
}
