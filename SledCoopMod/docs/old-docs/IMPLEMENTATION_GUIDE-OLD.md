# SledCoopMod — Implementation Guide

> **Active checklist & current status:** [NEW_PLAN.md](NEW_PLAN.md).
> **EOS removal plan:** [docs/eos_disable_plan.md](docs/eos_disable_plan.md).
> **Latest blocker (2026-04-30):** EOS suppression patches don't apply at
> `Plugin.Load` because the EOS plugin assemblies aren't loaded yet — `21
> skipped` in the log are the EOS suppressors. The post-spawn freeze is most
> likely a downstream symptom. Fix path is "Phase A — deferred patcher" in
> [NEW_PLAN.md §4](NEW_PLAN.md#4-new-plan-summary-full-detail-in-docseos_disable_planmd).

This document is the top-level reference for everything in the base game that must be integrated for full local co-op support. Each category file lists the relevant game types, their purpose, and what the mod still needs to implement.

## Category Docs

| Category | File | What it covers |
|----------|------|----------------|
| Player | [docs/player_types.md](docs/player_types.md) | Controllers, movement, camera, animation, input |
| Points | [docs/points_economy.md](docs/points_economy.md) | Points/score system, achievements, HUD |
| Sled | [docs/sled_types.md](docs/sled_types.md) | Sled equip/unequip, racing, checkpoints |
| Networking | [docs/networking.md](docs/networking.md) | FishNet, lobbies, Steam/EOS, voice |
| Building | [docs/building.md](docs/building.md) | Placeable items, build mode controller |
| Snowballs | [docs/snowballs.md](docs/snowballs.md) | Snow pickup, throwing, cannons, snowmen |
| UI | [docs/ui.md](docs/ui.md) | HUD, pause, canvas management |
| Split-screen Architecture | [docs/splitscreen_architecture.md](docs/splitscreen_architecture.md) | Root split-screen platform, camera/HUD layout, clone lifecycle |
| Slot Input Lifecycle | [docs/slot_input_lifecycle.md](docs/slot_input_lifecycle.md) | Local slot join/remove, input routing, pause, guest actions |
| Network / Fallback | [docs/network_fallback.md](docs/network_fallback.md) | Custom server startup, FishNet spawn/despawn, offline fallback |
| Full Support Checklist | [docs/full_support_checklist.md](docs/full_support_checklist.md) | End-to-end implementation checklist and gap tracking |
| Custom Server | [docs/custom_server_implementation.md](docs/custom_server_implementation.md) | FishNet API reference, clone spawn/despawn patterns, scene transitions |
| Runtime Hardening | [docs/runtime_hardening.md](docs/runtime_hardening.md) | IL2CPP-safe lookups, log throttling, scene-event workarounds, current quiet-startup contract |
| Type Compatibility | [docs/type_compatibility.md](docs/type_compatibility.md) | Game-side type/method names that exist vs. probed-and-missing, with patch wiring notes |

## New Guide Sections

This top-level guide now points to dedicated, developer-focused split-screen coop support docs. Each new document maps mod behaviour to the original game systems that must be emulated, and explains where the current mod implementation is complete or still pending.

## Offline-only Startup Plan

The mod currently targets local split-screen play only. Online EOS/Steam services are not supported for startup or custom-server hosting in this branch.

- Goal: let the player start a local offline split-screen session without invoking EOS or the game's native online lobby path.
- Keep only the FishNet local host startup path in `OfflineModeManager`.
- Suppress EOS boot-time initialization by patching `PlayEveryWare.EpicOnlineServices.PlatformManager` static initialization and removing `_Scripts.Boot.EOSAuthenticator` from `BootSceneManager` bootables.
- Prevent offline custom server startup from invoking `_Scripts.Managers.LobbyManager.CreateLobby`.
- Document the offline-only constraint in the networking/docs layer and in the local server UI status.
- Verify with a boot/test run that `LogEOS`/EOS lobby creation logs do not appear when the mod starts and when the player selects `START CUSTOM SERVER`.

### EOS removal — new plan (2026-04-30)

The patch set in [SledCoopMod/src/Patches/EOSPatches.cs](src/Patches/EOSPatches.cs)
covers the right targets but currently never applies, because PlayEveryWare /
EOS / FishyEOS plugin assemblies load lazily *after* `Plugin.Load`. The plan,
in priority order:

1. **Deferred patcher (BLOCKING).** Subscribe to `AppDomain.AssemblyLoad` in
   `Plugin.Load`. When `PlayEveryWare.*`, `EOSSDK*`, `Epic.OnlineServices*`,
   or `*FishyEOS*` loads, invalidate `PatchHelpers`'s type cache and re-run
   the EOS-tagged patch classes. Tag those classes with a marker attribute
   (`[SledCoopEosPatch]`).
2. **`SledCoopLobbyShim`** replaces `_Scripts.Managers.LobbyManager` for the
   offline branch — captures `Instance`, returns safe non-null sentinels for
   `GetCurrentLobby` / `GetSearchResults`, no-ops `LobbyHeartbeat` and
   `UILobbyExplorer`. See
   [docs/eos_disable_plan.md §Replacement: SledCoopLobbyShim](docs/eos_disable_plan.md#replacement-sledcooplobbyshim-replaces-native-_scriptsmanagerslobbymanager).
3. **Transport swap** — at `OfflineModeManager.StartOfflineLocalGame`, swap
   FishyEOS for FishNet's bundled `Tugboat` (TCP loopback) on the live
   `NetworkManager`. Disable the FishyEOS component. Without this the
   loopback host still flows every packet through `FishyEOS:IterateIncoming`
   regardless of patch state.
4. **Hardening sweep** — see
   [docs/eos_disable_plan.md §Hardening sweep](docs/eos_disable_plan.md#hardening-sweep--il2cpp-aot-safe-pattern-audit).
   Active offender the latest log proves: `Patch_CharacterCanvas_GuestBind`
   at [src/Patches/PlayerSocialPatches.cs:122](src/Patches/PlayerSocialPatches.cs#L122)
   and [:154](src/Patches/PlayerSocialPatches.cs#L154) — calls
   `GetComponents<Behaviour>()` which is **not** in this AOT image and
   throws every spawn.
5. **Re-enable `FishNetOwnershipPatcher`** behind a config flag once 1–4
   land and the post-spawn freeze is gone.

## Mod Architecture (current state)

### Persistent singletons (DontDestroyOnLoad)

```
ModBootstrap (MonoBehaviour — root host for all singletons)
  ├── LocalPlayerManager    — slot registry (slots 0–3); GetSlot(), GetSlotForPawn()
  ├── InputRouter           — gamepad join/leave detection; assigns LocalInputProvider per slot
  ├── CameraLayoutManager   — split-screen viewport rects; EnsureCameraForSlot(), RefreshLayout()
  ├── HudManager            — clone HUD canvases + viewport binding; RefreshHuds()
  ├── SceneWatcher          — IsInGameplayScene flag; fires GameplayPatcher.Apply/Remove and
  │                           FishNetOwnershipPatcher.Apply/Remove on session start/end;
  │                           TODO: subscribe to SceneManager.sceneLoaded for clone re-spawn
  ├── LocalCoopUI           — IMGUI overlay (badges, inventory, settings, pause)
  └── GuestSaveManager      — per-slot JSON save (stats, fish) for guests (slots 1–3)
