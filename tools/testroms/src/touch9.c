/* Touch test, ARM9 side: nothing to do; the ARM7 samples the touch screen. */
#include "../nds.h"

int main(void)
{
    RESULT9(0) = DONE_MAGIC;
    for (;;) {}
}
