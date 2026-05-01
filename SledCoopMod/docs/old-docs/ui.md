# UI

Game scripts for the in-game HUD, pause menu, canvas management, and lobby UI.

## In-Game HUD

| Type | Path | Notes |
|------|------|-------|
| `UIHUD` | `UIHUD.cs` | Root HUD MonoBehaviour. `Awake` patched by `Patch_UIHUD_Awake` to detect the Canvas and register it with `HudManager`. |
| `UIHUD_Points` | `UIHUD_Points.cs` | Displays live point counter. Bound to `CharacterPoints` on host pawn; needs rebinding per cloned HUD canvas. |
| `UIHUD_Crosshairs` | `UIHUD_Crosshairs.cs` | Crosshair element shown during aiming/throwing. |
| `UIHUD_Icons` | `UIHUD_Icons.cs` | Icon sprites on the HUD (activity indicators, etc.). |
| `UIHUD_Indicators` | `UIHUD_Indicators.cs` | Screen-edge indicators for off-screen events. |
| `CharacterCanvas` | `CharacterCanvas.cs` | World-space canvas anchored above a player pawn (health bar, nameplate area). |

## Canvas Management

| Type | Path | Notes |
|------|------|-------|
| `UIManager` | `UIManager.cs` | Singleton managing which UI panels are open. Switching panels calls `UIPanel.Open/Close`. |
| `UIPanel` | `UIPanel.cs` | Base class for all full-screen UI panels. `UIPanel.Awake` patch attempted but skipped (method not found in IL2CPP interop). |
| `UiReferenceController` | `UiReferenceController.cs` | Manages cursor lock state each frame via `Update_CursorLockState()`. `Update` patched by `Patch_UiReferenceController_Update` to re-assert cursor-free state when mod settings overlay is open. Also has `HandleInput` patched by `Patch_UIPanel_HandleInput` in UIPatches.cs. |

## Pause Menu

| Type | Path | Notes |
|------|------|-------|
| `UIPausePanel` | `UIPausePanel.cs` | Pause screen. `OnEnable`/`OnDisable` patched (if found) to set `SceneWatcher.IsGamePaused`. `LeanTween.pauseAll/resumeAll` used as belt-and-suspenders signal. |

## Lobby UI

| Type | Path | Notes |
|------|------|-------|
| `UICreateLobby` | `UICreateLobby.cs` | Create-lobby screen. |
| `UILobbyExplorer` | `UILobbyExplorer.cs` | Browse/join-lobby screen. |

## Controller Icons

| Type | Path | Notes |
|------|------|-------|
| `UIControllerIconsManager` | `UIControllerIconsManager.cs` | Swaps button prompt icons based on active input device (keyboard vs gamepad). Needs to be per-slot aware for guest players. |

## Mod Overlay (IMGUI)

The mod uses Unity IMGUI (no asset dependencies) for its overlay, drawn in `LocalCoopUI.OnGUI()`:

- **Menu mode**: slot add/remove buttons, offline-start button, player count
- **Game mode**: per-slot status badges in each viewport corner
- **Settings overlay** (RCtrl+L): max players, split orientation, verbose logging, per-slot device picker, username fields, in-game spawn/despawn

## Mod Implementation Status

- [x] `UIHUD.Awake` patched to detect and register host HUD canvas
- [x] `HudManager` clones HUD canvas per guest slot, binds to slot camera
- [x] `UiReferenceController.Update` patched to preserve cursor state when settings open
- [x] `LeanTween.pauseAll/resumeAll` patched for pause state tracking
- [x] `UIHUD_Points` rebound per cloned canvas to guest `CharacterPoints` (`Patch_UIHUD_Points_Bind`)
- [x] `UIControllerIconsManager` suppressed on clone pawn roots (`ConflictingTypeNames`) — prevents singleton conflicts. Icon swapping for guest slots is not implemented (acceptable; guests see game defaults).
- [x] `CharacterCanvas` (world-space nameplate) guest username set via `Patch_CharacterCanvas_GuestBind` — sets backing field and walks child Text/TextMeshPro after Start
- [x] Per-slot IMGUI pause menu (`LocalCoopUI`) covers each viewport independently; game's `UIPausePanel` suppressed while any clone slot is paused
