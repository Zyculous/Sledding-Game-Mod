# Network / Fallback

This document explains the custom server startup flow and fallback behavior used by the mod for local offline coop sessions.

## Why custom server startup exists

The base game’s offline host path is tied to single-player session logic and does not reliably support split-screen clone management.

The mod replaces the native offline host menu behavior with a custom local server startup path so it can:

- create guest clone pawns before game initialization completes
- control the server/client lifecycle for local slots
- avoid the native offline host menu prompt and use a dedicated coop flow
- eliminate any native EOS/Steam lobby startup for local split-screen play
- suppress EOS boot-time platform initialization and skip `_Scripts.Boot.EOSAuthenticator` during boot

## Key components

- `OfflineModeManager` — owns the custom local server request state and startup retry logic
- `LocalPlayerManager` — tracks which local slots are active and whether custom server mode is required
- `SceneWatcher` — ensures startup actions are executed during the correct scene lifecycle

## Custom server startup flow

1. The user selects `START CUSTOM SERVER` in `LocalCoopUI`.
2. `OfflineModeManager.StartOfflineLocalGame()` sets `customServerRequested = true` and triggers `TryStartCustomServer()`.
3. `TryStartCustomServer()` attempts to start the server using `FishNet`/local session APIs.
4. If startup succeeds, `activeCustomServer = true` and the mod enters coop mode.
5. If startup fails, the mod logs the failure and retries until the `NetworkManager` becomes available.

## Retry and fallback logic

- `OfflineModeManager.ProcessPendingServerStartup()` is called each frame by `ModBootstrap.Update()` when startup is pending.
- This allows the mod to wait for the game state to become ready before reattempting local server startup.
- If the local start path fails repeatedly or if the game is already in a state incompatible with a custom server, the mod remains in offline startup retry mode rather than invoking the native online/offline lobby flow.

## Runtime states

| State | Meaning |
|-------|---------|
| `customServerRequested` | The user requested a local coop server start |
| `activeCustomServer` | The mod has successfully taken over local host server startup |

## What full support needs

- A robust local startup path that works across the game’s lobby and gameplay scenes
- Reliable offline-only local startup without any native EOS/Steam lobby takeover
- Clear UI feedback for the player when the mod is starting the custom server or when fallback occurs
- Safe handling of the native `MatchmakingManager`/offline host logic so it does not conflict with clone creation

## Related docs

- [Custom Server Implementation](docs/custom_server_implementation.md) — FishNet registration and clone `NetworkObject` state.
- [Split-screen Architecture](docs/splitscreen_architecture.md) — guest clone lifecycle, cameras, and HUD layout.
- [Slot Input Lifecycle](docs/slot_input_lifecycle.md) — guest input binding and pause handling.
- [Full Support Checklist](docs/full_support_checklist.md)

## Related files

- `OfflineModeManager.cs`
- `ModBootstrap.cs`
- `LocalCoopUI.cs`
- `SceneWatcher.cs`
- `LocalPlayerManager.cs`
