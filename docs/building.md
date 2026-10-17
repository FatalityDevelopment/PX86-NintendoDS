[← Back to Documentation Index](README.md)

# Building

## Prerequisites
- .NET 8 SDK
- Windows, Linux or macOS. The frontend ships its native SDL2 and OpenAL Soft libraries through NuGet.
- Optional, for test ROMs: `clang`, `ld.lld`, `llvm-objcopy`, `python3` (no devkitPro needed)

## Commands

```bash
dotnet build Px86.NintendoDS.sln -c Release
dotnet test Px86.NintendoDS.TestProject
dotnet run --project Px86.NintendoDS.Emulator -c Release -- game.nds
tools/testroms/build.sh
```

## Projects

| Project | Kind | Purpose |
|---|---|---|
| `Px86.NintendoDS.Common` | Library | ARM CPU interpreter, CP15, disassemblers |
| `Px86.NintendoDS.FileFormats` | Library | `.nds` header/NitroFS, CRC16, PNG |
| `Px86.NintendoDS.EmulatorCore` | Library | The DS hardware, boot, sessions |
| `Px86.NintendoDS.Emulator` | Exe | Silk.NET desktop frontend and headless runner |
| `Px86.NintendoDS.TestProject` | MSTest | Unit tests and test-ROM integration tests |

## Test ROMs

`tools/testroms` builds tiny homebrew programs from C with clang (freestanding, no libnds). Each test has an ARM9 source
`src/<name>9.c` and an optional ARM7 source `src/<name>7.c`. `build.sh` links them at 02000000h / 02380000h and packs them with
`mkrom.py` into `Px86.NintendoDS.TestProject/TestRoms/<name>.nds`. The unit tests boot them and read results from main RAM.

## CI

`.github/workflows/build.yml` builds and tests on Windows and Ubuntu. It rejects committed `bin/`/`obj/` folders and publishes
the frontend as an artifact.

## Claude Code

`CLAUDE.md` holds the project rules and conventions. `.claude/skills/` has task playbooks: instructions, I/O registers,
debugging games, hardware references and tests. `.claude/agents/nds-accuracy-reviewer.md` reviews changes against GBATEK.
The SessionStart hook installs the .NET SDK in cloud sessions.