```

### Per-pawn components (attached to each `SledCoopP{n}` clone GO)

```
LocalPlayer         — slot back-reference, cleanup on Destroy
LocalCoopMovement   — physics movement driven by LocalInputProvider
LocalCoopActions    — game actions (sled, snowball, build, markers) via RpcLogic___ reflection
GuestRacingTracker  — local substitute for suppressed PlayerRacingController
```

### Gameplay-session patch families

Applied via `GameplayPatcher.Apply()` when `SceneWatcher.NotifyGameplayStarted()` fires; removed via `GameplayPatcher.Remove()` when `NotifyGameplayEnded()` fires. Not active during Boot to avoid IL2CPP trampoline overhead.

```
Patch_PlayerControl_*               — OnEnable/OnDisable/Start/Update/FixedUpdate/LateUpdate
Patch_PlayerRagdollTypeHandler_Awake
Patch_ClonePawnScriptSuppressor     — full lifecycle suppression (Awake→LateUpdate + FishNet
                                      callbacks) for all passive conflicting types
Patch_CloneGameplayOnlySuppressor   — gameplay-only suppression (Awake→LateUpdate only) for
                                      PlayerHoldingController and PlayerBuildingController;
                                      FishNet callbacks (OnStartServer/OnStartClient/…) ALLOWED
                                      so they initialize properly for ServerManager.Spawn calls
FishNetOwnershipPatcher             — postfixes IsOwner/IsServerInitialized/IsClientInitialized
                                      on NetworkBehaviour + NetworkObject to return true for
                                      any GO named "SledCoopP*"
```

### Always-active patch families (applied at BepInEx PatchAll)

```
Patch_PlayerControl_Awake           — detects host pawn; queues for next-frame processing
Patch_Canvas_Awake / UIPanel_Awake  — detects HUD canvas root (UIHUD itself has no Awake in this build)
Patch_Camera_OnEnable               — detects gameplay camera
Patch_LobbySettings_ResetStatics    — resets mod state on return to menu
Patch_BootSceneManager_Init         — fires SpawnHooks.OnBootComplete after game boot
Patch_PlayerLocalInput_Get*         — routes move/look/jump/interact/inventory/pause input
                                      to per-slot LocalInputProvider; blocks gamepad bleed to P1
Patch_UIPausePanel_*                — guarded probes; the type exists but exposes no
                                      OnEnable/OnDisable in interop, so these silently no-op
