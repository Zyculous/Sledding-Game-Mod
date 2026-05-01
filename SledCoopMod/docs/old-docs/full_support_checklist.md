# Full Support Checklist

A practical checklist for implementing and validating full local split-screen coop support in the mod.

## Architecture

- [x] Persistent mod bootstrap object exists and survives scene loads (`ModBootstrap.cs`)
- [x] Guest slot registry supports at least 3 local slots plus host (`LocalPlayerManager.cs`)
- [x] Split-screen cameras and viewports are created dynamically (`CameraLayoutManager.cs`)
- [x] HUD canvases are cloned and bound per guest camera (`HudManager.cs`)
- [x] Clone pawns are spawned from the host pawn and named predictably
- [x] Clone lifecycle is managed on scene transitions (`SceneWatcher.cs`)

## Input and actions

- [x] Each guest has a unique `LocalInputProvider`
- [x] Gamepad/keyboard routing does not bleed into the host player
- [x] Guest input values are redirected to `PlayerLocalInput` patches
- [x] Guest action commands invoke the game’s `RpcLogic___` methods
- [x] Movement for clones is driven by `LocalCoopMovement` (with sprint)
- [x] Building and inventory actions are supported for guests
- [x] Pause and menu input are handled safely for local coop

## UI and visuals

- [x] Each guest has a functional HUD overlay (IMGUI badges + cloned canvas)
- [x] Score and points display correctly per slot (`Patch_UIHUD_Points_Bind` + `GuestSaveManager`)
- [x] Nameplates and player identifiers work for clones (`Patch_PlayerUsernameController_GuestBind`, `Patch_CharacterCanvas_GuestBind`)
- [x] Custom menu text clearly shows local coop / custom server mode
- [x] Split-screen layout updates on resize or slot count changes (event-driven via `OnActivePlayersChanged`)

## Networking and startup

- [x] Custom local server startup flow is used instead of native offline host
- [x] `OfflineModeManager` can request and activate `customServerRequested`
- [x] Custom server startup is retried safely when game state is not ready
- [x] Fallback to native offline host is available if needed
- [x] `activeCustomServer` is clearly tracked and exposed in logs/UI
- [x] Client connection started after server (full FishNet host mode via `ClientManager.StartConnection`)

## Stability and compatibility

- [x] Obsolete Unity APIs are replaced with IL2CPP-safe alternatives
- [x] Nullable and null-reference guards exist around patched provider lookups
- [x] Original game systems are suppressed on clones to prevent duplicate behavior
- [x] No duplicate `UIHUD`, camera, or input systems are active for clones
- [x] Build completes cleanly with no fatal compile errors
- [x] Type / method lookups go through `PatchHelpers.SafeTypeByName` /
      `PatchHelpers.FindMethod` so missing optional types do not trigger the
      HarmonyX `ReflectionTypeLoadException` warning storm
      (see [runtime_hardening.md](runtime_hardening.md))
- [x] `SceneWatcher` no longer subscribes to `SceneManager.sceneLoaded`
      (`UnityAction\`2` ctor is missing in this Il2CppInterop build); scene-name
      polling in `Update()` arms the same one-frame clone-recheck path
- [x] Optional probes (`Patch_UIHUD_Awake`, `Patch_UIPausePanel_*`,
      race UI variants) resolve to null cleanly and are reported only at
      Debug — `[Harmony] N applied, M skipped` is the surfaced summary
- [x] Early P2 join (slot claimed before host pawn spawns) only logs at
      Debug; `OnHostPawnSpawned` re-runs the layout/wire/HUD sequence once
      the host camera registers

## Validation steps

1. Start the game and enter the local coop menu.
2. Create a custom server using the mod UI.
3. Join guest slots using keyboard/gamepad.
4. Confirm each guest has a camera, HUD, and input response.
5. Test snowball pickup/throw and building actions for at least one guest.
6. Verify score points update for each guest.
7. Reload or transition scenes and confirm clones persist.
8. Test fallback by forcing local server failure and ensuring native host option still works.

## Known remaining gaps (hard)

- [ ] Voice chat (`DissonanceVoip`) — requires per-slot Dissonance wiring; no safe IL2CPP path identified.
- [ ] EOS/Steam party presence for local guests — platform SDK constraint.
- [ ] Sled physics object end-to-end confirmation — `RpcLogic___Cmd_Sled___` is reachable and host fields are now copied to clone, but a live networked sled NetworkObject spawn has not been confirmed. `LocalCoopMovement` visual sled cube is the active fallback.
- [ ] Per-guest cosmetic selection — clones share host default model; `PlayerSavedStats` / `CharacterModelGeneralized` are suppressed on clones to prevent save-data bleed.
- [ ] Networked `PlayerRacingController` — replaced locally by `GuestRacingTracker`; native networked race tracking not wired.

## Notes

- This checklist is intended for the mod implementation, not the base game.
- Full support requires both code behavior and documentation of the guest slot flow.
- Use these docs together with `IMPLEMENTATION_GUIDE.md` for developer onboarding and debugging.

## See also

- [Split-screen Architecture](splitscreen_architecture.md)
- [Slot Input Lifecycle](slot_input_lifecycle.md)
- [Network / Fallback](network_fallback.md)
- [Custom Server Implementation](custom_server_implementation.md)
- [Runtime Hardening](runtime_hardening.md)
- [Type Compatibility](type_compatibility.md)
