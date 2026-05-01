# Process Launch

The host process resolves the game executable from `Application.dataPath`, then
starts one child process for each joined extra local slot.

## Child arguments

Example for player 2:

```text
--sledcoop-role=client
--sledcoop-host=127.0.0.1
--sledcoop-port=7770
--sledcoop-slot=1
--sledcoop-profile=Guest1
--sledcoop-device=Gamepad0
```

## Windowing

Networked instance mode uses separate game windows instead of split-screen cameras.
The host computes a desktop layout from the existing config and passes initial
Unity window size arguments to each child:

```text
-screen-fullscreen 0
-screen-width <computed>
-screen-height <computed>
-popupwindow
```

The child also reapplies its final position after boot because Unity/Wine may not
have a usable window handle during process startup.

Layout rules:

- `TwoPlayerSplitOrientation=Vertical`: same-monitor windows are left/right halves.
- `TwoPlayerSplitOrientation=Horizontal`: same-monitor windows are top/bottom halves.
- 3 and 4 players reuse the legacy split layout, translated into desktop window bounds.
- `MultiDisplayEnabled=true`: child slot `N` uses monitor `N` at full monitor size when enough monitors exist.
- If multi-display is enabled but there are not enough monitors, layout falls back to same-monitor tiling.
- In multi-monitor mode the Steam-launched host window is not moved; children are placed on their assigned monitors.

## Shutdown

The host tracks child `Process` handles. Leaving a slot or ending the host session
should kill the associated child process tree. Children that exit on their own are
reaped and logged.

## Profile isolation

Each child receives a profile name. Runtime save/profile redirection still needs a
dedicated implementation phase so child instances do not overwrite host settings.
