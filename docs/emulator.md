[← Back to Documentation Index](README.md)

# Emulator guide

## Running a game

```bash
dotnet run --project Px86.NintendoDS.Emulator -c Release -- "path/to/My Game.nds"
```

You can also start it without a ROM and use **File → Open ROM** (Ctrl+O), pick one from
**File → Recent**, or drag a `.nds` file onto the window. A self-contained build
(`dotnet publish Px86.NintendoDS.Emulator -c Release -r win-x64 --self-contained`) works the same way, and you can drop
a `.nds` file onto `Px86.NintendoDS.Emulator.exe`.

## Menus and settings

The menu bar sits at the top of the window. Esc hides it, for example in fullscreen, and shows it again.

| Menu | What's there |
|---|---|
| File | Open ROM, Recent, Close ROM, Save/Load state (slots 0–9 with times, or any file), Screenshot, GX capture, Exit |
| Emulation | Pause/Resume, Frame advance, Reset, Speed (normal, turbo, slow motion, unlimited) |
| Config | Settings…, screen layout, swap screens, smooth filtering, fullscreen |
| Tools | Cheats (Ctrl+C), Cheat search, PINE socket and GS frame stream on/off (for CodeDesigner) |
| Help | Controls, About |

**Config → Settings** (Ctrl+,) has these tabs. Settings are saved when the window closes.

| Tab | Settings |
|---|---|
| Paths & boot | BIOS/firmware dumps (Browse…), direct boot or boot through the real BIOS, save folder |
| Video | Layout (vertical, horizontal, single screen), rotation, gap between the screens, swap, integer scaling, smooth filtering, window scale |
| Audio | Sound on/off, volume |
| Input | Rebind each DS button by clicking it and pressing a key |
| Firmware user | Nickname, message, birthday, language, favourite colour (used when no firmware dump is set) |
| Emulation | Turbo speed |
| Remote | PINE and GS stream sockets: on/off, ports, format, JPEG quality |

Use your own legally made dumps of your cartridges. Nothing copyrighted ships with the emulator.

## BIOS and firmware (optional)

By default the emulator **direct-boots** the game with a built-in high-level BIOS, so no dump files are needed.
If you have dumps from your own console, you can point the emulator at them:

```bash
... -- game.nds --bios9 bios9.bin --bios7 bios7.bin --firmware firmware.bin            # BIOS used for SWIs, still direct boot
... -- game.nds --bios9 bios9.bin --bios7 bios7.bin --firmware firmware.bin --real-bios # full boot through the firmware
... --save-settings                                                                     # remember these paths
```

With a firmware dump, your nickname, language and touch-screen calibration come from your console.

## Controls

| DS | Keyboard | Gamepad |
|---|---|---|
| D-pad | Arrow keys | D-pad / left stick |
| A / B / X / Y | X / Z / S / A | East / South / North / West |
| L / R | Q / W | Shoulders |
| Start / Select | Enter / Backspace | Start / Back |
| Touch screen | Left mouse button on the bottom screen | — |
| Fast-forward | Hold Tab | Right trigger |
| Pause / frame advance | P / N | — |
| Reset | Ctrl+R | — |
| Save state / load state | F5 / F8 | — |
| Previous / next state slot | F6 / F7 (slots 0–9, shown in the title bar) | — |
| Fullscreen | F11 | — |
| Screenshot | F12 (saved to Pictures/Px86.NintendoDS) | — |
| GX capture (3D bug report) | F9 (saved to Pictures/Px86.NintendoDS as `.gxcap`) | — |
| Open ROM | Ctrl+O (or drop a file on the window) | — |
| Settings | Ctrl+, | — |
| Hide / show the menu bar | Esc | — |
| Quit | Ctrl+Q | — |

Save states are written next to the ROM as `game.st0` … `game.st9`. A state is the whole
machine, including the cartridge save memory at that moment, so loading an old state also rolls
the in-game save back; the `.sav` file is only rewritten the next time the game saves. States are
tied to the game (its game code) and to this emulator's state format version.