Patch_UiReferenceController_Update  — re-asserts cursor state when mod settings overlay is open
Patch_FishNetNetworkBehaviour_      — finalizer that swallows + throttle-logs benign teardown
  OnStopClient                        NullReferenceExceptions during scene unload
```

### IL2CPP-safe reflection contract

All patch `TargetMethod()` calls go through `PatchHelpers.SafeTypeByName` (cached lookup,
built once from the safe assemblies — UnityEngine modules and `__Generated` are skipped
because their `Type[] GetTypes()` throws `ReflectionTypeLoadException` in this Wine/IL2CPP
build) and `PatchHelpers.FindMethod` (`Type.GetMethod` + declared/inherited fallback —
never `AccessTools.Method`, which on miss runs the same offending assembly sweep).

This is mandatory: every uncached `AccessTools.TypeByName` or `AccessTools.Method` call on
a missing name produced 27+ HarmonyX `ReflectionTypeLoadException` warnings with stack
traces, and the mod probes ~20 optional types at startup. See
[docs/runtime_hardening.md](docs/runtime_hardening.md) for the full inventory.

---

## Known Gaps / Full-Support Checklist

- [x] FishNet `NetworkObject` registered with `ServerManager.Spawn(nob, null)` (server-owned) via `TryNetworkSpawnPawn` in `LocalPlayerSpawner`. Falls back to local-only (NetworkObject removed from hierarchy) when server is not running.
- [x] Two-tier suppressor: `Patch_ClonePawnScriptSuppressor` (full suppression including FishNet callbacks) for passive types; `Patch_CloneGameplayOnlySuppressor` (gameplay-only — FishNet callbacks ALLOWED) for `PlayerHoldingController` and `PlayerBuildingController` so their `OnStartServer/OnStartClient` initialize properly, enabling `ServerManager.Spawn` inside `RpcLogic___` calls.
- [x] `FishNetOwnershipPatcher` patches `IsOwner`, `IsServerInitialized`, `IsClientInitialized` → `true` for `SledCoopP*`; applied/removed with gameplay session to avoid Boot overhead.
- [x] `DestroyPawnGO` calls `ServerManager.Despawn(nob)` when clone is network-spawned, falls back to `UObject.Destroy` otherwise.
- [x] `CharacterPoints` wired to GuestSaveManager (`Patch_CharacterPoints_AddPoints` in PointsPatches.cs)
- [x] `PlayerSavedStats`, `AchievementController`, `CharacterModelGeneralized`, `PlayerAccessoryController`, `UIControllerIconsManager` suppressed on clone pawns — prevents P1 save data leaking into/from clones.
- [x] Snowball projectile for guest slots — networked: `PlayerHoldingController.RpcLogic___Cmd_ThrowObject___` calls `ServerManager.Spawn` for a real FishNet snowball; offline fallback: `LocalSnowball` proxy Rigidbody sphere.
- [x] Building placement for guest slots — networked: `PlayerBuildingController` FishNet initialization enables `BuiltObjectManager` server spawning via `RpcLogic___`; offline: local-only.
- [x] Race checkpoints extended to guests via `GuestRacingTracker` + `Patch_RaceCheckpoint_TriggerEnter`
- [x] HUD clones: `UIHUD_Points` rebound to guest `CharacterPoints` via `Patch_UIHUD_Points_Bind`
- [x] Guest username: `Patch_PlayerUsernameController_GuestBind` sets backing username field + walks child Text/TMPro for in-world nameplate
- [x] `CharacterCanvas` world-space nameplate rebound to guest username via `Patch_CharacterCanvas_GuestBind`
- [x] Race leaderboard probe via `Patch_RaceLeaderboard_GuestData` — logs guest lap/CP alongside native results
- [x] Build item type picker in per-slot IMGUI inventory overlay (cycle with ◄/►)
- [x] Race lap/checkpoint progress in per-slot IMGUI status badge and inventory overlay
- [x] Guest username shown in IMGUI status badge (non-default names; e.g. "P2 (Alice): Playing")
- [ ] Voice chat (`DissonanceVoip`) wired per guest slot
- [ ] EOS/Steam party presence for local guests

### Remaining hard gaps / known limitations

- **Sled physics object** — `PlayerSledController.RpcLogic___Cmd_Sled___` is now reachable (clone has valid `_networkObject`, `IsOwner` returns true). The call path should produce a real FishNet sled NetworkObject when networked, but this has **not yet been confirmed end-to-end**. Visual sled cube (local fallback via `LocalCoopActions`) is the active behavior. See [docs/custom_server_implementation.md#sled-physics-gap](docs/custom_server_implementation.md) for verification steps.
- **`PlayerRacingController`** — native networked race tracking replaced locally by `GuestRacingTracker`; full networked tracking remains a gap.
- **Scene transitions** — `SceneWatcher` now triggers `RecheckCloneRegistrations` via Update polling (the `sceneLoaded` subscription fails in this IL2CPP build). Clone re-registration on scene load is implemented; end-to-end testing across an additive scene load is still needed.
- **Voice chat (`DissonanceVoip`)** — hard gap; requires per-slot Dissonance wiring.
- **EOS/Steam party presence** for local guests — hard gap.
- **Per-guest cosmetic selection** (`PlayerSavedStats` / `CharacterModelGeneralized`) — clones share host's default model.

---

## Bug Fix Log & Implementation Roadmap

### Session: 2026-04-30 — EOS still booting; post-spawn freeze; hardening targets

Inputs: [docs/game_logs/LogOutput.log](docs/game_logs/LogOutput.log) (153 lines)
and [docs/game_logs/Player.log](docs/game_logs/Player.log).

#### Findings

1. **EOS suppression patches don't apply.** `Plugin.Load` reports
   `Harmony: 26 patch(es) applied, 21 skipped.` The 21 skipped are the
   `Patch_PlayEveryWare*` / `Patch_EOSManager_*` / `Patch_FishyEOS_*`
   classes. Root cause: `PatchHelpers.SafeTypeByName` caches against
   `AppDomain.CurrentDomain.GetAssemblies()` on first call, but the EOS
   plugin assemblies aren't loaded yet — they appear later from the Boot
   scene. Every EOS `TargetMethod()` returns `null`; HarmonyX skips the
   class. Player.log then shows EOS booting in full
   (`EOS_Initialize`, `EOS_Platform_Create`, EOSAuth `TokenGrant`, RTC
   init, FishyEOS-as-active-transport).
2. **Post-spawn freeze.** LogOutput ends at `[ModBootstrap] post-spawn tick
   1 (frame=19022).` followed by `[HostPatches]
   Server_CheckForFalselyJoinedPlayers skipped (custom server active).` — no
   subsequent heartbeats. Spawn itself (`OnHostPawnSpawned`) completed
   cleanly. Since EOS is fully booted and FishyEOS is the active transport,
   the most likely explanation is a blocking call inside the unsuppressed
   EOS / RTC stack on the first post-spawn tick. We can't isolate the exact
   call until the deferred patcher (finding 1) lands.
3. **`Patch_CharacterCanvas_GuestBind` throws every spawn.**
   [src/Patches/PlayerSocialPatches.cs:122](src/Patches/PlayerSocialPatches.cs#L122)
   calls `probe.GetComponents<Behaviour>()` directly. This AOT image is
   missing the parameterless `<Behaviour>` overload — visible in LogOutput
   line 27 (the AOT probe in `LocalPlayerSpawner.PreResolve`) and again at
   line 148. The exception is caught but the call still throws on every
   spawn and risks IL2CPP trampoline corruption on the next IL2CPP call
   (per the 2026-04-29 fix-up session).

#### Plan

See [NEW_PLAN.md](NEW_PLAN.md) for the working order; high level:

- Phase A — deferred EOS patcher (`AppDomain.AssemblyLoad` listener,
  `[SledCoopEosPatch]` marker, `PatchHelpers.InvalidateCache()`).
- Phase B — `SledCoopLobbyShim` replaces native `_Scripts.Managers.LobbyManager`.
- Phase C — Tugboat transport swap on the live `NetworkManager`.
- Phase D — defence in depth: native EOS DLL load block via the existing
  `EOSManager.LoadEOSLibraries` patch (will work once Phase A lands), with
  a BepInEx preloader patcher as a hard fallback.
- Phase E — IL2CPP-AOT hardening sweep.
  Concrete first action: gate
  `Patch_CharacterCanvas_GuestBind` behind a self-disabling probe that
  caps at 3 failures and then disables the TextMeshPro scan path entirely.
  Then audit every other generic-overload callsite — see
  [docs/eos_disable_plan.md §Hardening sweep](docs/eos_disable_plan.md#hardening-sweep--il2cpp-aot-safe-pattern-audit).
- Phase F — re-enable `FishNetOwnershipPatcher` behind a config flag.

### Session: 2026-04-29 (test, results) — Spawn frame is fine; freeze is on the next tick

The instrumentation worked and definitively answered the question:

```
[OnHostPawnSpawned] WireAllCameraFollowers returned; starting RefreshHuds.
[OnHostPawnSpawned] complete.
```

`OnHostPawnSpawned` returns cleanly.  No `Heartbeat frame=1800`, no
`[FishNetOwnershipPatcher] Gameplay ownership patches applied.` log → the
freeze is on the *next Unity tick*, not on the spawn frame itself.

The prime suspect is `FishNetOwnershipPatcher.Apply()` — fired ~5 frames
after `NotifyGameplayStarted` from `SceneWatcher.Update`.  It patches
FishNet's `IsOwner` / `IsServerInitialized` / `IsClientInitialized` getters
that are called hundreds of times per tick by FishNet's own coroutines.
Each patched call goes through a Harmony trampoline + per-call
`ReflectionHelper.GetGameObject(__instance)` (reflection through IL2CPP
property accessor) + `go.name.StartsWith("SledCoopP")` check.  Under
IL2CPP/Wine that is the most plausible source of either an instant freeze
during the patch apply (stripped overload mid-`Harmony.Patch`) or a death-
spiral on the first frame after apply.

Changes this session:

- `SceneWatcher.NotifyGameplayStarted` no longer arms the deferred
  `FishNetOwnershipPatcher.Apply()` — `_fishNetOwnershipDisabled = true`
  with an explanatory log line.  The default delay is also bumped to 600
  frames if/when the flag is flipped back on for re-investigation.
- The patch was only required for the *networked* sled / snowball /
  building-spawn path on guest pawns; the local-only fallback path remains
  in place, so disabling this just means those features don't yet produce
  real FishNet `NetworkObject`s — the visual sled cube and `LocalSnowball`
  proxy still work.
- `ModBootstrap.Update` now logs a per-tick heartbeat for the first 30
  ticks after `IsInGameplayScene` flips true.  If the freeze persists
  even with `FishNetOwnershipPatcher` disabled, the per-tick log will
  show *which* post-spawn tick the freeze occurs on (or whether Update
  is running at all).

### Session: 2026-04-29 (test) — Diagnostic instrumentation past clone-spawn

The previous "latest" session got the spawn flow much further: host pawn,
slot 1 clone (`SledCoopP1`, `ObjectId=40677`, server-owned), slot 1 camera
(`SledCoopCam_Slot1`, viewport 0.5-1.0), and the camera-follower wire all
completed cleanly.  But the log still stops at the second
`SpawnHooks: wired camera 'SledCoopCam_Slot1' → pawn 'SledCoopP1' for slot 1.`
with no further heartbeats — the freeze moved past `WireAllCameraFollowers`
but is still on or near the spawn frame.

The next call after that line is `HudManager.RefreshHuds()` (which should be
a no-op because `SourceHudCanvas` is null), then `OnHostPawnSpawned` returns
and Unity continues the frame.  The candidates are:

1. `RefreshHuds()` itself (uncatchable IL2CPP failure inside one of the
   property accesses or `LocalPlayerManager.ActiveSlots` LINQ enumeration).
2. The 5-frame-deferred `FishNetOwnershipPatcher.Apply()` from
   `SceneWatcher.Update` — patches FishNet ownership getters for clone-name
   targets.  Could blow up on AccessTools.TypeByName scan or trampoline
   conflict.
3. Unity's next render frame trying to draw the new split-screen viewports
   while the host pawn is still half-initialised under FishNet.

Changes this session:

- Added `[OnHostPawnSpawned]` log lines around `RefreshLayout`,
  `WireAllCameraFollowers`, `RefreshHuds`, and a final `complete.` so the
  next test log narrows the freeze to one specific call.
- Added a defensive early-bail in `HudManager.RefreshHuds` for the
  `SourceHudCanvas == null && _hostCanvases.Count == 0 && _clonedCanvases.Count == 0`
  case (true on every spawn frame because the HUD scan is deferred), so
  the spawn-frame call definitively no-ops without iterating ActiveSlots.

Next test log should show one of:

- `[OnHostPawnSpawned] starting RefreshHuds.` then no `complete` →
  RefreshHuds is the freeze; investigate further inside HudManager.
- `[OnHostPawnSpawned] complete.` then no further heartbeats →
  the freeze is on the next Unity tick (most likely
  `FishNetOwnershipPatcher.Apply` or render).  Defer / instrument those.
- `[OnHostPawnSpawned] complete.` followed by heartbeats →
  the spawn frame was always fine; the apparent freeze was perceptual.

### Session: 2026-04-29 (latest) — Spawn-frame freeze in HUD scan

The previous "fix-up" let the plugin load and the AOT probe correctly reported
`GetComponents<Behaviour>()` as **also missing** in this build (only
`GetComponent<T>()` is present).  Host pawn spawning then froze again, with
the log stopping at `SpawnHooks: registered host camera 'Main Camera'.` —
i.e. inside the synchronous `TryScanForHudCanvas` call that follows.

Two reinforcing causes:

1. The manual `WalkForHudCanvas` recursion is too expensive on Main Mountain
   Scene (thousands of GameObjects, per-call IL2CPP trampoline cost).  Even if
   no overload is missing, the spawn frame deadlocks.
2. `GetSceneRootGameObjects` uses reflection that may hit another stripped
   `Scene.GetRootGameObjects` overload — every reflection-based fallback we
   add becomes a freeze candidate on this build.

Fix:

- `TryScanForHudCanvas` reduced to a single
  `Object.FindObjectOfType<Canvas>()` call (the deprecated overload — the
  only Canvas finder confirmed present in the AOT image) plus the existing
  WorldSpace / nameplate filter in `ConsiderHudCanvas`.  No recursion, no
  reflection, no `MakeGenericMethod`.
- The `WalkForHudCanvas` helper and the reflection Strategy 2 are deleted.
- `OnHostPawnSpawned` no longer calls `TryScanForHudCanvas` synchronously.
  Instead it sets `HudManager.SuppressScanUntilFrame = frameCount + 300` (~5 s).
- The `LocalCoopUI` polling path checks that flag before calling
  `TryScanForHudCanvas`, so the first scan happens 5 s after gameplay starts
  and runs every second thereafter — outside the spawn frame, where any
  failure is non-fatal.
- `ConsiderHudCanvas` also gates the `ScanAndRegisterAllGameCanvases` call
  on `SuppressScanUntilFrame` (the all-canvas walk uses the same fragile
  scene-root reflection).
- The Awake-time patches (`Patch_UIPanel_Awake`, `Patch_Canvas_Awake`)
  remain the primary HUD detection path; the polling scan is just a
  fallback for canvases that already exist when our patches load.

### Session: 2026-04-29 (night, fix-up) — Plugin failed to load

The "night" probe also failed: BepInEx reported

```
[Error : BepInEx] Error loading [SledCoopMod 0.1.0]:
  System.MissingMethodException: Method not found:
  '!!0[] UnityEngine.GameObject.GetComponents()'.
   at SledCoopMod.LocalPlayerSpawner.PreResolve()
