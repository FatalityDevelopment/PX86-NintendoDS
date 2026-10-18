/* printf hook test, ARM7 side: the same formatter built as Thumb code.
 *   RESULT7(1) address of fmt_vsnprintf (bit 0 set: Thumb)
 * Expected line (ARM7): "[sprintf] arm7 v=-3 s=thumb" */
#include "../nds.h"
#define PRINTF_ATTR __attribute__((target("thumb")))
#include "printf_impl.h"

int main(void)
{
    static char buf[64];
    RESULT7(1) = (u32)fmt_vsnprintf;
    while (RESULT9(5) != 1) {}
    game_sprintf(buf, "arm7 v=%d s=%s", -3, "thumb");
    RESULT7(0) = DONE_MAGIC;
    for (;;) {}
}