Keyboard bindings can be changed in **Config → Settings → Input**, or in `settings.json`, where `KeyBindings` maps a Silk.NET
key name to a DS button. P and N act as hotkeys only while no DS button is bound to them.

## Saves

The emulator saves to `<game>.sav` next to the ROM (or to `SaveDirectory` in settings). It writes the file about a second
after the game saves, and again on exit. The save chip type (EEPROM/FLASH/FRAM) is detected automatically the first time the
game saves. An existing `.sav` file determines the type from its size, so saves from other emulators that use raw dumps work.

## Settings file

`%APPDATA%\Px86.NintendoDS\settings.json` (Windows) or `~/.config/Px86.NintendoDS/settings.json` (Linux/macOS):

```json
{
  "Bios9Path": null, "Bios7Path": null, "FirmwarePath": null, "UseRealBios": false,
  "Scale": 2, "AudioEnabled": true, "SaveDirectory": null,
  "KeyBindings": { "Up": "Up", "X": "A", "Z": "B", "...": "..." }
}
```

## Cheats

**Tools → Cheats** (Ctrl+C) lists the running game's cheats. Tick a cheat to turn it on. Enabled cheats run once
per frame, as on a real Action Replay. The list is saved per game in the settings folder under
`cheats/<GAMECODE>-<CRC32>.json`.

- **Action Replay DS** codes: one `XXXXXXXX YYYYYYYY` pair per line. The whole opcode set is supported: writes,
  conditionals, pointers, loops, the data register, and block copies (see GBATEK "DS Cart Cheat Action Replay DS").
  `;`, `#` and `//` start comments, and dashes are ignored.
- **Raw** codes: one `ADDRESS VALUE` write per line, re-applied every frame. The number of value digits sets the size:
  2 for a byte, 4 for a halfword, 8 for a word.
- **Import from usrcheat.dat** reads R4/DeSmuME cheat databases, including scrambled ones. It finds the game by game
  code and header CRC, and lets you pick which cheats to add. Imported cheats start switched off.

A code that doesn't parse is switched off, with a message saying why.

**Tools → Cheat search** finds where a value lives in main RAM:
1. Choose the value size and start a search.
2. Play until the value changes, then filter with a comparison: `=`, `!=`, `<` and `>` against a value, or changed,
   unchanged, increased, decreased, or changed by a given amount since the last scan.
3. Repeat until only a few addresses are left.
4. **Make cheat** turns one of them into a raw cheat that holds the value.

## Reporting 3D glitches (GX capture)

If a 3D scene renders wrongly, press **F9** while it's on screen. The emulator records that frame's
geometry commands, plus the textures and 3D settings it uses, into a `.gxcap` file in
Pictures/Px86.NintendoDS. The file holds one frame of 3D data, not the ROM, so it can be shared with
whoever fixes the bug.

`Px86.NintendoDS.Emulator --replay-gx file.gxcap` re-renders it without the game. It writes
`file.png` and `file.txt`, a dump of every polygon and command.

## Headless server (CodeDesigner and other tools)

`Px86.NintendoDS.Emulator game.nds --headless` runs without a window and opens two local sockets:
- **PINE** on 127.0.0.1:28013: memory, registers, breakpoints, stepping, save states, input;
- **GS stream** on 127.0.0.1:28014: live JPEG frames.

Useful options:
- `--paused` starts paused.
- Leaving out the ROM starts an empty server that waits for a client to load one.
- `--pine` / `--gs` open the sockets in the normal window too.

Everything is described in [pine-protocol.md](pine-protocol.md).

## Command line

Run with `--help` to see every option. For debugging, `--headless --frames N --screenshot out.png` runs without a window
and writes both screens to a PNG. `--trace-io` logs I/O registers the emulator doesn't handle yet.
