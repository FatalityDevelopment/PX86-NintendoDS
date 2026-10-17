/* Debug output test, ARM9 side: every emulator debug channel once.
 * Expected log lines (ARM9), in order:
 *   "raw string", "r0=1234ABCD", "chars OK", "W", "ensata", "arm msg r1=00000007", "thumb pc=<address>"
 *   RESULT9(1) first 4 ID bytes ("no$g")   RESULT9(2) 1 = clock cycle counter advanced */
#include "../nds.h"

#define DBG_ID     REG32(0x04FFFA00)
#define DBG_STR    REG32(0x04FFFA10)
#define DBG_STRP   REG32(0x04FFFA14)
#define DBG_STRL   REG32(0x04FFFA18)
#define DBG_CHAR   REG8(0x04FFFA1C)
#define DBG_CHAR32 REG32(0x04FFFA1C)
#define DBG_CLK    REG32(0x04FFFA20)
#define ENSATA     REG8(0x04FFF000)

static void put_chars(const char *s)
{
    while (*s)
        DBG_CHAR = (u8)*s++;
}

/* A no$gba in-code message in Thumb state: mov r12,r12; b over 6464h, flags, text. */
__attribute__((target("thumb"), noinline)) static void thumb_message(void)
{
    __asm__ volatile(
        "movs r1, #9\n"
        "mov r12, r12\n"
        "b 1f\n"
        ".hword 0x6464\n"
        ".hword 0\n"
        ".ascii \"thumb pc=%%pc%%\"\n"
        ".byte 0\n"
        ".balign 2\n"
        "1:\n" ::: "r1", "r12");
}

int main(void)
{
    RESULT9(1) = DBG_ID;
    u32 c0 = DBG_CLK;
    for (volatile int i = 0; i < 100; i++) {}
    RESULT9(2) = DBG_CLK != c0;

    DBG_STR = (u32)"raw ";
    DBG_STR = (u32)"string\n";

    /* %r0% must see r0 at the store, so set it right there. */
    static const char params[] = "r0=%r0%";
    __asm__ volatile(
        "mov r0, #0x12000000\n"
        "orr r0, r0, #0x00340000\n"
        "orr r0, r0, #0x0000AB00\n"
        "orr r0, r0, #0x000000CD\n"
        "str %0, [%1]\n" :: "r"(params), "r"(&DBG_STRL) : "r0", "memory");

    put_chars("chars OK\n");
    DBG_CHAR32 = 'W';
    DBG_CHAR32 = '\n';
    for (const char *s = "ensata\n"; *s; s++)
        ENSATA = (u8)*s;

    __asm__ volatile(
        "mov r1, #7\n"
        "mov r12, r12\n"
        "b 1f\n"
        ".hword 0x6464\n"
        ".hword 0\n"
        ".ascii \"arm msg r1=%%r1%%\"\n"
        ".byte 0\n"
        ".balign 4\n"
        "1:\n" ::: "r1", "r12");
    thumb_message();

    RESULT9(0) = DONE_MAGIC;
    for (;;) {}
}
