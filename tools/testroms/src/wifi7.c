/* Wifi test, ARM7 side. The role word at 023003F0h is set by the test before the first frame:
 *   0 = single console: chip ports, timers, and a TX that a loopback medium echoes back into RX
 *   1 = multiplay master, 2 = multiplay slave (two consoles joined by a hub)
 * All roles keep sending beacons and count the beacons they receive from others.
 *
 *   RESULT7(1)  W_ID                      RESULT7(2)  BB register 13h after writing 5Ah
 *   RESULT7(3)  BB[00h] chip ID (6Dh)     RESULT7(4)  RF register 05h after writing 77h (type 3 port)
 *   RESULT7(5)  1 = µs counter advanced   RESULT7(6)  beacon IRQ14 count (from W_BEACON_COUNT)
 *   RESULT7(7)  1 = IF.24 was raised      RESULT7(8)  W_TXSTAT after the LOC1 transmit
 *   RESULT7(9)  RX header length of the echoed frame        RESULT7(10) its first body word
 *   RESULT7(11) RX ring advance in halfwords                 RESULT7(12) W_TX_SEQNO after one transmit
 *   RESULT7(13) beacons received from other consoles
 *   RESULT7(14) master: missing-reply mask in the CMD TX header; slave: payload word of the received CMD
 *   RESULT7(15) master: REPLY frames received;                slave: CMD ACK frames received
 *   RESULT7(16) master: first body word of the reply
 *   RESULT7(17) RX header words 0..1 read through W_RXBUF_RD_DATA (single console)
 *   RESULT7(0)  DONE_MAGIC when the role's checks are finished */
#include "../nds.h"

#define ROLE        REG32(0x023003F0)
#define POWCNT2     REG16(0x04000304)
#define W(off)      REG16(0x04808000 + (off))
#define WRAM16(off) REG16(0x04804000 + (off))

#define W_ID 0x000
#define W_MODE_RST 0x004
#define W_TXSTATCNT 0x008
#define W_IF 0x010
#define W_IE 0x012
#define W_MACADDR 0x018
#define W_BSSID 0x020
#define W_AID_LOW 0x028
#define W_RXCNT 0x030
#define W_POWER_US 0x036
#define W_POWERSTATE 0x03C
#define W_RXBUF_BEGIN 0x050
#define W_RXBUF_END 0x052
#define W_RXBUF_WRCSR 0x054
#define W_RXBUF_WR_ADDR 0x056
#define W_RXBUF_RD_ADDR 0x058
#define W_RXBUF_READCSR 0x05A
#define W_RXBUF_RD_DATA 0x060
#define W_TXBUF_BEACON 0x080
#define W_BEACONINT 0x08C
#define W_TXBUF_CMD 0x090
#define W_TXBUF_REPLY1 0x094
#define W_TXBUF_LOC1 0x0A0
#define W_TXREQ_SET 0x0AE
#define W_TXSTAT 0x0B8
#define W_RXFILTER 0x0D0
#define W_US_COUNTCNT 0x0E8
#define W_US_COMPARECNT 0x0EA
#define W_US_COUNT0 0x0F8
#define W_CMD_COUNT 0x118
#define W_BEACON_COUNT 0x11C
#define W_BB_CNT 0x158
#define W_BB_WRITE 0x15A
#define W_BB_READ 0x15C
#define W_BB_BUSY 0x15E
#define W_BB_POWER 0x168
#define W_RF_DATA2 0x17C
#define W_RF_DATA1 0x17E
#define W_RF_BUSY 0x180
#define W_RF_CNT 0x184
#define W_TX_SEQNO 0x210

#define RX_BEGIN 0x4C00
#define RX_END   0x5F60

static u8 mac[6] = { 0x00, 0x09, 0xBF, 0x00, 0x00, 0x00 };
static u16 rx_read; /* byte offset of the next unread RX packet */

static void wait_us(u32 us)
{
    u16 start = W(W_US_COUNT0);
    while ((u16)(W(W_US_COUNT0) - start) < us) {}
}

static void bb_write(int index, int value)
{
    while (W(W_BB_BUSY) & 1) {}
    W(W_BB_WRITE) = (u16)value;
    W(W_BB_CNT) = (u16)(0x5000 | index);
}

static int bb_read(int index)
{
    while (W(W_BB_BUSY) & 1) {}
    W(W_BB_CNT) = (u16)(0x6000 | index);
    while (W(W_BB_BUSY) & 1) {}
    return W(W_BB_READ) & 0xFF;
}

/* Copy bytes into Wifi RAM (halfword writes only: the hardware ignores STRB). */
static void ram_put(int offset, const u8 *bytes, int count)
{
    for (int i = 0; i < count; i += 2)
        WRAM16(offset + i) = (u16)(bytes[i] | (i + 1 < count ? bytes[i + 1] << 8 : 0));
}

