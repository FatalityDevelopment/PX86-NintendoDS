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

## Boot-time RAM locations (direct boot)
027FF800h/027FFC00h chip ID ×2, header CRC, secure CRC · 027FFC40h boot indicator (1 = cartridge) ·
027FFC80h firmware user settings · 027FFE00h cartridge header.