```

Root cause: the IL2CPP runtime resolves every generic instantiation referenced
in a method body at *method invocation time* — *before* the method's `try`
block is entered. PreResolve referenced both `<Behaviour>` and `<Component>`,
and the missing `<Component>` overload tore the whole method down before the
catch could fire.

Fix:

- All references to `GetComponents<Component>()` (and any other unverified
  generic GameObject overload) removed from source.  PreResolve now only
  references `GetComponents<Behaviour>()`, isolated inside a dedicated helper
  (`TryProbeGetComponentsBehaviour`) so the binder only resolves it when the
  helper is invoked.
- Documented the rule in `docs/runtime_hardening.md` §5: never reference an
  unverified generic overload anywhere in source, even in unreachable paths.

### Session: 2026-04-29 (night) — AOT-overload probe + manual canvas walk

The "late" session swapped `GetComponent(Type)` for `GetComponents<Component>()`
and `FindObjectsByType` for `GetComponentsInChildren<Canvas>(true)`.  Both
replacements are *also* missing from this AOT image, so the freeze persisted.
This session's two findings:

- [x] **`GameObject.GetComponents<Component>()` and
       `GameObject.GetComponentsInChildren<T>(bool)` are stripped from this AOT**
      (`src/LocalPlayerSpawner.cs`, `src/SpawnHooks.cs`)
  - `GetComponents<Behaviour>()` *is* present (used at runtime by
    `PlayerSocialPatches.cs`), but no other generic-component-array overload
    is guaranteed.
  - Catching the `MissingMethodException` at the call site is **not enough** —
    an IL2CPP-side trampoline corruption persists past the catch and the next
    unrelated IL2CPP call (we observed `AddComponent<Camera>` for
    `SledCoopCam_Slot1`) hangs the main thread.  The freeze appears one or
    two log lines later than the actual offending call, hiding the cause.
  - Fix:
    - `LocalPlayerSpawner.PreResolve()` runs an AOT probe at plugin load on
      a throwaway GameObject and stores `_hostFieldCopyEnabled`.  If either
      `GetComponents<Behaviour>()` or `GetComponents<Component>()` throws,
      `TryCopyHostComponentFields` is skipped entirely on every spawn — no
      exception ever reaches the trampoline mid-spawn.
    - `Plugin.Load` now calls `LocalPlayerSpawner.PreResolve()` alongside
      the other `PreResolve` calls.
    - `SpawnHooks.TryScanForHudCanvas` Strategy 1 was rewritten to use a
      manual `Transform.GetChild(int)` recursion + per-node
      `GetComponent<Canvas>()` (the `WalkForHudCanvas` helper).  This avoids
      every `GetComponentsInChildren` overload completely.
    - `LocalPlayerSpawner.TryCopyHostComponentFields` itself now uses
      `GetComponents<Behaviour>()` (probe-validated) and resolves each target
      type via `IsAssignableFrom` in managed code.

### Session: 2026-04-29 (late) — Host-spawn freeze (uncatchable IL2CPP MissingMethod)

The previous session's WorldSpace-canvas filter exposed two further freeze paths
that were always present but masked by it failing earlier.

- [x] **`GameObject.GetComponent(Type)` is uncatchable in this interop**
      (`src/LocalPlayerSpawner.cs`)
  - From `LogOutput.log`:
    ```
    SpawnPawnForSlot failed for slot 1: Method not found:
      'UnityEngine.Component UnityEngine.GameObject.GetComponent(System.Type)'.
       at LocalPlayerSpawner.TryCopyHostComponentFields
    ```
  - The exception was logged as Error so it *looked* handled, but the trampoline
    state was corrupted; the very next IL2CPP call (`AddComponent<Camera>` for
    `SledCoopCam_Slot1`) hung the main thread.
  - Fix: `TryCopyHostComponentFields` now calls `host.GetComponents<Component>()`
    and `clone.GetComponents<Component>()` once each (compile-time generic, safe)
    and resolves each desired type via `IsAssignableFrom` in managed code.  Added
    `FindComponentOfType(Component[], Type)` helper.

- [x] **`Object.FindObjectsByType<T>(FindObjectsSortMode)` not in interop**
      (`src/SpawnHooks.cs`)
  - From `LogOutput.log`:
    ```
    SpawnHooks: HUD canvas scan threw: Method not found:
      '!!0[] UnityEngine.Object.FindObjectsByType(UnityEngine.FindObjectsSortMode)'.
    ```
  - Introduced in the previous "WorldSpace filter" fix as Strategy 1 — turned out
    the parameter-bearing `FindObjectsByType` overload is missing in this build.
  - Fix: Strategy 1 now walks the active scene's root GameObjects (via the
    already-proven `GetSceneRootGameObjects`) and calls
    `GetComponentsInChildren<Canvas>(true)` on each root, then applies the
    same WorldSpace / mod / nameplate filter.

### Session: 2026-04-29 (evening) — Host-spawn freeze (WorldSpace HUD)

- [x] **Wrong canvas accepted as the host HUD**
      (`src/SpawnHooks.cs`, `src/HudManager.cs`)
  - From `LogOutput.log`: the last line before the freeze was
    `HudManager: registered host HUD '(Canvas) Character Canvas' for slot 0.`
    `(Canvas) Character Canvas` is the per-pawn **WorldSpace** nameplate, not
    the screen-space HUD.  `Object.FindFirstObjectByType<Canvas>()` returned
    it first because it was on the just-Instantiated host pawn.
  - `HudManager.RefreshHuds` then ran `c.renderMode = ScreenSpaceOverlay` on
    that WorldSpace canvas while it was still mid-Awake under the host pawn's
    hierarchy → main thread stopped (no further heartbeat frames logged).
  - Fix:
    - `SpawnHooks.ConsiderHudCanvas` rejects `RenderMode.WorldSpace` and
      canvases whose name contains `character canvas`/`nameplate`/`playerlabel`.
    - `SpawnHooks.TryScanForHudCanvas` Strategy 1 now walks
      `FindObjectsByType<Canvas>()` and picks the first non-WorldSpace,
      non-mod, non-nameplate candidate instead of returning the first object.
    - `SpawnHooks.ScanAndRegisterAllGameCanvases` skips WorldSpace canvases
      so the per-pawn nameplate never enters `_hostCanvases`.
    - `HudManager.RefreshHuds` defensively never flips a WorldSpace canvas's
      renderMode even if one is somehow registered.

### Session: 2026-04-29 (pm) — Startup log hygiene + scene-loaded crash

Inputs: `LogOutput.log` from a clean `Boot → Main Mountain Scene` start with P2 joining
slot 1 before the host pawn spawned.

#### Fixed this session

- [x] **`Patch_FishNetNetworkBehaviour_OnStopClient` flooding** (`src/Patches/DiagnosticPatches.cs`)
  - Added static throttle counters (`_swallowCount`, `_swallowCountLastLogged`).
  - Finalizer now logs at `Debug` level only on the first swallow and every 300th.
  - Removed expensive `ReflectionHelper.GetGameObject` call from the hot path.
  - Result: log drops from ~1000 lines/sec to ≤3 lines per teardown burst.

- [x] **`SceneWatcher` clone-recheck never firing** (`src/SceneWatcher.cs`)
  - `SceneManager.sceneLoaded += OnSceneLoaded` throws `MissingMethodException`
    (`UnityAction\`2..ctor(System.Object, IntPtr)` is missing in this Il2CppInterop
    build).  The exception escapes the local try/catch at the trampoline boundary.
  - Fix: subscription removed entirely; `Update()` already polls `SceneManager
    .GetActiveScene().name` and arms `_recheckCloneFrame = frameCount + 1` on
    every name change, so behaviour is preserved.

