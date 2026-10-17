[← Back to Documentation Index](README.md)

# Hardware reference (cheat sheet)

A summary of the facts the code relies on most. For the full details see GBATEK, especially the sections named in each class's
`Reference:` line.

## CPUs and clocks
| | ARM9 | ARM7 |
|---|---|---|
| Core | ARM946E-S, ARMv5TE | ARM7TDMI, ARMv4T |
| Clock | 67.027964 MHz | 33.513982 MHz (system clock) |
| Extras | CP15, 32 KB ITCM, 16 KB DTCM, 8 KB I-cache, 4 KB D-cache | — |
| Exception vectors | FFFF0000h (high) | 00000000h |

## Memory maps
| Address | ARM9 | ARM7 |
|---|---|---|
| 00000000h | ITCM 32 KB | BIOS 16 KB |
| 02000000h | Main RAM 4 MB | Main RAM 4 MB |
| 03000000h | Shared WRAM (WRAMCNT) | Shared WRAM (WRAMCNT), ARM7 WRAM 64 KB at 03800000h |
| 04000000h | I/O | I/O (wifi at 04800000h) |
| 05000000h | Palettes 2 KB | — |
| 06000000h | VRAM (BG-A 06000000h, BG-B 06200000h, OBJ-A 06400000h, OBJ-B 06600000h, LCDC 06800000h) | VRAM C/D (WRAM mode) |
| 07000000h | OAM 2 KB | — |
| 08000000h | GBA slot | GBA slot |
| FFFF0000h | BIOS 4 KB | — |

WRAMCNT (ARM9 04000247h): 0 = 32K to ARM9, 1 = 2nd half ARM9 / 1st half ARM7, 2 = 1st half ARM9 / 2nd half ARM7, 3 = all to ARM7.

## Display
- 256×192 per screen, 355×263 dots per frame, 59.8261 Hz. HBlank after 256 dots, VBlank lines 192–261.
- POWCNT1 (04000304h): bit 0 LCDs, 1 engine A, 2/3 3D render/geometry, 9 engine B, 15 swap (1 = engine A on the top screen).
- VRAM banks: A–D 128 KB, E 64 KB, F/G 16 KB, H 32 KB, I 16 KB; VRAMCNT_x = enable (bit 7) | OFS (bits 3–4) | MST (bits 0–2).

## Interrupt bits (IE/IF)
0 VBlank · 1 HBlank · 2 VCount · 3–6 Timer 0–3 · 7 RTC (ARM7) · 8–11 DMA 0–3 · 12 Keypad · 13 GBA slot · 16 IPC sync ·
17 IPC send empty · 18 IPC recv not empty · 19 Cart done · 20 Cart IREQ · 21 GX FIFO (ARM9) · 22 Hinge · 23 SPI · 24 Wifi (ARM7)

## Frequently used registers
| Address | Name | CPU | Notes |
|---|---|---|---|
| 04000000h | DISPCNT | 9 | Engine A (B at 04001000h) |
| 04000004h | DISPSTAT | 9/7 | Per-CPU IRQ enables, LYC |
| 040000B0h | DMAxSAD/DAD/CNT | 9/7 | 12 bytes per channel |
| 04000100h | TMxCNT | 9/7 | Counter/reload + control |
| 04000130h | KEYINPUT | 9/7 | 0 = pressed |
| 04000136h | EXTKEYIN | 7 | X, Y, pen, hinge |
| 04000180h | IPCSYNC / IPCFIFOCNT / IPCFIFOSEND | 9/7 | FIFO receive at 04100000h |
| 040001A0h | AUXSPICNT / ROMCTRL / command | 9/7 | Data at 04100010h |
| 040001C0h | SPICNT / SPIDATA | 7 | Power, firmware, touch screen |
| 04000208h | IME / IE / IF | 9/7 | |
| 04000240h | VRAMCNT_A–I, WRAMCNT | 9 | 8-bit registers |
| 04000280h | DIVCNT … SQRT_PARAM | 9 | Hardware maths |
| 04000300h | POSTFLG / HALTCNT | 9/7 | HALTCNT is ARM7 only |
| 04000400h | Sound channels | 7 | 16 bytes × 16 |

## Debug ports (emulator, both CPUs)

no$gba pseudo-I/O (`EmulatorCore/Debugging/DebugOutput.cs`):

| Address | Access | Meaning |
|---|---|---|
| 04FFFA00h | R | emulator ID |
| 04FFFA10h, 14h, 18h | W | string pointer: raw / `%param%` / `%param%` + line feed |
| 04FFFA1Ch | W | char out |
| 04FFFA20h | R | 64-bit cycle counter |
| 04FFF000h | W, ARM9 | Ensata char out |

`mov r12,r12` followed by a branch over `6464h, flags, text` is an in-code message. GBATEK: "DS Debug Registers
(Emulator/Devkits)", "Debug Messages".

## Microphone (ARM7)

The microphone is the TSC2046's AUX input (channel 6, single-ended: command E4h for 12-bit, ECh for 8-bit).
Reply bits follow one dummy bit, MSB first: 12-bit V arrives as (V >> 5, V << 3), 8-bit as (V >> 1, V << 7).
Silence is 800h. Power manager register 2 bit 0 switches the amplifier on; register 3 sets the gain
(20×, 40×, 80×, 160×). `TouchScreen.Microphone` (an `IMicrophoneSource`) is sampled at the scheduler time
of each read.

## Wifi (ARM7)

`EmulatorCore/Hardware/Wifi/WifiController.cs` documents the full register map (GBATEK "DS Wifi").

- **Registers:** at 04808000h, mirrored at 04800000h. 8 KB packet RAM at 04804000h. 8-bit writes are ignored.
- **RX ring:** BEGIN/END are Wifi-window addresses (4000h–6000h). WRCSR/READCSR/WR_ADDR are halfword offsets
  into RAM. Each packet is a 12-byte RX header plus the frame, padded to 4 bytes.
- **TX:** a 12-byte TX header plus the IEEE frame. The length counts the 4-byte FCS, which the hardware adds.
  LOC3 goes first, LOC1 last.
- **IF.24 is edge-triggered:** it is only set when W_IF & W_IE goes from 0 to non-zero.
- **Timers:** the µs counter, W_BEACON_COUNT and W_POST_BEACON (1 ms units) drive IRQ13, IRQ14 and IRQ15.
- **Multiplay:** the master sends a CMD (W_TXBUF_CMD); each slave in the body's slave mask answers with REPLY2;
  the master sends the CMD ACK and raises IRQ12, which also fires when W_CMD_COUNT runs out.
- **Firmware Wifi block (firmware 02Ah–163h):** MAC at 036h, RF type at 040h (2 = DS, 3 = DS-Lite), initial
  W_CONFIG values at 044h–062h, BB values at 064h. Access point slots are at 3FA00h, 3FB00h and 3FC00h.

## Boot-time RAM locations (direct boot)
027FF800h/027FFC00h chip ID ×2, header CRC, secure CRC · 027FFC40h boot indicator (1 = cartridge) ·
027FFC80h firmware user settings · 027FFE00h cartridge header.
