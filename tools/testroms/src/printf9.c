/* printf/sprintf hook test, ARM9 side. The ROM has its own small formatter, as a
 * retail game does: the output goes to a buffer nobody reads, so only hooks see it.
 *   RESULT9(1) address of fmt_vsnprintf (the formatter the scanner should find)
 *   RESULT9(2) address of game_printf (printf(fmt, ...), output discarded)
 *   RESULT9(3) address of game_sprintf (sprintf(buf, fmt, ...))
 *   RESULT9(4) address of the 64-bit printf call's format, game_printf64 (printf(fmt, ...))
 * Expected lines, each once (the scanner + manual hooks must not print a call twice):
 *   "score=42 name=Px86 hex=0000BEEF"   (game_sprintf → fmt_vsnprintf)
 *   "[-7  ] [  ok] [+5] [c=Z] [50%]"    (game_printf → fmt_vsnprintf)
 *   "loop 0" "loop 1" "loop 2"          (same format, same depth: all kept)
 *   "big=123456789012 after=9"          (64-bit argument on the stack) */
#include "../nds.h"
#include "printf_impl.h"

int main(void)
{
    static char buf[128];
    RESULT9(1) = (u32)fmt_vsnprintf;
    RESULT9(2) = (u32)game_printf;
    RESULT9(3) = (u32)game_sprintf;
    /* Let the test add hooks or the scanner run before anything is printed. */
    while (RESULT9(5) != 1) {}

    game_sprintf(buf, "score=%d name=%s hex=%08X", 42, "Px86", 0xBEEF);
    game_printf("[%-4d] [%4s] [%+d] [c=%c] [%d%%]\n", -7, "ok", 5, 'Z', 50);
    for (int i = 0; i < 3; i++)
        game_printf("loop %d\n", i);
    game_printf("big=%lld after=%d\n", 123456789012LL, 9);

    RESULT9(0) = DONE_MAGIC;
    for (;;) {}
}
