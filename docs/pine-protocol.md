[← Back to Documentation Index](README.md)

# Remote control: PINE and GS stream sockets

Px86.NintendoDS can be driven by other programs (CodeDesigner, debuggers, cheat tools, test scripts)
over two local TCP sockets. They follow the same protocols CodeDesigner already uses with PCSX2:

| Socket | Default port | Direction | Purpose |
|---|---|---|---|
| PINE | `127.0.0.1:28013` | request → reply | memory, registers, execution control, breakpoints, watchpoints, input, save states |
| GS stream | `127.0.0.1:28014` | server → client | every finished frame (both screens) as JPEG / PNG / raw RGBA |

Both listen on the loopback interface only, on every OS. PCSX2 uses a Unix socket for PINE on Linux and
macOS; we use TCP everywhere, so a client simply connects to the port.

## Starting the emulator for remote use

```bash
# Headless server: no window, PINE + GS on, runs until Ctrl+C or the Shutdown opcode.
Px86.NintendoDS.Emulator game.nds --headless
Px86.NintendoDS.Emulator game.nds --headless --paused          # set breakpoints first, then Resume
Px86.NintendoDS.Emulator --headless                            # no ROM: send LoadRom (0x65) to start one
Px86.NintendoDS.Emulator game.nds --headless --pine 28100 --gs 28101 --gs-codec png

# Normal window with the sockets open (opt-in; or set PineEnabled/GsEnabled in settings.json).
Px86.NintendoDS.Emulator game.nds --pine --gs
```

`--headless --frames N [--screenshot out.png]` is the old batch mode (run N frames, print the speed,
exit). It doesn't open sockets.

To embed the emulator in-process instead, use the classes directly. `EmulatorHost`
(`Px86.NintendoDS.EmulatorCore.Hosting`) runs the console on its own thread. `PineSocket` and
`GsStreamSocket` (`Px86.NintendoDS.EmulatorCore.Remote`) take a host.

## PINE wire format

All values are little-endian.

```
Request:  [total_size : u32][opcode : u8][args…]   ([opcode][args…] may repeat)
Reply:    [total_size : u32][status : u8][data…]   status 00h = OK, FFh = FAIL
```

- `total_size` includes itself.
- Several commands packed into one request run in order and atomically, and their reply data is
  concatenated.
- If any command fails, the whole reply is `05 00 00 00 FF`.
- Requests can be up to 16 MiB.

### Target CPU

Memory, register, PC, step and breakpoint opcodes act on the connection's **selected CPU**. That is the
ARM9 until `SelectCpu` (0x60) picks the ARM7, and the choice lasts for the connection.

Opcodes that carry their own CPU byte (watchpoints, GetRegister/SetRegister, Disassemble) use
0 = ARM9 and 1 = ARM7.

Memory accesses see what that CPU sees: the ARM9 view includes the ITCM and DTCM. Writes have their
normal I/O side effects. Reads skip IPCFIFORECV (04100000h) and the cartridge data port (04100010h),
which would consume data, and return 0 there instead.

### Upstream PINE (0x00–0x0F)

| Op | Name | Args | Reply data |
|---|---|---|---|
| 00 | Read8 | addr u32 | u8 |
| 01 | Read16 | addr u32 | u16 |
| 02 | Read32 | addr u32 | u32 |
| 03 | Read64 | addr u32 | u64 (two 32-bit reads) |
| 04 | Write8 | addr u32, u8 | — |
| 05 | Write16 | addr u32, u16 | — |
| 06 | Write32 | addr u32, u32 | — |
| 07 | Write64 | addr u32, u64 | — |
| 08 | Version | — | string |
| 09 | SaveState | slot u8 (0–9) | — |
| 0A | LoadState | slot u8 | — (FAIL if the slot is empty) |
| 0B | Title | — | string (header title) |
| 0C | ID | — | string (game code, e.g. `CPUE`) |
| 0D | UUID | — | string `CODE-HEADERCRC-SECURECRC` |
| 0E | GameVersion | — | string (ROM version byte) |
| 0F | Status | — | u32: 0 running, 1 paused, 2 no game |

A string is `[length u32 including the NUL][UTF-8 bytes][00]`.

### CodeDesigner debugger extension (0x10–0x42)

These use the same numbers and layouts as `CodeDesigner.Ps2.Bridge.PineClient`. Opcodes that make no
sense on a DS (threads, IOP modules, symbols, locals, network) always FAIL.

