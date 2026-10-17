[← Back to Documentation Index](README.md)

# Roadmap and status

| Area | Status | Notes |
|---|---|---|
| ARM9/ARM7 interpreter + JIT | ✅ Done | All ARMv4T/ARMv5TE instructions; IL JIT for hot blocks (~2x) |
| CP15 / TCM | ✅ Done | Protection unit registers stored, not enforced; caches not modelled |
| Memory buses, WRAM/VRAM banking | ✅ Done | |
| IRQ, timers, DMA, IPC, maths, keypad | ✅ Done | DMA runs to completion when triggered |
| SPI (firmware, touch, power), RTC | ✅ Done | Default firmware generated when no dump is given |
| Cartridge protocol + saves | ✅ Done | Save type auto-detect; KEY2 not simulated (not needed) |
| Direct boot + HLE BIOS | ✅ Done | All common SWIs including callback decompression |
| Real BIOS/firmware boot | 🧪 Untested | Implemented (KEY1); needs user dumps to verify |
| 2D engines | ✅ Done | Includes display capture; OBJ mosaic still missing |
| Frontend (Silk.NET) | ✅ Done | Window, input, touch, fast-forward, screenshots, save-state hotkeys, settings; no file drop yet |
| 3D engine | ✅ Done | Geometry engine, clipping, lighting, all texture formats, software rasterizer; no anti-aliasing or 1-dot depth culling |
| Sound (SPU) | ✅ Done | 16 channels, PCM/ADPCM/PSG/noise, capture, OpenAL output |
| Save states | ✅ Done | 10 slots per game (F5/F8); determinism covered by tests |
| Performance work | 🔄 Ongoing | JIT done; next: register caching in JIT blocks, memory page tables |
| Wifi / microphone | ⏳ Later | Stubbed |

## Compatibility

Record games you've tried here (game code, version, what works, what doesn't, and the commit you tested).

| Game | Code | Status | Notes |
|---|---|---|---|
| *(none recorded yet)* | | | |
