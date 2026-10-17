---
name: nds-hardware-reference
description: Where to look up Nintendo DS hardware behaviour for Px86.NintendoDS — GBATEK section map, local docs, which reference emulator to read per subsystem, and the licensing rule for using them. Use before implementing or changing any hardware behaviour.
argument-hint: [subsystem, e.g. "3D", "SPU", "cart protocol"]
---

# DS hardware reference

## Primary spec: GBATEK
Fetch it once: `curl -sSL https://problemkaputt.de/gbatek.txt -o /tmp/gbatek.txt`, then
`grep -n "^DS " /tmp/gbatek.txt` lists the DS chapters. Useful sections:

| Topic | GBATEK section |
|---|---|
| Memory map / I/O map | "DS Memory Maps", "DS I/O Maps" |
| WRAM / VRAM banking | "DS Memory Control - WRAM", "DS Memory Control - VRAM" |
| CPU / CP15 | "ARM CPU Reference", "ARM CP15 System Control Coprocessor" |
| Interrupts / timers / DMA | "DS Interrupts", "DS Timers", "DS DMA Transfers" |
| IPC / maths | "DS Inter Process Communication (IPC)", "DS Maths" |
| 2D video | "DS Video BG Modes / Control", "DS Video OBJs", "DS Video Extended Palettes", "GBA LCD …" |
| Capture / main-memory display | "DS Video Capture and Main Memory Display Mode" |
| 3D | "DS 3D Overview", "DS 3D I/O Map", "DS 3D Geometry Commands", "DS 3D Rendering", "DS 3D Texture Formats" |
| Sound | "DS Sound", "DS Sound Channels", "DS Sound Capture" |
| Cartridge | "DS Cartridge Header", "DS Cartridge Protocol", "DS Cartridge Backup", "DS Encryption by Gamecode/Idcode (KEY1)" |
| SPI devices | "DS Firmware …", "DS Touch Screen Controller (TSC)", "DS Power Management Device" |
| RTC | "DS Real-Time Clock (RTC)" |
| BIOS functions | "BIOS Functions", "BIOS Decompression Functions", "BIOS RAM Usage" |

Local summaries: `docs/hardware-reference.md` (memory map and register cheat sheet) and `docs/ARCHITECTURE.md`
(how our classes map onto the hardware).

## Reference emulators (learn, don't copy)
The emulation wiki's Nintendo DS page (https://emulation.gametechwiki.com/index.php/Nintendo_DS_emulators) lists these.
Fetch sources from `raw.githubusercontent.com/<owner>/<repo>/<branch>/<path>`.

| Emulator | Read it for |
|---|---|
| melonDS (melonDS-emu/melonDS) | Accurate 3D rasterizer, GPU timing, cartridge, wifi, firmware/boot details |
| NooDS (Hydr8gon/NooDS) | Compact, readable implementations of every subsystem; a good first read |
| DeSmuME (TASEmulators/desmume) | Broad game compatibility hacks, save-type database ideas, debugging tools |
| SkyEmu (skylersaleh/SkyEmu) | Single-file, easy-to-follow C implementation |
| Dust (kelpsyberry/dust) | Modern Rust design, precise timing research |

**Licensing:** melonDS, NooDS and DeSmuME are GPL. This project is not GPL, so never copy their code, tables or
comments. Read them to understand the behaviour, confirm it in GBATEK, and write it fresh in our style.
