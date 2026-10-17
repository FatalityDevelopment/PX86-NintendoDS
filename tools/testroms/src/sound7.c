/* Sound test, ARM7 side.
 *  - channel 8: PSG square wave, 50% duty, ~1 kHz, full volume, centre pan
 *  - channel 0: one-shot PCM16 of 256 samples at ~32 kHz; its busy bit must clear by itself */
#include "../nds.h"

#define POWCNT2     REG16(0x04000304)
#define SOUNDCNT    REG16(0x04000500)
#define SOUNDBIAS   REG16(0x04000504)
#define SCHAN_CNT(n) REG32(0x04000400 + (n) * 16)
#define SCHAN_SAD(n) REG32(0x04000404 + (n) * 16)
#define SCHAN_TMR(n) REG16(0x04000408 + (n) * 16)
#define SCHAN_PNT(n) REG16(0x0400040A + (n) * 16)
#define SCHAN_LEN(n) REG32(0x0400040C + (n) * 16)

static short pcm[256] __attribute__((aligned(4)));

int main(void)
{
    POWCNT2 = 1;
    SOUNDCNT = 0x8000 | 127;
    SOUNDBIAS = 0x200;

    /* PSG: sample rate = 8 × 1 kHz; timer = -(16756991 / 8000). */
    SCHAN_TMR(8) = (u16)(0x10000 - 16756991 / 8000);
    SCHAN_CNT(8) = 0x80000000 | (3u << 29) | (3u << 24) | (64u << 16) | 127;

    for (int i = 0; i < 256; i++)
        pcm[i] = (i & 16) ? 0x2000 : -0x2000;
    SCHAN_SAD(0) = (u32)pcm;
    SCHAN_TMR(0) = (u16)(0x10000 - 16756991 / 32768);
    SCHAN_PNT(0) = 0;
    SCHAN_LEN(0) = sizeof(pcm) / 4;
    SCHAN_CNT(0) = 0x80000000 | (1u << 29) | (2u << 27) | (64u << 16) | 127;
    RESULT7(1) = SCHAN_CNT(0) >> 31;           /* busy right after start */

    /* Wait ~20 ms (one-shot is 256 samples ≈ 7.8 ms) by polling VCOUNT. */
    for (int lines = 0; lines < 330; ) {
        u16 v = VCOUNT;
        while (VCOUNT == v) {}
        lines++;
    }
    RESULT7(2) = SCHAN_CNT(0) >> 31;           /* busy cleared after the one-shot ended */
    RESULT7(3) = SCHAN_CNT(8) >> 31;           /* PSG keeps playing */
    RESULT7(0) = DONE_MAGIC;
    for (;;) {}
}
