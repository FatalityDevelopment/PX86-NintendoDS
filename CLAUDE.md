# CLAUDE.md — Px86.NintendoDS

## CRITICAL — Build artifacts and copyrighted files (MUST READ)

**NEVER commit files under `bin/` or `obj/`.** CI rejects any change that contains them.
Before every commit run `git diff --cached --name-only | grep -iE '(^|/)(bin|obj)/'` — if it prints anything, unstage those files.
**Do NOT use `git add .` or `git add -A` at the repo root** — add specific files or project directories.

**NEVER commit game ROMs, BIOS dumps, firmware dumps or save files** (`*.nds`, `bios7.bin`, `bios9.bin`, `firmware.bin`, `*.sav`).
They are copyrighted or personal. The only `.nds` files allowed are the test ROMs in
`Px86.NintendoDS.TestProject/TestRoms/`, which are built from our own sources in `tools/testroms/`.

**Learn from GPL emulators, don't copy them.** melonDS, DeSmuME and NooDS are GPL. Read them to understand behaviour, then
write our own implementation from GBATEK. Never paste their code.

## Project overview

A Nintendo DS emulator in C# (.NET 8). It emulates both CPUs (ARM946E-S ARMv5TE and ARM7TDMI ARMv4T), the memory system,
2D/3D graphics, sound, the cartridge (ROM + save chip) and the frontend. It boots games directly with an HLE BIOS (no dumps needed),
or through real BIOS/firmware dumps the user supplies.

## Repository structure

```
Px86.NintendoDS.sln
├── Px86.NintendoDS.Common/          # ARM CPU interpreter, CP15, disassembler, memory helpers
│   ├── Cpu/                         # ArmCpu, Alu, ProcessorMode
│   ├── Cpu/Arm/                     # ARM-state handlers + 4096-entry decode table
│   ├── Cpu/Thumb/                   # Thumb handlers + 1024-entry decode table
│   ├── Cpu/Coprocessors/Cp15.cs     # ARM9 system control coprocessor (TCM, vectors, halt)
│   ├── Disassembler/                # ARM and Thumb disassemblers
│   └── Interfaces/                  # IArmMemoryBus, ICoprocessor
├── Px86.NintendoDS.FileFormats/     # .nds header/NitroFS, CRC16, PNG + JPEG writers
├── Px86.NintendoDS.EmulatorCore/    # The console
│   ├── NdsEmulator.cs               # Owns everything; RunFrame / slice loop
│   ├── Scheduler.cs                 # Event scheduler (time in ARM9 cycles)
│   ├── Arm9MemoryBus.cs / Arm7MemoryBus.cs   # Memory maps + I/O dispatch
│   ├── DirectBoot.cs, GameSession.cs
│   ├── Hosting/EmulatorHost.cs      # Runs the console on its own thread; thread-safe control (Invoke, pause, step…)
│   ├── Debugging/                   # Breakpoints/watchpoints (DebugMonitor), debug memory access, game debug output (no$gba ports), string scanner
│   ├── Remote/                      # PINE socket (28013) + GS frame stream (28014), see docs/pine-protocol.md
│   ├── Network/                     # Local wireless (UDP multicast), software access point + NAT router (online)
│   ├── Hardware/                    # IRQ, timers, DMA, IPC, math, keypad, RTC, SPI/, Wifi/, system control
│   ├── Cartridge/                   # Slot protocol, KEY1, save memory (EEPROM/FLASH/FRAM)
│   ├── Bios/                        # HLE BIOS (SWIs), BIOS stubs, sound tables
│   ├── Video/                       # LCD timing, VRAM banks, 2D engines, Gpu3D/
│   └── Audio/                       # SPU
├── Px86.NintendoDS.Emulator/        # Silk.NET frontend (SDL window, OpenGL, OpenAL, input, headless mode)
│   ├── Ui/                          # Dear ImGui menu bar, settings, file browser, notifications (ASCII text only)
│   └── ScreenLayout.cs              # Screen placement + mouse→touch mapping (pure, unit-tested)
├── Px86.NintendoDS.TestProject/     # MSTest suite: Cpu/, Emulator/, Video/, Remote/, Debugging/, Frontend/, TestRoms/
├── tools/testroms/                  # clang-based test ROM toolchain (C sources → TestRoms/*.nds)
└── docs/                            # Architecture, hardware reference, learning resources, roadmap
```

