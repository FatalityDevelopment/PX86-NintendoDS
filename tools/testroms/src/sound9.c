/* Sound test, ARM9 side: nothing to do but report done and idle. */
#include "../nds.h"

int main(void)
{
    POWCNT1 = 0x820F;
    RESULT9(0) = DONE_MAGIC;
    for (;;) {}
}