- [x] **`HarmonyX` `ReflectionTypeLoadException` storm** (`src/Patches/DiagnosticPatches.cs`)
  - Every `AccessTools.TypeByName(name)` lookup that misses falls back to a full
    `GetTypesFromAssembly` sweep over every loaded assembly.  In this build,
    `UnityEngine.CoreModule`, `UnityEngine.PhysicsModule`,
    `UnityEngine.UIElementsModule`, `UnityEngine.ParticleSystemModule`,
    `UnityEngine.VirtualTexturingModule`, and `__Generated` all throw
    `ReflectionTypeLoadException` on enumeration, producing 27+ warning lines
    per lookup.  The mod probes ~20 optional types at startup → ~3000 warning
    lines and several hundred ms of GC pressure.
  - Fix: added `PatchHelpers.SafeTypeByName(name)` — a cached dictionary built
    once from `AppDomain.CurrentDomain.GetAssemblies()`, skipping
    `UnityEngine.*` and `__Generated`.  All patch `TargetMethod()` callsites
    were rewritten to use it (and `PatchHelpers.FindMethod`, which uses
    `Type.GetMethod` directly — no scan).

- [x] **"Skipped Patch_X" warnings on every optional patch**
  - When `TargetMethod()` returned null (target type/method absent),
    HarmonyX raised a `Patching exception` and `Plugin.Load` logged it at
    Warning level.  These are expected when the mod probes UI types (`UIHUD`
    Awake, `UIPausePanel` OnEnable/OnDisable, race UI variants, etc.) and
    should not surface to the player.
  - Fix: the catch in `Plugin.Load` now logs at `Debug`.  The aggregate count
    is still reported at Info as `Harmony: N applied, M skipped.`

