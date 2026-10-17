[← Back to Documentation Index](README.md)

# Roadmap and status

| Area | Status | Notes |
|---|---|---|
| ARM9/ARM7 interpreter | ✅ Done | All ARMv4T/ARMv5TE instructions; no JIT yet |
| CP15 / TCM | ✅ Done | Protection unit registers stored, not enforced; caches not modelled |
| Memory buses, WRAM/VRAM banking | ✅ Done | |
| IRQ, timers, DMA, IPC, maths, keypad | ✅ Done | DMA runs to completion when triggered |
| SPI (firmware, touch, power), RTC | ✅ Done | Default firmware generated when no dump is given |
| Cartridge protocol + saves | ✅ Done | Save type auto-detect; KEY2 not simulated (not needed) |
| Direct boot + HLE BIOS | ✅ Done | All common SWIs including callback decompression |
| Real BIOS/firmware boot | 🧪 Untested | Implemented (KEY1); needs user dumps to verify |
| 2D engines | ✅ Done | Display capture and OBJ mosaic still missing |
| Frontend (Silk.NET) | ✅ Done | Window, input, touch, fast-forward, screenshots, settings |
| 3D engine | 🚧 Next | Geometry engine + software rasterizer |
| Sound (SPU) | 🚧 Next | 16 channels, PCM/ADPCM/PSG, capture |
| Save states | ⏳ Planned | |
| Performance work | ⏳ Planned | Interpreter fast paths, memory page tables, maybe a JIT |
| Wifi / microphone | ⏳ Later | Stubbed |

## Compatibility

Record games you've tried here (game code, version, what works, what doesn't, and the commit you tested).

| Game | Code | Status | Notes |
|---|---|---|---|
| *(none recorded yet)* | | | |
