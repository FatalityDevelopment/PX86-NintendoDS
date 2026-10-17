/* ARM7 half of the system test: echo one IPC FIFO word +1, publish IPCSYNC = 7. */
#include "../nds.h"

int main(void)
{
    IPCSYNC = 7 << 8;
    IPCFIFOCNT = 0x8008;
    while (IPCFIFOCNT & 0x100) {}
    u32 value = IPCFIFORECV;
    IPCFIFOSEND = value + 1;
    RESULT7(1) = value;
    RESULT7(0) = DONE_MAGIC;
    for (;;) {}
}
