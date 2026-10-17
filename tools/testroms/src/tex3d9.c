/* 3D texturing + alpha blending test.
 * Full-screen quad with a repeating 8x8 direct-colour checker (red/blue texels),
 * texture coordinates 0..64 texels across the screen (each texel ≈ 4x3 pixels).
 * Then a white quad with alpha 15 over the left half, alpha-blended (DISP3DCNT bit 3). */
#include "../nds.h"

#define GX(port) REG32(0x04000400 + (port))
#define DISP3DCNT     REG16(0x04000060)
#define CLEAR_COLOR   REG32(0x04000350)
#define CLEAR_DEPTH   REG16(0x04000354)
#define MTX_MODE      GX(0x040)
#define MTX_IDENTITY  GX(0x054)
#define COLOR         GX(0x080)
#define TEXCOORD      GX(0x088)
#define VTX_16        GX(0x08C)
#define POLYGON_ATTR  GX(0x0A4)
#define TEXIMAGE      GX(0x0A8)
#define BEGIN_VTXS    GX(0x100)
#define END_VTXS      GX(0x104)
#define SWAP_BUFFERS  GX(0x140)
#define VIEWPORT      GX(0x180)
#define TEX_VRAM      ((volatile u16 *)0x06800000)   /* bank A in LCDC mode while uploading */

#define F(x) ((u32)(u16)(short)((x) * 4096))
#define T(s, t) ((u32)(u16)((s) << 4) | ((u32)(u16)((t) << 4) << 16))

static void vertex(float x, float y, float z)
{
    VTX_16 = F(x) | (F(y) << 16);
    VTX_16 = F(z);
}

int main(void)
{
    POWCNT1 = 0x820F;
    irq_init_vblank9();

    /* Upload the texture through LCDC, then map bank A as texture slot 0. */
    VRAMCNT_A = 0x80;
    for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x++)
            TEX_VRAM[y * 8 + x] = ((x + y) & 1) ? (0x8000 | 0x7C00) : (0x8000 | 0x001F);
    VRAMCNT_A = 0x83;

    DISPCNT = 0x00010000 | 0x0100 | 0x0008;
    DISP3DCNT = 0x0001 | 0x0008;                 /* texturing + alpha blending */
    CLEAR_COLOR = 31 << 16;
    CLEAR_DEPTH = 0x7FFF;
    VIEWPORT = (255 << 16) | (191 << 24);
    MTX_MODE = 0; MTX_IDENTITY = 0;
    MTX_MODE = 2; MTX_IDENTITY = 0;

    /* Textured quad: direct colour, 8x8, repeat S and T. */
    POLYGON_ATTR = (31 << 16) | 0xC0;
    TEXIMAGE = (7u << 26) | (1 << 16) | (1 << 17);
    BEGIN_VTXS = 1;
    COLOR = 0x7FFF;
    TEXCOORD = T(0, 64);  vertex(-1.0f, -1.0f, 0.5f);
    TEXCOORD = T(64, 64); vertex(0.999f, -1.0f, 0.5f);
    TEXCOORD = T(64, 0);  vertex(0.999f, 0.999f, 0.5f);
    TEXCOORD = T(0, 0);   vertex(-1.0f, 0.999f, 0.5f);
    END_VTXS = 0;

    /* Translucent white over the left half (closer). */
    POLYGON_ATTR = (15 << 16) | 0xC0 | (1 << 24);
    TEXIMAGE = 0;
    BEGIN_VTXS = 1;
    COLOR = 0x7FFF;
    vertex(-1.0f, -1.0f, 0.0f);
    vertex(0.0f, -1.0f, 0.0f);
    vertex(0.0f, 0.999f, 0.0f);
    vertex(-1.0f, 0.999f, 0.0f);
    END_VTXS = 0;

    SWAP_BUFFERS = 0;
    swi_vblank_wait();
    swi_vblank_wait();
    RESULT9(0) = DONE_MAGIC;
    for (;;)
        swi_vblank_wait();
}
