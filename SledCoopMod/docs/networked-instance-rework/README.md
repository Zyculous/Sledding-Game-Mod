# Networked Instance Rework

This plan replaces in-process `SledCoopP*` clone players with real FishNet clients.
The host instance starts a Tugboat server and local client, then launches one child
game process for each extra local player. Each child connects as a normal client so
the game owns player spawning, camera, HUD, movement, sledding, RPCs, and SyncVars.

## Goals

- Use the native FishNet lifecycle instead of local pawn copies.
- Let the game spawn real `Player Networked` prefabs for every player.
- Keep movement, sledding, snowballs, building, racing, and animation on native game code.
- Make the old clone path a legacy fallback only.
- Support same-machine coop first through Tugboat loopback.

## Non-goals for v1

- Public Steam/EOS matchmaking for multiple local players on one Steam account.
- Voice chat per local child beyond whatever the child process naturally supports.
- Single-window split-screen rendering. Multi-instance uses one Unity window per player.

## Glossary

- Host instance: the first game process, controlled by player 1.
- Child instance: a launched game process for an extra local player.
- Networked instance mode: the new default architecture using real FishNet clients.
- Legacy clone mode: the old `SledCoopP*` in-process clone architecture.

## Phase Order

1. Add docs and scaffolding for command-line roles and Tugboat startup.
2. Route the custom server button through networked instance mode.
3. Launch child processes and verify they connect as FishNet clients.
4. Move input/profile isolation from documentation to runtime patches.
5. Retire clone-specific movement, sled, HUD, and ownership patches from the default path.

## Subdocuments

- [Current State](current-state.md)
- [Architecture](architecture.md)
- [Process Launch](process-launch.md)
- [FishNet Integration](fishnet-integration.md)
- [Input and Profiles](input-and-profiles.md)
- [Migration Plan](migration-plan.md)
- [Testing Checklist](testing-checklist.md)
