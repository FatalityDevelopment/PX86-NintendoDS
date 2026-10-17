# Px86-NintendoDS

A Nintendo DS emulator written in C# (.NET 8), with a Silk.NET desktop frontend (SDL window, OpenGL, OpenAL).

- Both DS CPUs: ARM946E-S (ARMv5TE) and ARM7TDMI (ARMv4T) interpreters
- Full memory system, interrupts, timers, DMA, IPC, hardware maths, RTC, SPI devices (firmware, touch screen, power manager)
- Cartridge protocol with save-chip auto-detection (EEPROM/FLASH/FRAM); saves are written to `<game>.sav`
- **No BIOS or firmware dumps required**: games direct-boot with a built-in HLE BIOS (your own dumps are optional)
- 2D graphics engines (all BG modes, sprites, windows, blending), keyboard/gamepad input, mouse as stylus
- 3D graphics and sound are in progress, see [docs/roadmap.md](docs/roadmap.md)

## Quick start

```bash
dotnet build Px86.NintendoDS.sln -c Release
dotnet run --project Px86.NintendoDS.Emulator -c Release -- "path/to/game.nds"
dotnet test Px86.NintendoDS.TestProject
```

Controls, settings and options: [docs/emulator.md](docs/emulator.md). All documentation: [docs/README.md](docs/README.md).

Play only games you own. ROMs, BIOS and firmware files are not included and must never be committed.
