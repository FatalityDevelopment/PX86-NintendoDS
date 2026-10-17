namespace Px86.NintendoDS.EmulatorCore.Video
{
    public sealed partial class Gpu2DEngine
    {
        /// <summary>Display mode 1: BG and OBJ layers. (Backdrop only until the layer renderer lands.)</summary>
        private void RenderNormal(int line, Span<ushort> rgb)
        {
            rgb.Fill(PaletteColor(0));
        }
    }
}