| Op | Name | Args | Reply data |
|---|---|---|---|
| 10 | GetProgramCounter | — | pc u32, paused u8 |
| 11 | Pause | — | — |
| 12 | Resume | — | — (a core sitting on a breakpoint runs that instruction first) |
| 13 | Step | — | new pc u32 (only while paused) |
| 14 | SetBreakpoint | addr u32 | — |
| 15 | ClearBreakpoint | addr u32 | — (FAIL if there was none) |
| 16 | ClearAllBreakpoints | — | — (both CPUs) |
| 17 | GetRegisters | — | r0–r14, pc, CPSR, SPSR (18 × u32) |
| 18 | GetRegister | cpu u8, category u8, num u8 | u128 (value in the low 32 bits) |
| 19 | SetRegister | cpu u8, category u8, num u8, u128 | — |
| 1E | StepInto | — | — |
| 1F | StepOver | — | — (a BL/BLX/SWI runs to the next instruction) |
| 20 | StepOut | — | — (runs to LR) |
| 22 | SaveStateFile | len u16, UTF-8 path | — |
| 23 | LoadStateFile | len u16, UTF-8 path | — |
| 24 | Reset | — | — (reboots the game, keeps breakpoints) |
| 25 | FrameAdvance | frames u8 | — (runs that many frames, then pauses) |
| 26 | GetFPS | — | f32 (emulated frames per second) |
| 27 | SetLimiterMode | mode u8 | — (0 normal, 1 turbo ×2, 2 slow-mo ×0.5, 3 unlimited) |
| 28 | ListBreakpoints | cpu u8 (FFh = all) | count u32, then {addr u32, enabled u8, cpu u8} |
| 29 | Disassemble | cpu u8, addr u32, count u16 | count u16, then {addr u32, len u8, text} |
| 2E | AddWatch | cpu u8, start u32, end u32, cond u8 | — (cond 1 read, 2 write, 3 both; end exclusive) |
| 2F | RemoveWatch | cpu u8, start u32, end u32 | — |
| 30 | ListWatches | cpu u8 (FFh = all) | count u32, then {start u32, end u32, cond u8, action u8, cpu u8 (1 ARM9, 2 ARM7)} |
| 31 | ClearAllWatches | — | — |
| 32 | SaveScreenshot | — | — (PNG in Pictures/Px86.NintendoDS) |
| 33 | PadGetState | pad u8 | buttons u32, lx ly rx ry u8, 16 × pressure u8 |
| 34 | PadSetButton | pad u8, index u8, value f32 | — |
| 35 | PadSetAnalog | pad u8, lx ly rx ry u8 | — (ignored: the DS has no sticks) |
| 36 | PadSetState | pad u8, buttons u32, lx ly rx ry, 16 × pressure | — |
| 37 | PadGetType | pad u8 | u8 (1 = connected for pad 0) |
| 40 | GSStreamGetPort | — | port u16 (0 = GS stream off) |
| 41 | GSStreamGetStatus | — | active u8, port u16, subscribers u32, delivered u32, dropped u32 |
| 42 | GSStreamPing | — | — |

Notes on specific opcodes:
- **Disassemble:** the cpu byte's low nibble selects the CPU. 10h forces ARM and 20h forces Thumb;
  otherwise the CPU's current state decides.
- **GetRegister/SetRegister, category 0:** num 0–15 are r0–r15 (r15 = next instruction), 16 is CPSR and
  17 is SPSR.
- **GetRegister/SetRegister, category 1 (ARM9 CP15):** num 0 is control, 1 is the DTCM region, 2 is the
  ITCM region and 3 is the main ID (read-only).
- **Pad buttons:** they are bit numbers, from bit 0 to bit 12: A, B, Select, Start, Right, Left, Up, Down,
  R, L, X, Y, Debug. Remote buttons are OR'ed with the keyboard/gamepad.

### Nintendo DS extension (0x60+)

| Op | Name | Args | Reply data |
|---|---|---|---|
| 60 | SelectCpu | cpu u8 | — |
| 61 | ReadBuffer | addr u32, len u32 (≤ 16 MiB) | len bytes |
| 62 | WriteBuffer | addr u32, len u32, bytes | — (aligned words/halfwords are written wide, so VRAM/palette/OAM work) |
| 63 | SetTouch | down u8, x u16 (0–255), y u16 (0–191) | — |
| 64 | GetFrameCount | — | u64 |
| 65 | LoadRom | len u16, UTF-8 path | — (starts it with the configured BIOS/firmware) |
| 66 | GetBreakInfo | — | valid u8, cpu u8, reason u8 (0 breakpoint, 1 watchpoint), pc u32, addr u32, write u8 |
| 67 | Shutdown | — | — (headless server exits, window closes) |

## GS stream format

Clients connect and receive frames. Nothing is sent to the server. Each frame looks like this:

```
u32 size       bytes after this field (11 + payload)
u8  type       1 = frame
u32 frame_idx  emulated frame number
u16 width      256
u16 height     384   (top screen above the bottom screen)
u8  codec      1 = JPEG (one complete image per frame), 2 = PNG, 3 = raw RGBA (4 bytes/pixel)
u8  flags      bit 0 = Nintendo DS dual-screen layout
bytes payload
```

- This is the frame format of CodeDesigner's `GSStreamForwarder` and its GS viewer webview, so the
  existing viewer shows the DS screens unchanged.
- New subscribers get the current picture straight away, even while paused.
- Frames are capped at 60 per second. When encoding falls behind, older frames are skipped and counted
  as dropped.
- A client that can't accept a frame within 250 ms is disconnected.

## Hooking it into CodeDesigner

CodeDesigner's Bridge already speaks both protocols. A DS client needs four things:
- the PINE port (28013 instead of PCSX2's 28011, over TCP);
- `SelectCpu` before using ARM7 addresses;
- `GetRegisters` read as 18 ARM words instead of the MIPS layout;
- `GSStreamGetPort`, which works exactly as it does with PCSX2.
