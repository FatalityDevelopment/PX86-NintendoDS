---
name: nds-run-tests
description: Build, test and run Px86.NintendoDS — dotnet commands, the clang test-ROM toolchain, headless screenshots, running the real window under xvfb, and pointing tests at local ROMs. Use whenever you need to verify a change.
---

# Build, test and run

```bash
export PATH=$HOME/.dotnet:$PATH            # cloud sessions (the SessionStart hook installs the SDK there)
dotnet build Px86.NintendoDS.sln -c Release
dotnet test Px86.NintendoDS.TestProject                       # all tests
dotnet test Px86.NintendoDS.TestProject --filter TestThumb    # one class
```

## Test ROMs (our own homebrew)
- Sources: `tools/testroms/src/<name>9.c` (ARM9) + optional `<name>7.c` (ARM7). Register macros are in `tools/testroms/nds.h`.
- Build: `tools/testroms/build.sh` → `Px86.NintendoDS.TestProject/TestRoms/<name>.nds` (needs clang, ld.lld, llvm-objcopy, python3).
- Convention: store observations in `RESULT9(n)` / `RESULT7(n)`, then write `DONE_MAGIC` to index 0.
- C# side: `TestRomRunner.RunToCompletion("<name>", waitForArm7: true)` and assert with `TestRomRunner.Result9(nds, n)`.
- Commit the rebuilt `.nds` together with its source (it is our code; `.gitignore` allows only this folder).

## Headless runs and screenshots
```bash
dotnet run --project Px86.NintendoDS.Emulator -c Release -- some.nds --headless --frames 600 --screenshot /tmp/out.png
```
Read the PNG to check rendering (top screen above, bottom below). The output also reports the speed (% of 59.83 fps).

## The real window without a display
```bash
xvfb-run -s "-screen 0 800x900x24" bash -c 'timeout 8 dotnet run --project Px86.NintendoDS.Emulator -c Release --no-build -- some.nds & sleep 6; import -window root /tmp/screen.png; wait'
```
(The ALSA "cannot find card" messages are expected in containers; the game just runs silent.)

## Commercial ROMs
They are never in the repo. To check a game the user owns, ask for the path on their machine, or ask them to run the
headless command above and share the screenshot and log.
