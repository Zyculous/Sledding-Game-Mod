# Type Compatibility — Game Types vs. Mod Patches

Snapshot of which Assembly-CSharp types our patches target, which exist
in this build, and what the patch does when the type is missing.

Source of truth: `Unity Files/Scripts/Assembly-CSharp/` decompiled output
plus the runtime log from a clean boot. Use the `[Harmony] N applied,
M skipped` line in `LogOutput.log` to verify after a binary update.

## Confirmed-present types (patches active)

| Type | Namespace / file | Patches |
|------|------------------|---------|
| `PlayerControl` | global | `Patch_PlayerControl_Awake` (always) + lifecycle suppressors (gameplay) |
| `PlayerLocalInput` | global | `Patch_PlayerLocalInput_GetMoveInput / GetLookInput / GetMoveInputRaw / GetJumpDown / GetInteractDown / GetInventoryDown / GetPauseDown / GetLookInputRaw` |
| `PlayerRagdollTypeHandler` | global | `Patch_PlayerRagdollTypeHandler_Awake` (gameplay only) |
| `PlayerHoldingController`, `PlayerBuildingController` | global | gameplay-only suppressor (FishNet callbacks **allowed**) |
| `PlayerAnimationController`, `PlayerPushingController`, `PlayerMovement`, `PlayerCameraControl`, `PlayerVoiceController`, `PlayerPartyGroupHandler`, `PlayerRacingController`, `PlayerUsernameController`, `PlayerActivityController`, `PlayerFlyCamController`, `PlayerTeleportationController` | global | full clone-pawn lifecycle suppressor |
| `CharacterActor`, `CharacterBody` | KCC | full lifecycle suppressor |
| `CharacterCanvas` | global | `Patch_CharacterCanvas_GuestBind` (rebinds username) |
| `CharacterPoints` | global | `Patch_CharacterPoints_AddPoints` (mirrors guest delta to GuestSaveManager) |
| `PlayerSavedStats`, `AchievementController`, `CharacterModelGeneralized`, `PlayerAccessoryController` | global | suppressed on clone roots to prevent host save bleed |
| `RaceCheckpoint` | global | `Patch_RaceCheckpoint_TriggerEnter` postfix calls `GuestRacingTracker.TryAdvance` |
| `LobbySettings` | global | `Patch_LobbySettings_ResetStatics` triggers `LocalPlayerManager.ResetForNewSession` |
| `_Scripts.Boot.BootSceneManager` | `_Scripts/Boot/` | `Patch_BootSceneManager_Init` triggers `SpawnHooks.OnBootComplete` |
| `_Scripts.UI.Components.UIPanel` | `_Scripts/UI/Components/` | `Patch_UIPanel_Awake` postfix scans for HUD canvas |
| `_Scripts.UI.In_Game.UIHUD` | `_Scripts/UI/In_Game/` | **field only** — no `Awake` method exists; HUD is detected via `UIPanel.Awake` and `Canvas.Awake` instead |
| `_Scripts.UI.In_Game.UIPausePanel` | `_Scripts/UI/In_Game/` | **no `OnEnable` / `OnDisable`** in interop; pause state is driven by `Patch_LeanTween_PauseAll` / `ResumeAll` instead |
| `_Scripts.UI.Manager.UIManager` | `_Scripts/UI/Manager/` | `Patch_UIManager_OpenPanel` prefix gates while settings overlay is open |
| `UiReferenceController` | global | `HandleInput_RegardlessOfNetwork` prefix; `Update` postfix re-asserts cursor state |
| `LeanTween` | global | `pauseAll` / `resumeAll` postfixes drive `SceneWatcher.IsGamePaused` |
| `BuildableItemType` | global enum | resolved via `SafeTypeByName` for guest inventory cycling |
| `MySteamLobby`, `_Scripts.Managers.LobbyManager` | global | `OfflineModeManager` pre-resolves these for offline-mode probing |
| `RaceManager`, `RaceData` | `_Scripts.Managers` | live in the build but **not yet wired**; `GuestRacingTracker` is the local substitute. `Patch_RaceController_OnRaceStart` probes `RaceManager` first when present. |

## Probed-and-missing types (patch silently skipped)

These types are referenced by name in the patch source for forward / backward
compatibility with future builds. Each one resolves to `null` via
`SafeTypeByName`, the patch is skipped, and the boot log shows
`Harmony: N applied, M skipped` only at `Debug` level — no warning.

| Probed name | Used by | Why skipped here | Replacement / fallback in this build |
|-------------|---------|------------------|--------------------------------------|
| `UIHUD` (root, no namespace) | `Patch_UIHUD_Awake` | type **does** exist as `_Scripts.UI.In_Game.UIHUD` but has no `Awake` | `Patch_UIPanel_Awake` + `Patch_Canvas_Awake` |
| `UIPausePanel` (root) | `Patch_UIPausePanel_OnEnable / OnDisable / Open` | type exists but no `OnEnable` / `OnDisable` in interop; `Open` is async-state-machine only | `LeanTween` pause/resume hooks |
| `UIControllerIconsManager` | `Patch_ClonePawnScriptSuppressor` | type does not exist in this build | suppressor sweeps remaining HUD scripts via root-name guard |
| `RaceController`, `RaceSession`, `RaceSessionManager` | `Patch_RaceController_OnRaceStart` | not present; only `RaceManager` exists | `GuestRacingTracker.ResetRace()` is invoked when a future patch lands |
| `UIRaceResults`, `RaceResultsPanel`, `RaceResults`, `UILeaderboard`, `RaceLeaderboard`, `UIRaceLeaderboard`, `RaceScoreboard`, `UIRaceScoreboard` | `Patch_RaceLeaderboard_GuestData` | not present; race UI ships under different names (or is added later) | per-slot status/badge in `LocalCoopUI` IMGUI overlay |

## Adding a new patch

1. Confirm the target lives in Assembly-CSharp by grepping
   `Unity Files/Scripts/Assembly-CSharp/`.
2. Use `PatchHelpers.SafeTypeByName` and `PatchHelpers.FindMethod` (never raw
   `AccessTools.TypeByName` / `AccessTools.Method` in `TargetMethod()`).
3. Return `null` cleanly when the type or method is absent — `Plugin.Load`
   logs the skip at Debug and continues.
4. If the patch is gameplay-session-scoped, pre-resolve the target in
   `GameplayPatcher.PreResolve` instead of using `[HarmonyPatch]`.