/* TX header + IEEE header at offset; returns the offset of the frame body. */
static int put_frame(int offset, u16 fc, const u8 *da, int body_bytes)
{
    for (int i = 0; i < 12 + 24; i += 2)
        WRAM16(offset + i) = 0;
    WRAM16(offset + 8) = 0x14;                       /* 2 Mbit/s */
    WRAM16(offset + 10) = (u16)(24 + body_bytes + 4); /* header + body + FCS */
    WRAM16(offset + 12) = fc;
    ram_put(offset + 12 + 4, da, 6);
    ram_put(offset + 12 + 10, mac, 6);
    ram_put(offset + 12 + 16, mac, 6);               /* BSSID = sender: everyone uses RXFILTER.0 */
    return offset + 12 + 24;
}

/* RX ring in Wifi RAM, as 0-based byte offsets (the BEGIN/END registers count from 4000h). */
#define RING_BEGIN (RX_BEGIN - 0x4000)
#define RING_END   (RX_END - 0x4000)

static int ring(int offset) { return offset >= RING_END ? offset - RING_END + RING_BEGIN : offset; }
static u32 ring32(int offset) { return WRAM16(ring(offset)) | (u32)WRAM16(ring(offset + 2)) << 16; }

/* Next unread RX packet: its flags (or -1 when the ring is empty); *body = ring offset of the frame body. */
static int rx_next(int *body, int *length)
{
    if (rx_read == W(W_RXBUF_WRCSR) * 2)
        return -1;
    int at = rx_read;
    int flags = WRAM16(at);
    *length = WRAM16(ring(at + 8));
    *body = ring(at + 12 + 24);
    rx_read = (u16)ring(at + ((12 + *length + 3) & ~3));
    W(W_RXBUF_READCSR) = (u16)(rx_read / 2);
    return flags;
}

static u32 beacons_seen;

/* Drain the RX ring, counting other consoles' beacons; returns the flags of the first non-beacon packet. */
static int rx_poll(int *body, int *length)
{
    for (;;) {
        int flags = rx_next(body, length);
        if (flags < 0)
            return -1;
        if ((flags & 0xF) == 1) {
            beacons_seen++;
            RESULT7(13) = beacons_seen;
            continue;
        }
        return flags;
    }
}

static void wifi_init(void)
{
    POWCNT2 |= 2;
    W(W_IE) = 0;
    W(W_POWER_US) = 0;
    W(W_BB_POWER) = 0;
    W(W_MODE_RST) = 0;
    W(W_IF) = 0xFFFF;
    for (int i = 0; i < 3; i++)
        W(W_MACADDR + i * 2) = (u16)(mac[i * 2] | mac[i * 2 + 1] << 8);
    W(W_RXBUF_BEGIN) = RX_BEGIN;
    W(W_RXBUF_END) = RX_END;
    W(W_RXBUF_WR_ADDR) = (RX_BEGIN - 0x4000) / 2;
    W(W_RXBUF_READCSR) = (RX_BEGIN - 0x4000) / 2;
    rx_read = RING_BEGIN;
    W(W_RXCNT) = 0x8001;
    W(W_RXFILTER) = 0x0001;
    W(W_US_COUNTCNT) = 1;
    W(W_MODE_RST) = 1;
    W(W_POWERSTATE) = 2;
}

static const u8 broadcast[6] = { 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF };
static const u8 mp_cmd[6]    = { 0x03, 0x09, 0xBF, 0x00, 0x00, 0x00 };
static const u8 mp_reply[6]  = { 0x03, 0x09, 0xBF, 0x00, 0x00, 0x10 };

static void start_beacons(void)
{
    /* Beacon at 0800h: management/beacon, body = timestamp(8) + interval(2) + capability(2) + 4 tag bytes. */
    int body = put_frame(0x0800, 0x0080, broadcast, 16);
    for (int i = 0; i < 16; i += 2)
        WRAM16(body + i) = 0;
    W(W_TXBUF_BEACON) = 0x8000 | (0x0800 / 2);
    W(W_BEACONINT) = 8;
    W(W_BEACON_COUNT) = 8;
    W(W_US_COMPARECNT) = 1;
}

