# Migration Plan

## Phase 1: Scaffolding

- Add role command-line parsing.
- Add `NetworkedInstanceManager`.
- Add config flags for networked mode, legacy clone mode, child process launch, and Tugboat port.
- Route the custom server button through networked mode by default.

## Phase 2: First playable connection

- Host starts Tugboat server and local client.
- Host launches one child process.
- Child connects to host and receives a native player.
- Confirm logs contain no `SledCoopP*` spawn lines.

## Phase 3: Input and profile isolation

- Filter `PlayerLocalInput` per process role/device.
- Redirect child profile/save paths.
- Add child window placement.

## Phase 4: Retire clone primary path

- Leave clone mode behind `LegacyLocalCloneMode`.
- Disable clone camera/HUD/movement/sled systems in networked mode.
- Remove ownership getter patches from the default flow.

## Phase 5: Optional platform lobby support

- Investigate Steam/EOS with unique platform identities.
- Keep Tugboat loopback as the stable same-machine baseline.