- [x] **`SourceCameraPrefab not available` warnings on early P2 join**
  - `InputRouter` lets P2 join slot 1 before the host pawn (and host camera)
    exist, so `EnsureCameraForSlot` and `RefreshLayout` correctly defer.  The
    log line was at Warning, suggesting an error.
  - Fix: `CameraLayoutManager.RefreshLayout`, `EnsureCameraForSlot`, and
    `SpawnHooks.WireCameraFollower` now emit Debug rather than Warning when
    the prefab is not yet set.  `OnHostPawnSpawned` already retries the
    full layout/wire/HUD sequence after the host camera is registered.

#### Pre-existing fixes from earlier session

### Session: 2026-04-29 — Freeze / log-flood fixes

#### Root cause analysis (from `LogOutput.log`)

The game was not actually frozen — it did transition to Main Mountain Scene and start gameplay. The perceived freeze was caused by `Patch_FishNetNetworkBehaviour_OnStopClient` firing 500–1000+ times per second during FishNet's scene-teardown phase, generating catastrophic log I/O and GC pressure on the main thread.

Three bugs were identified and fixed:

#### Fixed this session

- [x] **`Patch_FishNetNetworkBehaviour_OnStopClient` flooding** (`src/Patches/DiagnosticPatches.cs`)
  - Added static throttle counters (`_swallowCount`, `_swallowCountLastLogged`).
  - Finalizer now logs at `Debug` level only on the first swallow and every 300th.
  - Removed expensive `ReflectionHelper.GetGameObject` call from the hot path.
  - Result: log drops from ~1000 lines/sec to ≤3 lines per teardown burst.

