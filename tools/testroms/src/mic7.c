/* Microphone test, ARM7 side: read TSC channel 6 (AUX = microphone) over SPI like the SDK,
 * first with the power manager's amplifier off, then on.
 *   RESULT7(1) 12-bit reading, amplifier off          RESULT7(2) 12-bit reading, amplifier on
 *   RESULT7(3) min of 64 readings                     RESULT7(4) max of 64 readings
 *   RESULT7(5) 8-bit reading                          RESULT7(6) 12-bit reading at gain 2 (80x)
 *   RESULT7(0) DONE_MAGIC */
#include "../nds.h"

#define SPICNT  REG16(0x040001C0)
#define SPIDATA REG16(0x040001C2)

static void spi_wait(void) { while (SPICNT & 0x80) {} }

/* Power manager (SPI device 0): register index, then data. */
static void pm_write(int reg, int value)
{
    SPICNT = 0x8802;
    SPIDATA = (u16)reg;
    spi_wait();
    SPICNT = 0x8002;
    SPIDATA = (u16)value;
    spi_wait();
}

/* TSC (device 2): command, then two reply bytes; dummy bit first, MSB first. */
static int tsc_read(int command, int eight_bit)
{
    SPICNT = 0x8A01;
    SPIDATA = (u16)command;
    spi_wait();
    SPIDATA = 0;
    spi_wait();
    int high = SPIDATA & 0xFF;
    SPICNT = 0x8201;
    SPIDATA = 0;
    spi_wait();
    int low = SPIDATA & 0xFF;
    int bits = (high << 8) | low;
    return eight_bit ? (bits >> 7) & 0xFF : (bits >> 3) & 0xFFF;
}

#define MIC12 0xE4 /* start, channel 6, 12-bit, single-ended, power-down mode 0 */
#define MIC8  0xEC /* same, 8-bit */

int main(void)
{
    pm_write(2, 0);
    RESULT7(1) = (u32)tsc_read(MIC12, 0);

    pm_write(2, 1);
    pm_write(3, 0);
    RESULT7(2) = (u32)tsc_read(MIC12, 0);
    int lo = 0xFFF, hi = 0;
    for (int i = 0; i < 64; i++) {
        int v = tsc_read(MIC12, 0);
        if (v < lo) lo = v;
        if (v > hi) hi = v;
        for (volatile int d = 0; d < 200; d++) {}
    }
    RESULT7(3) = (u32)lo;
    RESULT7(4) = (u32)hi;
    RESULT7(5) = (u32)tsc_read(MIC8, 1);
    pm_write(3, 2);
    RESULT7(6) = (u32)tsc_read(MIC12, 0);
    RESULT7(0) = DONE_MAGIC;
    for (;;) {}
}
