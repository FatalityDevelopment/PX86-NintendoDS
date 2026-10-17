/* 3D test: a red/green/blue triangle at z = 0 in front of a white quad at z = 0.5 that is drawn
 * afterwards (depth test must keep the triangle), identity projection, full-screen viewport.
 * The quad covers the right half of the screen only, so the left corners show the clear colour. */
#include "../nds.h"

#define GX(port) REG32(0x04000400 + (port))
#define DISP3DCNT     REG16(0x04000060)
#define CLEAR_COLOR   REG32(0x04000350)
#define CLEAR_DEPTH   REG16(0x04000354)
#define GXSTAT        REG32(0x04000600)

#define MTX_MODE      GX(0x040)
#define MTX_IDENTITY  GX(0x054)
#define COLOR         GX(0x080)
#define VTX_16        GX(0x08C)
#define POLYGON_ATTR  GX(0x0A4)
#define BEGIN_VTXS    GX(0x100)
#define END_VTXS      GX(0x104)
#define SWAP_BUFFERS  GX(0x140)
#define VIEWPORT      GX(0x180)

#define F(x) ((u32)(u16)(short)((x) * 4096))

static void vertex(float x, float y, float z)
{
    VTX_16 = F(x) | (F(y) << 16);
    VTX_16 = F(z);
}

int main(void)
{
    POWCNT1 = 0x820F;
    irq_init_vblank9();
    DISPCNT = 0x00010000 | 0x0100 | 0x0008;   /* mode 1 display, BG0 on, BG0 = 3D */
    DISP3DCNT = 0;
    CLEAR_COLOR = (31 << 16) | 0x0000;           /* opaque black rear plane */
    CLEAR_DEPTH = 0x7FFF;

    VIEWPORT = 0 | (0 << 8) | (255 << 16) | (191 << 24);
    MTX_MODE = 0; MTX_IDENTITY = 0;
    MTX_MODE = 2; MTX_IDENTITY = 0;

    POLYGON_ATTR = (31 << 16) | 0xC0;            /* solid, front + back */
    BEGIN_VTXS = 0;                              /* triangles */
    COLOR = 0x001F; vertex(-0.5f, -0.5f, 0.0f);
    COLOR = 0x03E0; vertex( 0.5f, -0.5f, 0.0f);
    COLOR = 0x7C00; vertex( 0.0f,  0.5f, 0.0f);
    END_VTXS = 0;

    BEGIN_VTXS = 1;                              /* quads */
    COLOR = 0x7FFF;
    vertex(0.0f, -1.0f, 0.5f);
    vertex(0.99f, -1.0f, 0.5f);
    vertex(0.99f, 0.99f, 0.5f);
    vertex(0.0f, 0.99f, 0.5f);
    END_VTXS = 0;

    RESULT9(2) = REG32(0x04000604);               /* RAM_COUNT: polygons in the list being built */
    SWAP_BUFFERS = 0;
    RESULT9(1) = GXSTAT;                          /* busy until the swap happens at VBlank */

    swi_vblank_wait();
    swi_vblank_wait();
    RESULT9(3) = REG32(0x04000604);               /* RAM_COUNT after the swap: new empty list */

    /* Capture the 3D layer (source A = 3D, 256x192) into VRAM bank B. */
    REG8(0x04000241) = 0x80;                      /* VRAMCNT_B = LCDC */
    REG32(0x04000064) = 0x80000000 | (1 << 24) | (3 << 20) | (1 << 16);
    swi_vblank_wait();
    swi_vblank_wait();
    RESULT9(4) = REG32(0x04000064);               /* bit 31 clears when the capture is done */
    RESULT9(0) = DONE_MAGIC;
    for (;;)
        swi_vblank_wait();
}
