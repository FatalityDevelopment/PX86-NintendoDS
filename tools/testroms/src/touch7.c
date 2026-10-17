/* Touch test, ARM7 side: sample the TSC2046 over SPI like the SDK's touch driver
 * (12-bit X on channel 5, Y on channel 1), then convert to pixels with the firmware
 * user-settings calibration at 027FFC80h, using GBATEK's formula.
 *   RESULT7(1) = pixel x, RESULT7(2) = pixel y, RESULT7(3) = pen down, RESULT7(4) = samples taken
 *   RESULT7(5) = raw adc x, RESULT7(6) = raw adc y */
#include "../nds.h"

#define SPICNT    REG16(0x040001C0)
#define SPIDATA   REG16(0x040001C2)
#define EXTKEYIN  REG16(0x04000136)
#define USER      ((volatile u8 *)0x027FFC80)

static u16 user16(int offset) { return (u16)(USER[offset] | (USER[offset + 1] << 8)); }

static void spi_wait(void) { while (SPICNT & 0x80) {} }

/* Device 2 (touch screen), 2 MHz, enabled; bit 11 keeps chip select between bytes. */
static int tsc_read(u8 command)
{
    SPICNT = 0x8A01;
    SPIDATA = command;
    spi_wait();
    SPIDATA = 0;
    spi_wait();
    int high = SPIDATA & 0xFF;
    SPICNT = 0x8201;
    SPIDATA = 0;
    spi_wait();
    int low = SPIDATA & 0xFF;
    return ((high << 8) | low) >> 3 & 0xFFF;
}

static int to_pixel(int adc, int adc1, int scr1, int adc2, int scr2)
{
    return (adc - adc1) * (scr2 - scr1) / (adc2 - adc1) + (scr1 - 1);
}

int main(void)
{
    RESULT7(0) = DONE_MAGIC;
    for (u32 n = 1;; n++) {
        int pen = (EXTKEYIN & 0x40) == 0;
        if (pen) {
            int ax = tsc_read(0xD1);   /* channel 5 = X, 12-bit, differential */
            int ay = tsc_read(0x91);   /* channel 1 = Y */
            RESULT7(5) = (u32)ax;
            RESULT7(6) = (u32)ay;
            RESULT7(1) = (u32)to_pixel(ax, user16(0x58) & 0xFFF, USER[0x5C], user16(0x5E) & 0xFFF, USER[0x62]);
            RESULT7(2) = (u32)to_pixel(ay, user16(0x5A) & 0xFFF, USER[0x5D], user16(0x60) & 0xFFF, USER[0x63]);
        }
        RESULT7(3) = (u32)pen;
        RESULT7(4) = n;
    }
}
