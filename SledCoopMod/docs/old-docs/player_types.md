# Player Types

Game scripts in `Assembly-CSharp` that drive a player pawn. Each must be handled (suppressed or replicated) for every extra local player clone.

## Core Controllers

| Type | Path | Notes |
|------|------|-------|
| `PlayerControl` | `PlayerControl.cs` | Top-level controller; owns Rewired Player 0 input maps. Suppressed on clones via `Patch_PlayerControl_*`. Start/Update/FixedUpdate/LateUpdate/OnEnable/OnDisable all patched. |
| `PlayerLocalInput` | `PlayerLocalInput.cs` | Reads Rewired + raw axis input; returns move/look/jump/interact values. Postfixed by `Patch_PlayerLocalInput_*` to redirect to `LocalInputProvider` for clone pawns. |
| `PlayerMovement` | `PlayerMovement.cs` | Physics movement, teleport markers (StartHoldingMarkerPlace, HoldingMarkerPlace, StopHoldingMarkerPlace, StartHoldingMarkerRespawn, etc.). Called via reflection in `LocalCoopActions`. Suppressed on clones via `Patch_ClonePawnScriptSuppressor`. |
| `CharacterActor` | `CharacterActor.cs` | Kinematic character controller (likely Kinematic Character Controller or custom). Sets `isKinematic`. Suppressed on clones. |
| `CharacterBody` | `CharacterBody.cs` | Body physics / collider management. Suppressed on clones. |

## Animation & Visuals

| Type | Path | Notes |
|------|------|-------|
| `PlayerAnimationController` | `PlayerAnimationController.cs` | Drives Animator state machine. Suppressed on clones (Awake/Start/Update etc.). |
| `PlayerRagdollTypeHandler` | `PlayerRagdollTypeHandler.cs` | Hides character mesh until `sync_EquippedCharacterName` SyncVar fires — never fires on non-networked clones. Awake suppressed during spawn via `Patch_PlayerRagdollTypeHandler_Awake`. |
| `CharacterModelGeneralized` | `CharacterModelGeneralized.cs` | Character model/cosmetic switching. |
| `PlayerAccessoryController` | `PlayerAccessoryController.cs` | Cosmetic accessories on the character rig. |
| `PlayerUsernameController` | `PlayerUsernameController.cs` | Nameplate above player head. Needs rebinding per guest slot. |

## Camera

| Type | Path | Notes |
|------|------|-------|
| `PlayerCameraControl` | `PlayerCameraControl.cs` | Cinemachine-driven follow camera. Suppressed on clones. Each extra player gets a fresh plain `Camera` GO from `CameraLayoutManager`. |
| `PlayerFlyCamController` | `PlayerFlyCamController.cs` | Free-fly debug camera. Suppressed on clones. |

## Social / Party

| Type | Path | Notes |
|------|------|-------|
| `PlayerPartyGroupHandler` | `PlayerPartyGroupHandler.cs` | Party group logic (EOS). Suppressed on clones. |
| `PlayerVoiceController` | `PlayerVoiceController.cs` | Dissonance voice chat per player. Suppressed on clones; full guest voice needs DissonanceVoip wiring. |

## Special Abilities / Activities

| Type | Path | Notes |
|------|------|-------|
| `PlayerPushingController` | `PlayerPushingController.cs` | Push/shove interaction. Suppressed on clones. |
| `PlayerTeleportationController` | `PlayerTeleportationController.cs` | Teleport-to-marker logic. Suppressed on clones. |
| `PlayerInteraction` | `PlayerInteraction.cs` | Proximity interact with world objects. |
| `PlayerActivityController` | `PlayerActivityController.cs` | Manages activity state (idle, fishing, building, etc.). |
| `PlayerBuildingController` | `PlayerBuildingController.cs` | Build mode entry/exit; StartPlacingItem/StopPlacingItem called via reflection in `LocalCoopActions`. |

## Save / Stats

| Type | Path | Notes |
|------|------|-------|
| `PlayerSavedStats` | `PlayerSavedStats.cs` | Per-player cosmetic/achievement persistence (P1 native save). Guest slots use `GuestSaveManager` + `GuestPlayerStats` instead. Need proxy for cosmetics. |
| `CharacterPoints` | `CharacterPoints.cs` | Per-pawn live points component. Must be wired to `GuestSaveManager.AddPoints` for guest slots. |

## Mod Implementation Status

- [x] Clones spawned with `SpawnHooks`
- [x] Conflicting scripts suppressed via `Patch_ClonePawnScriptSuppressor`
- [x] Input redirected via `LocalInputProvider` + `LocalCoopMovement`
- [x] Sled/snowball/build/marker actions via `LocalCoopActions` (reflection)
- [x] `CharacterPoints` → `GuestSaveManager` wiring (`Patch_CharacterPoints_AddPoints`)
- [x] `PlayerSavedStats` suppressed on clone pawns — prevents P1 save contamination; clone cosmetics frozen at host default. Full per-guest cosmetic selection is a hard gap.
- [x] `AchievementController`, `CharacterModelGeneralized`, `PlayerAccessoryController`, `UIControllerIconsManager` added to suppressor (prevent crashes from reading uninitialized `PlayerSavedStats`)
- [x] `PlayerUsernameController` nameplate: suppressor postfix (`Patch_PlayerUsernameController_GuestBind`) sets backing field and walks child Text/TextMeshPro to show guest username
- [ ] `PlayerVoiceController` functional for guest slots (requires DissonanceVoip per slot — hard gap)
