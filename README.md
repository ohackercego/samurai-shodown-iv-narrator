# Samurai Shodown IV Accessibility Narrator

A screen reader narrator for **SAMURAI SHODOWN IV (GOG / Neo Geo port by Code Mystics)**.
It reads the game's own memory to announce menus, options, controls and the character
select screen through NVDA, so a blind player can navigate the game without sight.

The narrator is a separate, read-only process. It never injects code into the game,
never writes to game memory, and never modifies game files.

---

## Requirements

| Item | Details |
|---|---|
| Game | SAMURAI SHODOWN IV, GOG release (`NeoGeo.exe`) - **not included**, buy it on GOG |
| OS | Windows 10 or 11, 64-bit |
| Screen reader | NVDA (recommended). SAPI and other Prism backends also work |
| Runtime | .NET Framework 4.x (already present on Windows 10/11) |
| Build only | .NET Framework `csc.exe` (ships with Windows) |

---

## Installation

The narrator must live in a `mod` folder **inside the game directory**:

```
C:\Program Files\GOG Galaxy\Games\SAMURAI SHODOWN IV\
    NeoGeo.exe
    Data\
    Options\
    mod\                      <-- extract the release zip here
        Ss4Narrator.exe
        Ss4Narrator.cs
        prism.dll
        nvdaControllerClient.dll
        SpeechCore.dll
        SRAL.dll
        tolk.dll
        UniversalSpeech.dll
        prism\
        ssvs_path.cfg
```

Steps:

1. Download the release zip and extract its contents into a new `mod` folder inside
   the game directory (as shown above).
2. Start NVDA.
3. Run `Ss4Narrator.exe`. The narrator waits for the game.
4. Start the game normally.

The narrator connects to `NeoGeo.exe` automatically and reconnects every time the
game is restarted - you can leave it running.

`ssvs_path.cfg` holds the game folder path. If the game is installed somewhere else,
edit that single line. Without the file, the narrator asks you to locate `NeoGeo.exe`
the first time.

---

## What is announced

### Main menus

Every front-end screen is read item by item, with the menu name, the selected entry
and the position:

```
Main Menu, PLAY, 1 of 3
Local Play, ONE PLAYER, 1 of 2
Pause Menu, CONTINUE, 1 of 3
Confirmation, YES, 1 of 2
```

### Options and settings

All four settings screens are read with the **current value** of each row, taken from
the game's own options table, so the spoken value is the value on screen:

```
Game Settings, Difficulty Level: 4, 5 of 9
Display Settings, VSync: On, 5 of 7
Display Settings, Scanlines: 50%, 1 of 7
Volume Settings, Game Volume: 50%, 1 of 3
System Settings, Language: Portuguese, 3 of 4. Languages: Japanese, English, Spanish, Portuguese
```

On/off rows are read using the **actual choice text of that row**, never a fixed
"on/off" label. This matters: in this game some rows list their choices as
`on, off` and others as `off, on`, and two rows use `no, yes` / `yes, no`. The
narrator resolves the choice through the same table the game uses, so
`VSync: On`, `Fullscreen: No` and `Smoothing: Off` are all correct.

### Controls

The controls screen announces the key or button mapped to each action, for both
players, for example:

```
Controls, Player 1, Up: arrow up, 2 of 16
Controls, Player 1, Button A: 7, 7 of 16
```

Press **F7** on any screen to have the whole mapping recited at once (both players).

### Character select

The character under the cursor is announced by name as you move. In two-player mode
each announcement says which player it belongs to:

```
Nakoruru
Player 1, Nakoruru
Player 2, Galford
```

After confirming you also hear the chosen **style** and **level**:

```
Player 1, Shura
Player 2, Rasetsu
Player 1, Medium
Player 2, Advanced
```

The styles are the two the game has: **Shura** (Slash) and **Rasetsu** (Bust).
The levels are **Beginner**, **Medium** and **Advanced**, chosen per player.

Amakusa is announced as `Amakusa, secret` and the random slot as `random`.

### Language

The narrator follows the game's own language setting. Change the language in
System Settings and the narrator switches to it and announces the change.

---

## Keyboard shortcuts