## Build and test

```bash
dotnet build Px86.NintendoDS.sln -c Release
dotnet test Px86.NintendoDS.TestProject
dotnet run --project Px86.NintendoDS.Emulator -c Release -- game.nds
dotnet run --project Px86.NintendoDS.Emulator -c Release -- game.nds --headless --frames 600 --screenshot out.png
dotnet run --project Px86.NintendoDS.Emulator -c Release -- game.nds --headless   # PINE + GS server until Ctrl+C
tools/testroms/build.sh               # rebuild test ROMs (needs clang, ld.lld, llvm-objcopy, python3)
```

In Claude Code cloud sessions the SessionStart hook (`.claude/hooks/session-start.sh`) installs the .NET 8 SDK into `~/.dotnet`.
If `dotnet` is missing in a shell, use `export PATH=$HOME/.dotnet:$PATH`. There is no display: use `--headless --screenshot`,
or `xvfb-run` to run the real window, and read the PNG to check rendering.

## Key conventions

### Code style (same as CodeDesigner)

- Namespaces follow folders, block-scoped: `namespace Px86.NintendoDS.EmulatorCore.Hardware { ... }`.
- `public sealed class`. Each peripheral starts with a `/// <summary>` that lists its register map
  (address, bits, meaning) and ends with `Reference: GBATEK "<section name>"`.
- Register offsets are `public const uint REG_NAME = 0x...;` relative to 0x04000000.
- Group members with `// ── Section ──────` dividers.
- Hooks are C# events (`UnknownInstruction`, `ScanlineReady`, `UnhandledIo`, …).
- Comments explain hardware behaviour and *why*. Don't narrate what the code obviously does.

### CPU (`Px86.NintendoDS.Common/Cpu`)

- One `ArmCpu` class covers both cores. `IsArmV5` gates ARMv5-only behaviour: BLX, CLZ, QADD, SMLAxy, LDRD/STRD,
  LDR/LDM/POP-to-PC interworking, and the different misaligned-LDRH and LDM/STM writeback rules.
- `Pc` is the address of the instruction being executed. During execution `R[15]` = PC + 8 (ARM) or + 4 (Thumb).
  Handlers that write PC must call `Branch`, `BranchExchange` or `LoadPc` — never assign `R[15]` directly.
- An ARM decode index is `(op >> 16 & 0xFF0) | (op >> 4 & 0xF)`. A Thumb index is `op >> 6`.
- SWI numbers: ARM = bits 23–16, Thumb = bits 7–0. `SwiHandler` (HLE) returns true when it handled the call.
- Test opcodes come from `llvm-mc -triple=armv5te -show-encoding` (or `thumbv5te`). Don't hand-encode them.
- The JIT (`EmulatorCore/Jit/JitCompiler.cs` + `Common/Cpu/JitOps.cs`) must match the interpreter bit for bit. When you
  change a handler's behaviour or cycles, change its inlined twin too. `TestProject/Jit/` compares the two in lockstep.

### Timing and the main loop

- Scheduler time is in ARM9 cycles (67.03 MHz). One ARM7 cycle = 2 ARM9 cycles. Timers/LCD use the 33.51 MHz system clock (×2).
- `NdsEmulator.RunSlice`: the ARM9 runs to the slice end, then the ARM7, then due events fire. Slices are ≤ 64 cycles.
- Devices read the precise time from `Scheduler.CurrentTime` and schedule with `ScheduleFromNow`.
- LCD: 4260 ARM9 cycles per line, HBlank at 3072, 263 lines, VBlank on lines 192–261. `RunFrame` ends at line 192.

