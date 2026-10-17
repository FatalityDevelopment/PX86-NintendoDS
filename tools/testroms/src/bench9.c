/* CPU benchmark (not a test): endless CRC-style work over a 16 KB main-RAM buffer,
 * half in ARM state and half in Thumb state. Run it headless and look at the fps. */
#include "../nds.h"

static u8 buffer[16384];

__attribute__((target("thumb"), noinline)) static u32 work_thumb(u32 crc)
{
    for (int i = 0; i < 16384; i++) {
        crc ^= buffer[i];
        for (int b = 0; b < 2; b++)
            crc = (crc >> 1) ^ (0xEDB88320u & -(crc & 1));
    }
    return crc;
}

__attribute__((target("arm"), noinline)) static u32 work_arm(u32 crc)
{
    for (int i = 0; i < 16384; i++) {
        crc ^= buffer[i];
        for (int b = 0; b < 2; b++)
            crc = (crc >> 1) ^ (0xEDB88320u & -(crc & 1));
        buffer[i] = (u8)crc;
    }
    return crc;
}

int main(void)
{
    POWCNT1 = 0x820F;
    RESULT9(0) = DONE_MAGIC;
    u32 crc = 0xFFFFFFFF;
    for (u32 round = 0;; round++) {
        crc = work_arm(crc);
        crc = work_thumb(crc);
        RESULT9(1) = round;
    }
}
