/* Debug output test, ARM7 side: the ARM7 has the same ports. Expected line: "hello from arm7". */
#include "../nds.h"

int main(void)
{
    REG32(0x04FFFA18) = (u32)"hello from arm7";
    RESULT7(0) = DONE_MAGIC;
    for (;;) {}
}
