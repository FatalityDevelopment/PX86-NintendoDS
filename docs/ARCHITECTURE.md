[← Back to Documentation Index](README.md)

# Architecture

## Projects

```
 Px86.NintendoDS.Emulator (exe, Silk.NET)
     │  window thread: SDL window, OpenGL texture, input, OpenAL
     │  emulation thread: GameSession → NdsEmulator.RunFrame() at 59.83 Hz
     ▼
 Px86.NintendoDS.EmulatorCore ──► Px86.NintendoDS.Common (ArmCpu, Cp15, disassembler)
     │                          └► Px86.NintendoDS.FileFormats (NdsRom, Crc16, PngWriter)
     ▼
 NdsEmulator ── owns every component below
```

## Inside `NdsEmulator`

```
            ┌──────────────── Scheduler (ARM9 cycles) ────────────────┐
            │  LcdHBlank/LineEnd · Timer9_n/Timer7_n · CartridgeTransfer │
            └───────────────────────────────────────────────────────────┘
  ArmCpu (ARMv5TE) + Cp15                    ArmCpu (ARMv4T)
        │ IArmMemoryBus                            │ IArmMemoryBus
  Arm9MemoryBus                              Arm7MemoryBus
   ├ ITCM/DTCM                                ├ BIOS7, ARM7 WRAM, Wifi stub
   ├ main RAM ───────────── shared ──────────┤ main RAM
   ├ shared WRAM (WRAMCNT) ──── shared ───────┤ shared WRAM
   ├ palette / OAM / VRAM (VramController) ──┤ VRAM C/D as WRAM
   └ I/O ─┐                                   └ I/O ─┐
          ├ InterruptController (×2) · TimerUnit (×2) · DmaController (×2)
          ├ IpcController (both sides) · Keypad · SystemControl
          ├ MathUnit (ARM9) · Gpu2DEngine A/B · Gpu3DEngine · LcdController
          ├ CartridgeSlot (+ SaveMemory, Key1Decryptor)
          └ SpiBus (FirmwareFlash, TouchScreen, PowerManager) · RealTimeClock · SoundUnit (ARM7)
```

## Main loop

`RunFrame` repeats `RunSlice` until the LCD reaches line 192 (VBlank):

1. `target = min(next event, now + 64)`
2. The ARM9 steps until its local time reaches `target`. A halted CPU skips ahead.
3. The ARM7 does the same; each ARM7 cycle counts as 2.
4. `Scheduler.AdvanceTo(target)` fires the events that are due: scanline rendering at HBlank, VCOUNT/VBlank IRQs and DMA,
   timer overflows, cartridge data words.

While a CPU runs, `Scheduler.CurrentTime` holds its exact position, so timers read correct counters and devices can schedule
work relative to "now". DMA transfers run to completion when they trigger, and their cost is charged to the CPU that owns them.

## Boot

- **Direct boot** (`DirectBoot.Apply`): copies the binaries and header, writes the boot info and firmware user settings to RAM,
  sets CP15/WRAMCNT/POWCNT and the stacks, and puts the cartridge in main-data mode.
- **HLE BIOS** (`Bios/`): `BiosStub` builds tiny BIOS images (vectors + IRQ dispatcher). `HleBios` implements the SWIs in C#,
  including decompression that reads through guest callbacks (`CallGuest`).
- **Real BIOS** (optional): the user's dumps replace the stubs, HLE is off, and the CPUs start at the reset vectors.
  The cartridge serves KEY1 commands through `Key1Decryptor`.

## Video

- `LcdController` drives timing. At each visible HBlank, `NdsEmulator` asks both `Gpu2DEngine`s to render that line.
- `Gpu2DEngine` renders BG/OBJ layers into RGB555, composes them with windows and blending, applies master brightness, and writes
  6-bit-per-channel pixels to its `Framebuffer`. POWCNT1 bit 15 decides which engine is shown on the top screen.
- `VramController` maps the nine banks into `VramRegion` page tables for CPU access and for the engines (BG, OBJ,
  extended palettes, textures).

## Frontend threading

The emulation thread owns `NdsEmulator`. The window thread only touches the shared frame buffer (under a lock) and the
`InputMapper` state, which the emulation thread applies at the start of each frame.
