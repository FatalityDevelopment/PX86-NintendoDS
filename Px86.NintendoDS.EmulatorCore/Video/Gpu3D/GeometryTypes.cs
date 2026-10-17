namespace Px86.NintendoDS.EmulatorCore.Video.Gpu3D
{
    /// <summary>A vertex after transformation (clip space) and, once the polygon is finished, in screen space.</summary>
    public struct Vertex3D
    {
        // Clip coordinates (20.12 fixed point).
        public int X, Y, Z, W;

        // Colour, 6 bits per channel.
        public int R, G, B;

        // Texture coordinates (12.4 fixed point).
        public int S, T;

        // Screen space (filled in when the polygon is stored).
        public int ScreenX, ScreenY;   // pixels, y = 0 at the top
        public int Depth;              // 24-bit Z or W value used by the depth buffer
    }

    /// <summary>A polygon in Polygon RAM: its vertices plus the attributes latched when it was defined.</summary>
    public sealed class Polygon3D
    {
        public Vertex3D[] Vertices = Array.Empty<Vertex3D>();
        public int VertexCount;

        public uint Attributes;       // POLYGON_ATTR latched at BEGIN_VTXS
        public uint TextureParams;    // TEXIMAGE_PARAM
        public uint PaletteBase;      // PLTT_BASE

        public bool WBuffer;          // SWAP_BUFFERS bit 1 in effect for this polygon
        public bool FrontFacing;
        public bool Translucent;
        public int MinY, MaxY;        // for translucent Y-sorting

        public int Alpha => (int)(Attributes >> 16) & 0x1F;
        public int Mode => (int)(Attributes >> 4) & 3;
        public int PolygonId => (int)(Attributes >> 24) & 0x3F;
    }
}
