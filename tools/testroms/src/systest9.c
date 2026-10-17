/* ARM9 half of the system test: IRQs, VBlankIntrWait, divider, sqrt, timers,
 * DMA, HLE SWIs, IPC with the ARM7 and cartridge reads. Results go to RESULT9(n). */
#include "../nds.h"

static volatile u32 vblanks;
static volatile u32 timer_overflows;

static void irq_handler(void)
{
    u32 flags = IF & IE;
    if (flags & IRQ_VBLANK) vblanks++;
    if (flags & IRQ_TIMER0) timer_overflows++;
    IF = flags;
    IRQ_CHECK9 |= flags;
}

/* "ABCDABCD": 4 literals, then a 4-byte copy from 4 bytes back. */
static const u8 lz_data[] __attribute__((aligned(4))) = { 0x10, 8, 0, 0, 0x08, 'A', 'B', 'C', 'D', 0x10, 0x03, 0, 0, 0, 0, 0 };
static u32 dma_src[16] __attribute__((aligned(4)));
static u32 dma_dst[16] __attribute__((aligned(4)));
static u8 lz_out[8] __attribute__((aligned(4)));
static u32 card_buf[128];

int main(void)
{
    POWCNT1 = 0x820F;
    IRQ_HANDLER9 = irq_handler;
    DISPSTAT = 1 << 3;
    IE = IRQ_VBLANK | IRQ_TIMER0;
    IME = 1;
    __asm__ volatile("mrs r0, cpsr\n bic r0, r0, #0x80\n msr cpsr_c, r0" ::: "r0");

    /* 1: VBlankIntrWait x3 */
    for (int i = 0; i < 3; i++)
        swi_vblank_wait();
    RESULT9(1) = vblanks;

    /* 2-4: hardware divider and square root */
    DIVCNT = 0;
    DIV_NUMER = (u64)(s64)-100;
    DIV_DENOM = 7;
    while (DIVCNT & 0x8000) {}
    RESULT9(2) = (u32)DIV_RESULT;
    RESULT9(3) = (u32)DIVREM_RESULT;
    SQRTCNT = 0;
    SQRT_PARAM = 1000000;
    while (SQRTCNT & 0x8000) {}
    RESULT9(4) = SQRT_RESULT;

    /* 5: timer 0 at F/64 overflowing every 1000 ticks, counted over 2 frames */
    TIMER_DATA(0) = 0x10000 - 1000;
    TIMER_CNT(0) = 0x80 | 0x40 | 1;
    swi_vblank_wait();
    timer_overflows = 0;
    swi_vblank_wait();
    swi_vblank_wait();
    TIMER_CNT(0) = 0;
    RESULT9(5) = timer_overflows;

    /* 6: immediate 32-bit DMA */
    for (int i = 0; i < 16; i++) dma_src[i] = 0x1000 + i;
    DMA_SRC(0) = (u32)dma_src;
    DMA_DST(0) = (u32)dma_dst;
    DMA_CNT(0) = 0x84000000 | 16;
    RESULT9(6) = dma_dst[15];

    /* 7: HLE SWIs: Div, CpuSet fill, LZ77 */
    s32 rem;
    s32 q = swi_div(-50, 3, &rem);
    u32 fill = 0xAABBCCDD;
    u32 fill_dst[4];
    swi_cpuset(&fill, fill_dst, (1 << 24) | (1 << 26) | 4);
    swi_lz77(lz_data, lz_out);
    RESULT9(7) = (q == -16 && rem == -2 ? 1 : 0)
               | (fill_dst[3] == 0xAABBCCDD ? 2 : 0)
               | (lz_out[4] == 'A' && lz_out[7] == 'D' ? 4 : 0);

    /* 8-9: IPC FIFO echo and IPCSYNC from the ARM7 */
    IPCFIFOCNT = 0x8008;
    IPCFIFOSEND = 0x1234;
    while (IPCFIFOCNT & 0x100) {}
    RESULT9(8) = IPCFIFORECV;
    RESULT9(9) = IPCSYNC & 0xF;

    /* 10-11: cartridge B7 read of ROM 8000h (start of the ARM7 binary) */
    AUXSPICNT = 0x8000;
    CARD_CMD[0] = 0xB7;
    CARD_CMD[1] = 0x00; CARD_CMD[2] = 0x00; CARD_CMD[3] = 0x80; CARD_CMD[4] = 0x00;
    CARD_CMD[5] = 0; CARD_CMD[6] = 0; CARD_CMD[7] = 0;
    ROMCTRL = 0xA1586000;
    u32 words = 0;
    while (ROMCTRL & 0x80000000)
        if ((ROMCTRL & 0x00800000) && words < 128)
            card_buf[words++] = CARD_DATA;
    RESULT9(10) = card_buf[0];
    RESULT9(11) = words;

    RESULT9(0) = DONE_MAGIC;
    for (;;)
        swi_vblank_wait();
}