- [x] **`SceneWatcher` clone-recheck never firing** (`src/SceneWatcher.cs`)
  - `SceneManager.sceneLoaded` subscription fails in this IL2CPP build (`UnityAction<Scene,LoadSceneMode>` constructor is missing).
  - Fix: Update() scene-change polling now sets `_recheckCloneFrame = frameCount + 1` when `IsInGameplayScene` and the scene name changes.
  - `RecheckCloneRegistrations` now runs on every scene transition, not just when the broken event fires.

- [x] **Guest camera sequencing (slot 1 never gets a camera)** (`src/SpawnHooks.cs`)
  - If `SpawnExtraLocalPlayers` throws or degrades during the exception storm, `slot.Pawn` may be null when the caller's `RefreshLayout` runs — skipping camera creation.
  - Fix: `SpawnPawnForSlot` now calls `RefreshLayout()`, `WireCameraFollower()`, and `RefreshHuds()` immediately after `slot.Pawn = pawnClone`, before the finally block. These calls are idempotent with the caller-side calls.

#### Remaining checklist — full functionality roadmap

| Priority | Item | File(s) | Notes |
|----------|------|---------|-------|
| High | Verify sled physics end-to-end | `LocalCoopActions.cs`, `LocalCoopMovement.cs` | `RpcLogic___Cmd_Sled___` is reachable; live sled NetworkObject spawn unconfirmed |
| High | Scene-transition clone survival test | `SceneWatcher.cs`, `SpawnHooks.cs` | `RecheckCloneRegistrations` now wired; needs live test with an additive level load |
| Medium | Per-guest cosmetic selection | `LocalPlayerSpawner.cs`, `SpawnHooks.cs` | `PlayerSavedStats`/`CharacterModelGeneralized` suppressed; clone uses host model |
| Medium | Networked `PlayerRacingController` | `RacePatches.cs`, `GuestRacingTracker.cs` | `GuestRacingTracker` is the local substitute; full networked leaderboard integration pending |
| Medium | Race leaderboard display per guest | `RacePatches.cs`, `LocalCoopUI.cs` | `Patch_RaceLeaderboard_GuestData` logs data; UI display not yet implemented |
| Low | Voice chat (`DissonanceVoip`) | N/A | Hard gap; no safe IL2CPP path identified |
| Low | EOS/Steam party presence for guests | N/A | Platform SDK constraint; hard gap |
