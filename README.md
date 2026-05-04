# SledCoopMod

Same-machine split-screen local co-op mod for **Sledding Game**.

The mod is designed to coexist with the unmodified game: when it's installed
but you want to play vanilla, flip a single config flag (or remove the DLL)
and the game runs as if the mod were never there.

This mod is functional but no where close to bug free. Here are some notable bugs:
  -Guest data doesnt load in and always copies the player 1
  -Leftover UI text on screen that needs removed (trust me I tried)
  -In game mod config menu (working on it)
  -Player tab menu
  -Shutdown of game sometimes will freeze. It seems to do this less if you quit all of the other splitscreen instances before quitting the original.
  -Racing works but the racing scoreboard is wrong

---

## Features

- **Up to 4 local players** on one PC, each in its own game window.
- **Configurable split-screen layouts**:
  - 2-player: `Vertical` (left/right) or `Horizontal` (top/bottom).
  - 3-player: `AsymmetricTop` (large top + two bottom) or `AsymmetricLeft`.
  - 4-player: 2×2 grid.
- **Optional multi-monitor mode** — child windows can be moved to separate
  displays when more than one monitor is available.
- **Mid-session join** — press Start / Options on an unassigned controller to
  join during an active match (toggle in config).

---

## Installation

You need the game installed via Steam first.

### Windows (no Python required)

PowerShell ships with Windows, so the installer is a `.ps1` script wrapped
in a `.bat` file you can double-click.

1. Drop these three files together in any folder:
   - `install_sledcoopmod.bat`
   - `install_sledcoopmod.ps1`
   - `SledCoopMod.dll`
2. Double-click `install_sledcoopmod.bat`.
3. Confirm the detected game folder (or paste the path to it) and let the
   installer run.

The installer will:

- Auto-detect your Sledding Game install via the Steam registry +
  `libraryfolders.vdf`.
- Download the latest BepInEx 6 IL2CPP build for Unity 6 and extract it
  into the game folder.
- Pre-seed `BepInEx/config/BepInEx.cfg` so the BepInEx **console window is
  disabled by default** (the on-disk log at `BepInEx/LogOutput.log` stays
  enabled for diagnostics).
- Copy `SledCoopMod.dll` into `BepInEx/plugins/`.

Optional flags: `-GamePath '<path>'`, `-NonInteractive`, `-SkipBepInEx`,
`-SkipMod`, `-SkipConsoleDisable`. Run from PowerShell with
`powershell -ExecutionPolicy Bypass -File .\install_sledcoopmod.ps1 -NonInteractive`
for an unattended install.

### Linux + Steam Proton

Linux uses the Python tkinter installer (`install_sledcoopmod.py`).

```bash
sudo apt install python3-tk          # one-time dependency
python3 install_sledcoopmod.py
```

The Linux installer does the same things as the Windows one make sure to set `WINEDLLOVERRIDES="winhttp=n,b" %command%` in the game launch options in steam or the mod wont run

After install, launch Sledding Game from Steam normally. BepInEx generates
its IL2CPP interop assemblies on first run, then the mod loads.

---

## Disabling the mod for vanilla gameplay

You don't have to uninstall to play vanilla. Pick whichever fits:

**Easiest — config toggle.** Edit
`<game>/BepInEx/config/com.sledcorp.sledcoopmod.cfg` and set:

```ini
[General]
Enabled = false
```

Restart the game. SledCoopMod will load, log a warning, and skip applying
any patches — the game runs vanilla.


**Full removal.** Delete `<game>/BepInEx/plugins/SledCoopMod.dll`. To
remove BepInEx itself, delete the `BepInEx/` folder and `winhttp.dll` at
the game root, and (Linux only) clear the
`WINEDLLOVERRIDES="winhttp=n,b" %command%` Steam launch option.

---

## Configuration reference

All settings live in
`<game>/BepInEx/config/com.sledcorp.sledcoopmod.cfg`, generated on first
launch.

| Section / key | Default | What it does |
| --- | --- | --- |
| `General.Enabled` | `true` | Master on/off. `false` = mod loaded but inert. |
| `LocalCoop.LocalCoopEnabled` | `true` | Enable same-machine networked co-op. |
| `LocalCoop.MaxLocalPlayers` | `4` | Hard cap on local players (1–4). |
| `LocalCoop.MidSessionJoinEnabled` | `true` | Allow controllers to join an active match. |
| `SplitScreen.TwoPlayerSplitOrientation` | `Vertical` | `Vertical` / `Horizontal`. |
| `SplitScreen.ThreePlayerLayout` | `AsymmetricTop` | `AsymmetricTop` / `AsymmetricLeft`. |
| `MultiDisplay.MultiDisplayEnabled` | `false` | Move child windows to other monitors when available. |
| `Online.OnlineHybridEnabled` | `false` | EXPERIMENTAL: multiple local players on one online connection. |
| `NetworkedInstances.Enabled` | `true` | Use real FishNet client processes for extra players. |
| `NetworkedInstances.LaunchChildProcesses` | `true` | Host auto-launches child game processes for joined slots. |
| `NetworkedInstances.Port` | `7770` | Loopback Tugboat port. |
| `Debug.VerboseLogging` | `false` | Spawn / input / camera diagnostic logs. |

---

## Troubleshooting

**Nothing happens on launch.**
Check `<game>/BepInEx/LogOutput.log`. If the file doesn't exist, BepInEx
itself didn't load:
- Windows: confirm `winhttp.dll` is at the game root next to
  `Sledding Game.exe`.
- Linux: confirm the Steam launch option contains
  `WINEDLLOVERRIDES="winhttp=n,b" %command%`.

**The BepInEx console window is back.**
The installer disables it by default. To re-enable for live debugging, edit
`<game>/BepInEx/config/BepInEx.cfg` and set
`[Logging.Console] Enabled = true`.

**Child windows don't appear.**
- Make sure the host process actually started (the host log will show
  `Networked host requested`).
- Verify nothing else is bound to port `7770` (or change
  `NetworkedInstances.Port`).
- Check the per-child child windows' `BepInEx/LogOutput.log` — each child
  is a separate process and writes its own log.

---

## Building from source

Requires the .NET 6 SDK.

```bash
cd SledCoopMod
dotnet build -c Release
```

The build copies `SledCoopMod.dll` into `<repo>/BepInEx/plugins/` via an
MSBuild post-build target. Re-run the installer (or copy the DLL by hand)
to update the deployed copy in the game folder.

The csproj references game-specific interop DLLs from
`<your Steam path>/Sledding Game Demo/BepInEx/interop/`, generated by
BepInEx the first time you launch the modded game. Update the
`<GameInterop>` and `<UnityEditorManaged>` paths in
`SledCoopMod/SledCoopMod.csproj` to match your machine.
