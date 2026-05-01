# Split-screen Architecture

This document explains the root architecture needed for full local split-screen coop support. It maps the original game systems to the mod components that must emulate them.

## Why this is required

The base game is designed for a single host player and networked remote players. Local split-screen coop requires the mod to create extra guest pawns, cameras, HUDs, and input routing while preventing the game's single-player systems from conflicting with those clones.

## Root architecture

### Original game systems

| Game system | Original file(s) | Responsibility |
|-------------|------------------|----------------|
| Main player lifecycle | `PlayerControl.cs` | Controls the host pawn and owns Rewired player inputs |
| Camera system | `PlayerCameraControl.cs` | Cinemachine follow camera and host camera behavior |
| UI HUD | `UIHUD.cs`, `UIHUD_Points.cs`, `UIHUD_Icons.cs`, `CharacterCanvas.cs` | Host HUD and world-space nameplate UI |
| Game pause and menu | `UIPausePanel.cs`, `UiReferenceController.cs` | Pause/resume logic and cursor handling |
| Boot sequence | `BootSceneManager.cs`, `LobbySettings.cs` | Session boot, scene load, reset state |

### Mod components

| Mod component | File | Purpose |
|---------------|------|---------|
| `ModBootstrap` | `ModBootstrap.cs` | Persistent host GameObject for all managers |
| `LocalPlayerManager` | `LocalPlayerManager.cs` | Manages guest slots, joined status, and slot metadata |
| `CameraLayoutManager` | `CameraLayoutManager.cs` | Creates split-screen cameras, refreshes viewport layout, binds guest cameras |
| `HudManager` | `HudManager.cs` | Clones HUD canvases and binds them to guest cameras |
| `SceneWatcher` | `SceneWatcher.cs` | Tracks gameplay scenes, applies/removes gameplay patches, and manages session state |
| `LocalCoopUI` | `LocalCoopUI.cs` | Draws per-slot IMGUI overlay, settings, pause controls |
| `SpawnHooks` | `SpawnHooks.cs` | Detects host pawn, spawns guest pawns, and manages clone lifecycle |
| `LocalPlayerSpawner` | `LocalPlayerSpawner.cs` | Prepares clone pawn GameObjects, attaches local components, and handles FishNet registration |

## Camera and viewport layout

The mod must make each guest slot appear as an independent player viewport without using the game’s native follow camera.

### Original game files

- `PlayerCameraControl.cs` — host camera logic
- `PlayerFlyCamController.cs` — debug fly camera

### Mod behavior

- `CameraLayoutManager` creates a new plain `Camera` for each guest slot.
- The viewports are arranged based on split orientation and player count.
- The mod does not use `PlayerCameraControl` on clone pawns; the game camera is suppressed for clones.
- Each guest camera is bound to a cloned HUD canvas by `HudManager`.

## HUD cloning and canvas management

The game only creates one `UIHUD` and `CharacterCanvas` per host. The mod must clone these UI roots for each guest.

### Original game files

- `UIHUD.cs` — HUD root
- `UIHUD_Points.cs` — point counter
- `UIControllerIconsManager.cs` — button prompts
- `CharacterCanvas.cs` — world-space nameplate canvas

### Mod behavior

- `HudManager` detects the host HUD canvas during `UIHUD.Awake` and clones it for each guest slot.
- Cloned HUD canvases are bound to guest cameras and to guest slot data.
- Guest `UIHUD_Points` elements are rebound to `CharacterPoints` on the clone pawn.
- `CharacterCanvas` world nameplates are updated via `Patch_CharacterCanvas_GuestBind`.
- `UIControllerIconsManager` is suppressed on clones to avoid singleton conflicts.

## Clone pawn lifecycle

The mod must instantiate and configure a guest pawn for every joined slot.

### Original game files

- `PlayerControl.cs` — host pawn root behavior
- `PlayerMovement.cs` — movement logic
- `PlayerHoldingController.cs` — snowball and item actions
- `PlayerBuildingController.cs` — build placement and item handling
- `PlayerRagdollTypeHandler.cs` — ragdoll/animation startup
- `PlayerSavedStats.cs` / `CharacterPoints.cs` — per-player score and stats

### Mod behavior

- `SpawnHooks` detects the host pawn and spawns clones named `SledCoopP{n}`.
- `LocalPlayerSpawner` prepares clone GameObjects by adding `LocalPlayer`, `LocalCoopMovement`, `LocalCoopActions`, and other helper components.
- `LocalCoopMovement` drives physics movement from local inputs instead of the game’s native movement loops.
- Game logic scripts on clones are suppressed via `Patch_ClonePawnScriptSuppressor` and `Patch_CloneGameplayOnlySuppressor` to prevent conflicts.
- `FishNetOwnershipPatcher` makes `IsOwner`, `IsServerInitialized`, and `IsClientInitialized` return true for clones so network checks inside `RpcLogic___` paths behave consistently.

## Scene transition handling

Clones must survive scene loads and continue working after transitions.

### Original game systems

- `UnityEngine.SceneManagement.SceneManager` — scene load events
- `BootSceneManager.cs` / `BootSceneManager.InitializeBootables()` — boot flow and first scene load

### Mod behavior

- `SceneWatcher` tracks whether the game is in a gameplay scene and applies gameplay-specific patches only during active sessions.
- The mod should subscribe to `SceneManager.sceneLoaded` to rebind clone cameras, HUDs, and any lost state after scene transitions.
- `LocalPlayerSpawner` currently creates clones only once per session, so scene reload or new gameplay scenes must preserve clone state correctly.

## What a full support guide must do

This document is the top-level reference for full support, but the deeper details are in dedicated docs:

- [Split-screen Architecture](docs/splitscreen_architecture.md)
- [Slot Input Lifecycle](docs/slot_input_lifecycle.md)
- [Network / Fallback](docs/network_fallback.md)
- [Custom Server Implementation](docs/custom_server_implementation.md)
- [Networking](docs/networking.md)
- [Full Support Checklist](docs/full_support_checklist.md)