| Key | Action |
|---|---|
| `F7` | Recite the full controls mapping (both players) |
| `F8` | Repeat the current announcement |
| `F9` | Announce the current menu name |
| `F10` | Mute / unmute |
| `F11` / `F12` | Exit the narrator |

---

## How it works

The narrator reads `NeoGeo.exe` memory with `ReadProcessMemory` under
`PROCESS_VM_READ`. Nothing is written to the game.

The port keeps a fixed image base (`0x400000`, no ASLR), so every address is a
constant. Three kinds of data are read:

1. **Front-end menus** - a global `MenuManager` pointer, the active menu object,
   its label array and selected index. Menu names come from a table of 34 RTTI
   vtables measured in this build.
2. **Options** - the game stores every setting as two-byte pairs `<id><digit>` in
   three chained strings (unsaved, user, factory default). The narrator mirrors the
   game's own getter to read the live value, and its own choice-text function to
   render it.
3. **Game state** - the emulated 68k work RAM (64 KB) reached through the Neo Geo
   bank table. Character, style and level selection are read from there; the
   character byte is the game's internal character id, taken from the `CURSOR-1`
   object found by walking the engine's object linked list.

Text to speech goes through Prism, which uses NVDA's controller client. Static UI
strings come from the game's own `Data\strings.txt`.

---

## Building from source

```powershell
cd mod
powershell -ExecutionPolicy Bypass -File build.ps1
```

`build.ps1` compiles `Ss4Narrator.cs` with the .NET Framework x64 `csc.exe` and
prints `built : ...\Ss4Narrator.exe (x64, Prism speech backend)`. There are no
NuGet packages and no project file.

All native dependencies are already in this repository (`prism.dll`,
`nvdaControllerClient.dll`, `SpeechCore.dll`, `SRAL.dll`, `tolk.dll`,
`UniversalSpeech.dll`, and the Prism runtime under `prism\`). Nothing is downloaded
during the build.

If the executable is locked because the narrator is running, close it first -
`csc` cannot overwrite a running process image.

---

## Troubleshooting

**Nothing is spoken.** Check that NVDA is running before starting the narrator. The
log in the `mod` folder prints `NVDA running: YES` and `Prism backend: NVDA` when the
speech chain is up.

**The narrator starts but stays silent on menus.** The game must be running. The log
shows `Waiting for NeoGeo.exe to start...` until it connects, then
`Neo Geo RAM Base discovered`.

**A setting is announced but the value is missing.** Values are only spoken when the
narrator can resolve them through the game's own tables. It never guesses: a row with
no resolvable value is announced by name only. The Resolution row is deliberately
silent because its value does not live in the options table.

**Style or level is not announced.** Both are only valid once the choice is
confirmed, and only during the VS and match-intro screens. Reading them earlier
returns garbage, so the narrator waits for a stable value.

**The log.** `mod\log.txt` holds a line per announcement and diagnostic lines for
each reading. Run with `-Verbose` for the detailed diagnostics.

---

## Known limitations

- **No in-match narration.** Health bars, rage meter, round number, timer and match
  result are **not** announced. This is not implemented in any narrator for this
  engine, including the Samurai Shodown V Special one this project started from.
  Everything is in place to add it - the emulated RAM is already located - but the
  offsets for those values have not been measured yet.
- **Style is announced on confirmation, not while moving.** During navigation the
  game does not store the style anywhere readable; the byte only becomes valid when
  the choice is confirmed.
- **Some popups announce position without text.** Confirmation boxes created with
  custom keys (for example save-slot prompts) fall back to `Confirmation, 1 of 2`
  rather than risk reading the wrong label.
- **The stage name is not announced.** The offsets inherited from the V Special
  narrator do not hold stage data in this build - the byte oscillates every frame -
  so it is disabled rather than guess.

---

## Credits and licensing

The narrator reads memory only. It contains no game code and no game assets.

Speech is provided by [Prism](https://github.com/dgilland/prism) (MIT). The NVDA
controller client is redistributed from NV Access under its own licence. Tolk and
UniversalSpeech are redistributed under their respective licences.

SAMURAI SHODOWN IV is a trademark of SNK. This project is unofficial and is not
affiliated with or endorsed by SNK, Code Mystics or GOG.
