# Testing Checklist

## Startup

- Host log shows `role=Host`.
- Child log shows `role=Client`.
- Host log shows Tugboat transport selected.
- Child log shows Tugboat client connection requested.

## Player spawning

- `PlayerReferenceManager` has one entry per connected process.
- Each player has a distinct FishNet connection id.
- Logs contain no `SledCoopP*` spawn lines in networked mode.

## Gameplay

- Each process has its own native camera and HUD.
- Host overlay disappears after networked startup/gameplay begins.
- Networked logs contain no `SledCoopCam_*`, `SledCoopHUD_*`, or `SledCoopP*` setup lines.
- Movement and jumping use native `PlayerMovement`.
- Sledding spawns and uses native `PlayerSledController`/`Sled`.
- Snowballs, building, racing, and animations use native RPCs.

## Shutdown

- Leaving a slot stops the child process.
- Host session end kills all child processes.
- Server removes disconnected child players.

## Window layout

- With `TwoPlayerSplitOrientation=Vertical` and `MultiDisplayEnabled=false`, P1 is left half and P2 is right half.
- With `TwoPlayerSplitOrientation=Horizontal` and `MultiDisplayEnabled=false`, P1 is top half and P2 is bottom half.
- With `MultiDisplayEnabled=true` and enough monitors, child windows fill their assigned monitors.
- With `MultiDisplayEnabled=true` and too few monitors, windows fall back to same-monitor tiling.

## Failure triage

- If child cannot connect, check Tugboat port and firewall.
- If no native player spawns, check `PlayerSpawner` and FishNet scene loading.
- If input leaks between windows, implement the `PlayerLocalInput` process/device filter phase.
