/* 2D graphics test.
 * Engine A: mode 0, BG0 = 4bpp text checkerboard (red/green 8x8 tiles), one 16x16 blue sprite at (100,50).
 * Engine B: mode 3, BG3 = 256x256 direct-colour bitmap, red ramp = x / 8. */
#include "../nds.h"

#define DISPCNT_B   REG32(0x04001000)
#define BG0CNT      REG16(0x04000008)
#define BG3CNT_B    REG16(0x0400100E)
#define BG3PA_B     REG16(0x04001030)
#define BG3PD_B     REG16(0x04001036)
#define VRAMCNT_B   REG8(0x04000241)
#define VRAMCNT_C   REG8(0x04000242)
#define BG_PAL      ((volatile u16 *)0x05000000)
#define OBJ_PAL     ((volatile u16 *)0x05000200)
#define BG_VRAM     ((volatile u16 *)0x06000000)
#define BGB_VRAM    ((volatile u16 *)0x06200000)
#define OBJ_VRAM    ((volatile u16 *)0x06400000)
#define OAM         ((volatile u16 *)0x07000000)

int main(void)
{
    POWCNT1 = 0x820F;
    VRAMCNT_A = 0x81;  /* A → engine A BG */
    VRAMCNT_B = 0x82;  /* B → engine A OBJ */
    VRAMCNT_C = 0x84;  /* C → engine B BG */

    BG_PAL[0] = 0x0000;
    BG_PAL[1] = 0x001F;
    BG_PAL[2] = 0x03E0;
    OBJ_PAL[1] = 0x7C00;

    /* Tiles at char base 1 (06004000h): tile 1 all colour 1, tile 2 all colour 2. */
    for (int i = 0; i < 16; i++) {
        BG_VRAM[0x2000 + 16 + i] = 0x1111;
        BG_VRAM[0x2000 + 32 + i] = 0x2222;
    }
    for (int y = 0; y < 32; y++)
        for (int x = 0; x < 32; x++)
            BG_VRAM[y * 32 + x] = ((x + y) & 1) ? 1 : 2;

    for (int i = 0; i < 64; i++)
        OBJ_VRAM[i] = 0x1111;
    for (int i = 0; i < 128; i++)
        OAM[i * 4] = 0x0200;   /* disable all */
    OAM[0] = 50;               /* y, square */
    OAM[1] = 0x4000 | 100;     /* x, size 1 → 16x16 */
    OAM[2] = 0;                /* tile 0, priority 0 */

    BG0CNT = 1 << 2;
    DISPCNT = 0x00010000 | 0x0100 | 0x1000 | 0x10;

    for (int y = 0; y < 256; y++)
        for (int x = 0; x < 256; x++)
            BGB_VRAM[y * 256 + x] = 0x8000 | (x >> 3);
    BG3PA_B = 0x100;
    BG3PD_B = 0x100;
    BG3CNT_B = 0x4084;
    DISPCNT_B = 0x00010000 | 0x0800 | 3;

    RESULT9(0) = DONE_MAGIC;
    for (;;) {}
}
