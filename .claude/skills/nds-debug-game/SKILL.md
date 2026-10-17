---
name: nds-debug-game
description: Triage a Nintendo DS game that doesn't boot, freezes, renders wrongly or misbehaves in Px86.NintendoDS. Covers headless runs with screenshots, I/O tracing, CPU state, common DS emulation pitfalls, and comparing against reference emulators. Use when the user reports a broken game.
argument-hint: [rom path or game name]
---

# Debug a game

The user's ROMs, BIOS and firmware are on their machine, not in the repo. Ask for the path, or ask them to run the commands
and paste the output. Never commit their files.

## 1. Reproduce headless
```bash
dotnet run --project Px86.NintendoDS.Emulator -c Release -- game.nds --headless --frames 900 --screenshot /tmp/shot.png
```
- Look at the PNG (top screen above, bottom below).
- The output reports the speed and both CPUs' PC. Run again with `--frames 300/600/1200`: a PC that doesn't move means a
  spin loop — disassemble around it.
- `--trace-io` logs unhandled I/O registers. A register polled in a loop is usually the culprit → `nds-add-io-register`.

## 2. Inspect from a test or a scratch program
`new NdsEmulator()`, `LoadRom(NdsRom.Load(path))`, `Boot()`, `RunFrame()`, then read `Arm9`/`Arm7` (`ToString()` dumps the
registers) and memory through `Arm9Bus.BusRead32`. Disassemble with `ArmDisassembler.Disassemble(op, addr)` /
`ThumbDisassembler`. Hook `Arm9.UnknownInstruction`, `Hle9.UnsupportedSwi` and the bus `UnhandledIo` events.

## 3. Usual suspects (roughly in order of frequency)
1. **ARM7 ↔ ARM9 handshake stuck** — IPC FIFO/SYNC IRQs (edge-triggered!), IPCFIFOCNT error bit, both CPUs halted.
2. **Interrupt plumbing** — IE/IF/IME, DISPSTAT IRQ enables, IntrWait check bits at [DTCM+3FF8h]/[0380FFF8h], HALT wake-up.
3. **Cartridge reads** — ROMCTRL block size/busy/ready bits, B7 address wrapping at 1000h, DMA mode "DS cartridge", secure area redirect.
4. **Save chip** — wrong auto-detected type (see `SaveMemory`), RDSR busy bit, missing command. Delete the `.sav` and retry.
5. **VRAM mapping** — VRAMCNT MST/OFS for BG/OBJ/texture/ext-palette. Dump `Vram.BgA` etc. Overlapping banks.
6. **DMA timing modes** — HBlank/VBlank/display-start/GXFIFO triggers, repeat with dest reload.
7. **Timers** — prescaler, cascade, reload on start.
8. **CPU edge cases** — LDM/STM writeback quirks, misaligned loads, ARMv5 interworking. Add a CPU test (`nds-implement-instruction`).
9. **HLE BIOS gaps** — an unsupported SWI, or a decompression callback. Compare by running with real BIOS dumps if the user has them
   (`--bios9 --bios7`).
10. **3D / sound** — check `docs/roadmap.md` for what's implemented yet.

## 4. Compare with a reference emulator
Read the matching subsystem in melonDS (`src/`), NooDS (`src/`) or DeSmuME (raw.githubusercontent.com) to see how they handle the
case. Understand it, then implement it from GBATEK in our style — never paste GPL code.

## 5. Lock it in
Write a regression test (a test ROM or a unit test) that fails before the fix and passes after it. Note the game and the fix in the
compatibility table in `docs/roadmap.md`.
