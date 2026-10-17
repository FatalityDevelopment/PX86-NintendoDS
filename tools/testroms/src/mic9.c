/* Microphone test, ARM9 side: nothing to do; the microphone ADC belongs to the ARM7. */
#include "../nds.h"

int main(void)
{
    RESULT9(0) = DONE_MAGIC;
    for (;;) {}
}
