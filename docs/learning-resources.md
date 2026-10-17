[← Back to Documentation Index](README.md)

# Learning resources

## Specification
- **GBATEK** by Martin Korth: https://problemkaputt.de/gbatek.htm (text version: `gbatek.txt`). This is the reference for every
  DS register, protocol and quirk. Our doc comments cite its section names.
- **ARM Architecture Reference Manual** (ARMv5TE) and the **ARM946E-S** / **ARM7TDMI** technical reference manuals, for
  CPU edge cases.

## Emulators to study
These are the emulators listed on the emulation wiki page the project started from
(https://emulation.gametechwiki.com/index.php/Nintendo_DS_emulators), and what each one is useful for when building Px86:

| Emulator | Language / license | Useful for |
|---|---|---|
| **melonDS** | C++ / GPLv3 | The accuracy benchmark: 3D rasterizer details, GPU timing, wifi, DSi; very active |
| **DeSmuME** | C++ / GPLv2 | Long-standing broad compatibility, debugging tools, game-specific findings |
| **NooDS** | C++ / GPLv3 | Small, readable code for every subsystem; good for seeing a whole design at once |
| **SkyEmu** | C / MIT | Compact single-file C implementation of GBA/NDS |
| **Dust** | Rust / GPLv3 | Modern architecture, precise timing research |
| **CorgiDS** | C++ / GPLv3 | Early, small and readable; historical interest |
| **no$gba** | Closed source | Written by GBATEK's author; best-in-class debugger |
| **DraStic** | Closed source | Fast Android emulator (dynarec); performance reference |

Sources can be read from `raw.githubusercontent.com`. **Rule:** GPL code must not be copied into this project (code, tables
or comments). Use those emulators to understand behaviour, confirm it in GBATEK, and write it fresh.

## Homebrew and testing
- Our own test ROMs: `tools/testroms` (clang, freestanding C, results mailbox in RAM).
- devkitPro / libnds and its `nds-examples` repository are useful homebrew to try once you have devkitARM installed locally.