### Memory map facts that bite

- ARM9: ITCM 32 KB at 0 (mirrored up to CP15's ITCM size), DTCM 16 KB movable (direct boot: 0x03000000), main RAM 4 MB mirrored
  over 0x02xxxxxx, shared WRAM by WRAMCNT, palette/VRAM/OAM ignore 8-bit writes, BIOS at 0xFFFF0000.
- ARM7: BIOS 16 KB at 0, ARM7 WRAM 64 KB at 0x03800000, shared WRAM at 0x03000000 (WRAMCNT 0 → mirrors ARM7 WRAM),
  VRAM C/D at 0x06000000 when MST = 2.
- DMA cannot see the TCMs (`DmaController.Active` makes the ARM9 bus skip them).
- IRQ handler pointers: ARM9 `[DTCM+0x3FFC]`, ARM7 `[0x0380FFFC]`. IntrWait check bits: `[DTCM+0x3FF8]` / `[0x0380FFF8]`.
- IRQ bits: 0 VBlank, 1 HBlank, 2 VCount, 3–6 timers, 7 RTC, 8–11 DMA, 12 keypad, 16 IPC sync, 17 IPC send empty,
  18 IPC recv not empty, 19 cart done, 21 GXFIFO (ARM9), 22 hinge, 23 SPI, 24 Wifi (ARM7).

### Remote control and threading

- Anything that touches a running emulator from another thread goes through `EmulatorHost.Invoke`. The
  host runs the console in `ChunkCycles` chunks under that lock.
- PINE opcode numbers 0x00–0x42 must stay compatible with CodeDesigner's `PineClient`
  (`CodeDesigner.Ps2.Bridge/PineClient.cs`). DS-only opcodes go at 0x60+.
- Document every opcode in `docs/pine-protocol.md` and test it in `TestProject/Remote/`.

### Video

- VRAM: `VramController` keeps one `VramRegion` (a table of 16 KB pages) per destination. Banks may overlap: reads OR them
  together and writes go to all of them. Remap on every VRAMCNT write.
- 2D layer pixels are `ushort` with bit 15 = opaque. Framebuffers hold 6-bit channels (`0x00BBGGRR`); convert with `Gpu2DEngine.ToArgb`.
- POWCNT1 bit 15 = 1 puts engine A on the **top** screen.

## Testing

- Every new instruction gets an execution test (and a disassembly test) in `Px86.NintendoDS.TestProject/Cpu/`.
- Hardware behaviour should be covered by a test ROM: add `tools/testroms/src/<name>9.c` (plus an optional `<name>7.c`), run
  `tools/testroms/build.sh`, and write a test with `TestRomRunner.RunToCompletion("<name>")`. Results go in `RESULT9(n)` / `RESULT7(n)`.
  Finish by writing `DONE_MAGIC` to `RESULT9(0)`.
- Rendering tests compare pixels from `ScreenCapture.Compose(nds)`. Save a PNG and look at it when a test fails.
- Use the skills in `.claude/skills/` for recurring tasks (instructions, I/O registers, debugging games, tests).

## Reference sources

- **GBATEK** (https://problemkaputt.de/gbatek.htm, text version `gbatek.txt`) is the primary spec. Cite its section names in doc comments.
- ARM Architecture Reference Manual (ARMv5TE) and the ARM946E-S / ARM7TDMI TRMs, for CPU edge cases.
- Reference emulators to *learn from* (see `docs/learning-resources.md`): melonDS (accuracy, 3D), NooDS (readable, compact),
  DeSmuME (breadth), SkyEmu, Dust (Rust), CorgiDS. Their sources can be fetched from raw.githubusercontent.com.
