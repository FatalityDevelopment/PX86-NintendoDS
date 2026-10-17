/* Register definitions for Px86 test ROMs (freestanding, no libnds). */
#pragma once
typedef unsigned char u8;
typedef unsigned short u16;
typedef unsigned int u32;
typedef unsigned long long u64;
typedef signed int s32;
typedef signed long long s64;

#define REG8(a)  (*(volatile u8  *)(a))
#define REG16(a) (*(volatile u16 *)(a))
#define REG32(a) (*(volatile u32 *)(a))
#define REG64(a) (*(volatile u64 *)(a))

#define DISPCNT        REG32(0x04000000)
#define DISPSTAT       REG16(0x04000004)
#define VCOUNT         REG16(0x04000006)
#define DMA_SRC(n)     REG32(0x040000B0 + (n) * 12)
#define DMA_DST(n)     REG32(0x040000B4 + (n) * 12)
#define DMA_CNT(n)     REG32(0x040000B8 + (n) * 12)
#define TIMER_DATA(n)  REG16(0x04000100 + (n) * 4)
#define TIMER_CNT(n)   REG16(0x04000102 + (n) * 4)
#define KEYINPUT       REG16(0x04000130)
#define IPCSYNC        REG16(0x04000180)
#define IPCFIFOCNT     REG16(0x04000184)
#define IPCFIFOSEND    REG32(0x04000188)
#define IPCFIFORECV    REG32(0x04100000)
#define AUXSPICNT      REG16(0x040001A0)
#define ROMCTRL        REG32(0x040001A4)
#define CARD_CMD       ((volatile u8 *)0x040001A8)
#define CARD_DATA      REG32(0x04100010)
#define IME            REG32(0x04000208)
#define IE             REG32(0x04000210)
#define IF             REG32(0x04000214)
#define VRAMCNT_A      REG8(0x04000240)
#define DIVCNT         REG16(0x04000280)
#define DIV_NUMER      REG64(0x04000290)
#define DIV_DENOM      REG64(0x04000298)
#define DIV_RESULT     REG64(0x040002A0)
#define DIVREM_RESULT  REG64(0x040002A8)
#define SQRTCNT        REG16(0x040002B0)
#define SQRT_RESULT    REG32(0x040002B4)
#define SQRT_PARAM     REG64(0x040002B8)
#define POWCNT1        REG16(0x04000304)

#define IRQ_VBLANK  (1 << 0)
#define IRQ_TIMER0  (1 << 3)
#define IRQ_DMA0    (1 << 8)
#define IRQ_IPCSYNC (1 << 16)

/* Results mailbox shared with the unit tests (main RAM, uncached in tests). */
#define RESULT9(i)  REG32(0x02300000 + (i) * 4)
#define RESULT7(i)  REG32(0x02300100 + (i) * 4)
#define DONE_MAGIC  0x600DF00D

/* BIOS IRQ handler pointer and check bits. */
#define IRQ_HANDLER9  (*(void (* volatile *)(void))0x03003FFC)
#define IRQ_CHECK9    REG32(0x03003FF8)
#define IRQ_HANDLER7  (*(void (* volatile *)(void))0x0380FFFC)
#define IRQ_CHECK7    REG32(0x0380FFF8)

static inline void swi_vblank_wait(void) { __asm__ volatile("swi 0x050000" ::: "r0", "r1", "r2", "r3", "r12", "lr", "memory"); }
static inline void swi_cpuset(const void *src, void *dst, u32 control)
{
    register const void *r0 __asm__("r0") = src;
    register void *r1 __asm__("r1") = dst;
    register u32 r2 __asm__("r2") = control;
    __asm__ volatile("swi 0x0B0000" : "+r"(r0), "+r"(r1), "+r"(r2) :: "r3", "r12", "lr", "memory");
}
static inline s32 swi_div(s32 num, s32 den, s32 *rem)
{
    register s32 r0 __asm__("r0") = num;
    register s32 r1 __asm__("r1") = den;
    __asm__ volatile("swi 0x090000" : "+r"(r0), "+r"(r1) :: "r2", "r3", "r12", "lr", "memory");
    *rem = r1;
    return r0;
}
static inline void swi_lz77(const void *src, void *dst)
{
    register const void *r0 __asm__("r0") = src;
    register void *r1 __asm__("r1") = dst;
    __asm__ volatile("swi 0x110000" : "+r"(r0), "+r"(r1) :: "r2", "r3", "r12", "lr", "memory");
}

/* Minimal ARM9 IRQ setup: VBlank IRQ that acknowledges itself and sets the
 * BIOS check bits, so swi_vblank_wait() works. */
static void nds_default_irq9(void)
{
    u32 flags = IF & IE;
    IF = flags;
    IRQ_CHECK9 |= flags;
}

static inline void irq_init_vblank9(void)
{
    IRQ_HANDLER9 = nds_default_irq9;
    DISPSTAT = 1 << 3;
    IE = IRQ_VBLANK;
    IME = 1;
    __asm__ volatile("mrs r0, cpsr\n bic r0, r0, #0x80\n msr cpsr_c, r0" ::: "r0");
}
