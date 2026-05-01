# Slot Input Lifecycle

This document describes how local guest slots are joined, configured, and routed through the mod so each player gets independent controls and pause handling.

## What the game expects

The base game is written for a single player using `PlayerControl` and `PlayerLocalInput`.

### Original game files

- `PlayerControl.cs` — host pawn ownership, input focus, and gamepad assignment
- `PlayerLocalInput.cs` — reads Rewired and raw axis input and returns move/look/jump/interact values
- `PlayerMovement.cs` — applies physics movement based on host inputs
- `PlayerHoldingController.cs` — snowball pickup, throw, and item action commands
- `PlayerBuildingController.cs` — build mode placement and object interactions
- `PlayerInteraction.cs` / `PlayerActivityController.cs` — activity and interaction state
- `UIPausePanel.cs` / `UiReferenceController.cs` — pause logic and cursor handling

## Mod responsibilities

The mod must build an independent input lifecycle for each guest slot while preserving the host game’s single-player systems.

### Slot registry and join/remove

- `LocalPlayerManager` manages slot definitions, joined state, and metadata for slots 0–3.
- Slot 0 is the host; slots 1–3 are local guests.
- `InputRouter` detects gamepad connect/disconnect and keyboard-only guest claims.
- `LocalCoopUI` exposes per-slot controls for joining and leaving.

### Local input provider

The mod does not allow clone pawns to use `PlayerLocalInput` directly.

- `LocalInputProvider` is the per-slot input source.
- It reads keyboard/gamepad state via reflection-safe code to avoid IL2CPP crashes.
- It exposes normalized axes, button presses, and action queries to the clone runtime.

### Redirecting host-style input

- `Patch_PlayerLocalInput_*` postfixes redirect `PlayerLocalInput` return values to `LocalInputProvider` for clones.
- These patches also block joystick/keyboard bleed from guest slots into the host player.
- If a clone slot owns a gamepad, the mod prevents that stick from driving P1.

### Action execution

Guest actions are executed through reflection and local helper components.

- `LocalCoopActions` contains wrappers for sled actions, snowball pickup/throw, building, and marker placement.
- It invokes the game’s internal `RpcLogic___` methods so guest actions run through the same game logic paths as host actions.
- `Patch_CloneGameplayOnlySuppressor` allows `PlayerHoldingController` and `PlayerBuildingController` to initialize enough to support these methods while still suppressing conflicting lifecycle code.

### Local movement

- `LocalCoopMovement` applies physics movement for clone pawns using local inputs.
- It replaces the game’s normal `PlayerMovement` update path for clones.
- This component is attached by `LocalPlayerSpawner` during clone setup.

### Pause and settings

- `LocalCoopUI` draws guest status badges and per-slot pause controls.
- `Patch_UIPausePanel_*` suppresses the native pause panel when any guest slot is paused.
- `UiReferenceController.Update` is patched to keep the cursor unlocked while the mod settings overlay is open.

## What full support requires

### Required mod behavior

- Each guest slot must have a unique `LocalInputProvider`.
- The input router must bind gamepads and keyboard controls without causing host input bleed.
- Guest actions must use the game’s own `RpcLogic___` paths where possible.
- Pause and inventory state must be handled per slot.
- The mod must keep guest `PlayerLocalInput` data separate from host input.

### Key mod files

- `LocalPlayerManager.cs`
- `InputRouter.cs`
- `LocalInputProvider.cs`
- `LocalCoopUI.cs`
- `LocalPlayerSpawner.cs`
- `LocalCoopActions.cs`
- `LocalCoopMovement.cs`
- `Patches/DiagnosticPatches.cs`

## Implementation notes

- Guest slot input is intentionally isolated from `PlayerControl` and `PlayerLocalInput` on P1.
- For local guests, `GetMoveInput`, `GetLookInput`, `GetJumpDown`, `GetInteractDown`, `GetInventoryDown`, and `GetPauseDown` are all postfixed.
- Input providers are stored in a slot registry instead of being attached directly to cloned `NetworkBehaviour` objects.
- Shared game state for activities like build item selection is coordinated through the mod’s slot data and overlay UI.