static void single_console(void)
{
    /* Baseband and RF serial ports. */
    bb_write(0x13, 0x5A);
    RESULT7(2) = (u32)bb_read(0x13);
    RESULT7(3) = (u32)bb_read(0x00);
    W(W_RF_CNT) = 0x0114;                 /* type 3: 20-bit transfers */
    W(W_RF_DATA1) = 0x0577;
    W(W_RF_DATA2) = 5;
    while (W(W_RF_BUSY) & 1) {}
    W(W_RF_DATA1) = 0x0500;
    W(W_RF_DATA2) = 6;
    while (W(W_RF_BUSY) & 1) {}
    RESULT7(4) = W(W_RF_DATA1) & 0xFF;

    /* µs counter. */
    u16 a = W(W_US_COUNT0);
    for (volatile int i = 0; i < 2000; i++) {}
    RESULT7(5) = W(W_US_COUNT0) != a;

    /* Beacon timing: IRQ14 every 2 ms, and the ARM7 IF.24 line. */
    IE = 1u << 24;
    W(W_IE) = 1 << 14;
    W(W_BEACONINT) = 2;
    W(W_BEACON_COUNT) = 2;
    W(W_US_COMPARECNT) = 1;
    u32 beacons = 0;
    for (int ms = 0; ms < 10; ms++) {
        wait_us(1000);
        if (W(W_IF) & (1 << 14)) {
            beacons++;
            W(W_IF) = 1 << 14;
        }
        if (IF & (1u << 24)) {
            RESULT7(7) = 1;
            IF = 1u << 24;
        }
    }
    RESULT7(6) = beacons;
    W(W_IE) = 0;
    W(W_US_COMPARECNT) = 0;

    /* TX via LOC1; the loopback medium sends it back, it lands in the RX ring. */
    int body = put_frame(0x0000, 0x0008, broadcast, 4);
    ram_put(body, (const u8 *)"PX86", 4);
    W(W_IF) = 0xFFFF;
    W(W_TXBUF_LOC1) = 0x8000;
    W(W_TXREQ_SET) = 1;
    while (!(W(W_IF) & (1 << 1))) {}
    RESULT7(8) = W(W_TXSTAT);
    RESULT7(12) = W(W_TX_SEQNO);

    u16 start = W(W_RXBUF_WRCSR);
    while (!(W(W_IF) & 1)) {}
    int at, length;
    rx_poll(&at, &length);
    RESULT7(9) = (u32)length;
    RESULT7(10) = ring32(at);
    RESULT7(11) = (u32)(W(W_RXBUF_WRCSR) - start);

    W(W_RXBUF_RD_ADDR) = start * 2;
    u32 h0 = W(W_RXBUF_RD_DATA);
    u32 h1 = W(W_RXBUF_RD_DATA);
    RESULT7(17) = h0 | h1 << 16;
}

static void master(void)
{
    wait_us(20000); /* let the slave get ready */
    for (int attempt = 0; attempt < 20 && RESULT7(15) == 0; attempt++) {
        int body = put_frame(0x0400, 0x0228, mp_cmd, 8);
        WRAM16(body + 0) = 0x0106;
        WRAM16(body + 2) = 0x0002;          /* slave 1 */
        ram_put(body + 4, (const u8 *)"CMD!", 4);
        W(W_IF) = 1 << 12;
        W(W_CMD_COUNT) = 0x0200;
        W(W_TXBUF_CMD) = 0x8000 | (0x0400 / 2);
        W(W_TXREQ_SET) = 2;
        while (!(W(W_IF) & (1 << 12))) {}
        RESULT7(14) = WRAM16(0x0400 + 2);
        int at, length, flags;
        while ((flags = rx_poll(&at, &length)) >= 0) {
            if ((flags & 0xF) == 0xE) {
                RESULT7(15)++;
                RESULT7(16) = ring32(at);
            }
        }
        wait_us(5000);
    }
}

static void slave(void)
{
    W(W_AID_LOW) = 1;
    int body = put_frame(0x0600, 0x0118, mp_reply, 4);
    ram_put(body, (const u8 *)"RPLY", 4);
    W(W_TXBUF_REPLY1) = 0x8000 | (0x0600 / 2);
    for (int ms = 0; ms < 3000 && RESULT7(15) == 0; ms++) {
        int at, length, flags;
        while ((flags = rx_poll(&at, &length)) >= 0) {
            if ((flags & 0xF) == 0xC) {
                RESULT7(14) = ring32(at + 4); /* after REPLYTIME and the slave mask: "CMD!" */
                W(W_TXBUF_REPLY1) = 0x8000 | (0x0600 / 2);
            }
            if ((flags & 0xF) == 0xD)
                RESULT7(15)++;
        }
        wait_us(1000);
    }
}

int main(void)
{
    u32 role = ROLE;
    mac[5] = (u8)(role + 1);
    wifi_init();
    RESULT7(1) = W(W_ID);
    if (role == 0)
        single_console();
    start_beacons();
    if (role == 1)
        master();
    else if (role == 2)
        slave();
    else
        wait_us(30000);
    RESULT7(0) = DONE_MAGIC;
    for (;;) {
        int at, length;
        rx_poll(&at, &length);
    }
}
