---
name: nds-add-io-register
description: Add or fix a memory-mapped I/O register in Px86.NintendoDS (ARM9 or ARM7 side) — bus dispatch, 8/16/32-bit access, read/write masks, side effects, doc comment and a test. Use when --trace-io reports an unhandled register, or a game depends on a register that isn't emulated.
argument-hint: [address, e.g. 0x04000304]
---

# Add an I/O register

## 1. Research
- Find the register in GBATEK ("DS I/O Maps" lists both CPUs). Note which CPU(s) can see it, its width, which bits are
  read-only or write-only, and its side effects (write-1-to-clear, start-on-write, FIFO pops).
- Some registers exist only on one CPU (e.g. DIVCNT ARM9, SPICNT/RTC ARM7). Others are shared state with a separate view per
  CPU (IPC, KEYCNT, DISPSTAT, IME/IE/IF).

## 2. Put the state in a device class
- Existing devices: `Hardware/` (IRQ, timers, DMA, IPC, math, keypad, RTC, SPI, SystemControl), `Cartridge/`, `Video/`, `Audio/`.
  Add a new `public sealed class` only for a new block of hardware.
- The class doc comment starts with the register map and ends with `Reference: GBATEK "<section>"`.
- Add `public const uint REG_X = 0x...;` (offset from 0x04000000).
- Write methods take `(value, mask)` so 8/16-bit partial writes work: `reg = (reg & ~mask) | (value & mask & writableBits)`.

## 3. Wire it into the bus
- `Arm9MemoryBus.cs` / `Arm7MemoryBus.cs`:
  - Registers that must be accessed as 32 bits (FIFOs, DMA, IE/IF, 64-bit math values) go in `TryReadIo32` / `TryWriteIo32`.
    A 16-bit access to them is converted automatically with a shifted mask.
  - Plain 16-bit registers go in `ReadIo16Core` / `WriteIo16Core`.
  - True 8-bit registers (VRAMCNT, WRAMCNT, POSTFLG, HALTCNT) go in `ReadIo8` / `WriteIo8`.
- Unknown accesses raise `UnhandledIo`. Running the frontend with `--trace-io` prints them.

## 4. Timing / interrupts
- Read time from `scheduler.CurrentTime`. Schedule with `ScheduleFromNow(SchedulerEvent.X, delay)` (add a new enum value
  plus a `SetHandler` call if needed).
- Raise interrupts with `InterruptController.Request(bit)`. Trigger DMA with `DmaController.Trigger(DmaTiming.X)`.

## 5. Test
- Write a test ROM `tools/testroms/src/<name>9.c` that pokes the register through `nds.h` macros and stores what it
  observes in `RESULT9(n)`. Run `tools/testroms/build.sh`, then assert with `TestRomRunner` in `Px86.NintendoDS.TestProject/Emulator/`.
- For pure logic (e.g. the divider), a direct unit test on the device class is fine too.
