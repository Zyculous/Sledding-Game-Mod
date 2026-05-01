# SledCoopMod — Active Plan & Status (2026-04-30)

> **Architecture pivot:** the preferred direction is now the networked
> multi-instance rework, where extra local players are separate FishNet client
> processes instead of `SledCoopP*` clones. See
> [docs/networked-instance-rework/README.md](docs/networked-instance-rework/README.md).

> **Build status:** Phases A (deferred EOS patcher) and E1 (AOT
> hardening for `Patch_CharacterCanvas_GuestBind`) implemented and
> compiled clean. New files: [src/SledCoopEosPatchAttribute.cs](src/SledCoopEosPatchAttribute.cs),
> [src/Aot.cs](src/Aot.cs), [src/EosLatePatcher.cs](src/EosLatePatcher.cs).
> Updated: [src/Plugin.cs](src/Plugin.cs),
> [src/Patches/DiagnosticPatches.cs](src/Patches/DiagnosticPatches.cs),
> [src/Patches/EOSPatches.cs](src/Patches/EOSPatches.cs),
> [src/Patches/PlayerSocialPatches.cs](src/Patches/PlayerSocialPatches.cs).
> Awaiting in-game test for verification.

Quick reference for the current state of the mod and the next blockers.
**Source-of-truth detail** lives in [IMPLEMENTATION_GUIDE.md](IMPLEMENTATION_GUIDE.md)
and [docs/eos_disable_plan.md](docs/eos_disable_plan.md). This file is the
checklist; cross-reference those for *why* and *how*.

---

## 1. Current runtime state (from [docs/game_logs/LogOutput.log](docs/game_logs/LogOutput.log))

What works:

- Plugin loads, `26 patches applied / 21 skipped`, type cache built (63062 entries from 179 assemblies).
- AOT probe correctly disables host-field-copy (parameterless `GameObject.GetComponents<T>()` is missing in this AOT image).
- Splash → Boot → `Main Mountain Scene` transitions cleanly.
- `OfflineModeManager.StartOfflineLocalGame` succeeds: server starts, client connects to loopback (full host mode).
- Slot 1 (`SledCoopP1`) clone spawns, NetworkObject removed, camera created (`SledCoopCam_Slot1`), follower wired, HUD refresh runs.
- `OnHostPawnSpawned` completes; `LocalCoopMovement` initialises with `Gamepad0` provider and the existing root Rigidbody.

What's broken:

- **Game freezes ~1 tick after spawn** — last log line is `[HostPatches] Server_CheckForFalselyJoinedPlayers skipped (custom server active).` (frame=19022, then no more heartbeats).
- **EOS still boots fully** — [docs/game_logs/Player.log](docs/game_logs/Player.log) shows `EOSSDK-Win64-Shipping.dll` loading, `EOS_Initialize` / `EOS_Platform_Create` succeeding, EOSAuth `TokenGrant` HTTP calls completing, RTC init, etc. The 21 skipped patches are predominantly the EOS suppressors — see §2.
- **`Patch_CharacterCanvas_GuestBind` throws every spawn**: `Method not found: '!!0[] UnityEngine.GameObject.GetComponents()'` at [SledCoopMod/src/Patches/PlayerSocialPatches.cs:122](SledCoopMod/src/Patches/PlayerSocialPatches.cs#L122) and [:154](SledCoopMod/src/Patches/PlayerSocialPatches.cs#L154). Caught, but it's noise per spawn.
- **`FishyEOS` is still the active transport** on `NetworkManager` (see Player.log tails: every server/client packet flows through `FishNet.Transporting.FishyEOSPlugin.FishyEOS`). The mod's loopback host therefore depends on a transport whose underlying EOS runtime is partially-initialised and partially-blocked.

---

## 2. Why EOS is still loading despite the patches

Read this carefully — it changes the whole approach.

`PatchHelpers.SafeTypeByName` builds its type-name cache **once, on first call**, from
`AppDomain.CurrentDomain.GetAssemblies()` (see [SledCoopMod/src/Patches/DiagnosticPatches.cs:60](SledCoopMod/src/Patches/DiagnosticPatches.cs#L60)).

Every `EOSPatches.cs` target uses `SafeTypeByName(...)`. At
`Plugin.Load` (BepInEx chainload), the only assemblies loaded are BepInEx, Unity,
and Assembly-CSharp. **The PlayEveryWare/EOS plugin assemblies and the FishyEOS
plugin are not yet loaded** — Unity loads them lazily when their MonoBehaviours
first deserialize from the boot scene.

Result: every `Patch_PlayEveryWare*`, `Patch_EOSManager_*`, `Patch_FishyEOS_*`
target resolves to `null`, Harmony skips the class, and the mod records
`21 skipped`. EOS then initialises normally because nothing is patching it.

This is the **single biggest unfixed bug** in the mod and the most likely
cause of the freeze (see §3).

---

## 3. Likely cause of the post-spawn freeze

Hypothesis (consistent with both logs):

1. EOS booted fully (Player.log: `Successfully created the EOS SDK Platform.`,
   then `TokenGrant` HTTP requests, RTC initialise, Stomp messaging connect).
2. FishyEOS transport is the live transport on the loopback `NetworkManager`.
   Its `IterateIncoming` / `IterateOutgoing` runs every tick.
3. Once `PlayerControl.OnStartClient` and `Dissonance` initialisation kick in,
   they re-enter EOS / RTC code paths that the mod *intended* to block but
   didn't (per §2). One of those calls blocks the main thread (Wine + EOS
   native overlay registry probe + RTC capture pipeline reset are all
   plausible candidates — Player.log shows `Detected a frame skip, forcing
   capture pipeline reset` and `OverlayPath registry key not found`).

Until §2 is fixed we can't isolate the exact freezing call: the suppression
patches need to actually apply before the EOS singleton is touched.

---

## 4. New plan summary (full detail in [docs/eos_disable_plan.md](docs/eos_disable_plan.md))

### Phase A — Fix patch-application timing (BLOCKING; do first)

- [x] **A1.** [src/EosLatePatcher.cs](src/EosLatePatcher.cs) subscribes to `AppDomain.CurrentDomain.AssemblyLoad`. When an assembly's name matches `PlayEveryWare`, `EpicOnlineServices`, `EOSSDK`, `Epic.OnlineServices`, or `FishyEOS` (case-insensitive substring), the type cache is invalidated and the tagged patch classes are re-attempted. Already-applied classes are tracked in a HashSet so re-fires don't double-patch. Initialize() also runs an immediate retry in case a target assembly is already in the AppDomain at hook time.
- [x] **A2.** `PatchHelpers.InvalidateCache()` clears the type-name dictionary, the missing-type set, and resets `_typeCacheBuilt` under a lock. ([src/Patches/DiagnosticPatches.cs:69](src/Patches/DiagnosticPatches.cs#L69))
- [x] **A3.** [`SledCoopEosPatchAttribute`](src/SledCoopEosPatchAttribute.cs) added; 24 lazy-loaded EOS targets in [src/Patches/EOSPatches.cs](src/Patches/EOSPatches.cs) now carry the marker. **Intentionally NOT tagged**: `BootSceneManager`, `EOSAuthenticator`, `ExternalAuthenticationManager`, `_Scripts.Managers.LobbyManager` (Assembly-CSharp targets — already loaded at Plugin.Load), and the `FishyEOS_*StartConnection_OfflineOnly` patches (would fake-success the loopback transport startup and break the working local host path).
- [ ] **A4.** Verify with the validation checklist in [docs/eos_disable_plan.md §Validation](docs/eos_disable_plan.md): boot logs must NOT contain `EOS_Initialize`, `EOS_Platform_Create`, `LogEOS(Boot)`, or `EOSAuthenticator:StartConnect`. **Run test required.**

### Phase B — Replace `_Scripts.Managers.LobbyManager` with a stub-and-singleton substitute

The native [LobbyManager.cs](Unity Files/AssetRipper_export_20260422_195824/ExportedProject/Assets/Scripts/Assembly-CSharp/_Scripts/Managers/LobbyManager.cs) holds an
`EOSLobbyManager` field, exposes `Lobby` / `LobbyDetails` / `LobbySearch` types
in its public surface, and is a `MonoBehaviour` referenced by `GameInfo`,
`UILobbyExplorer`, and `LobbyHeartbeat`. Suppressing each method (current state)
leaves `Instance == null` for any caller that reads it before our prefix runs.

- [ ] **B1.** Add `SledCoopLobbyShim` (new file [SledCoopMod/src/SledCoopLobbyShim.cs]). At first scene load, locate the `LobbyManager` GO, snapshot the `Instance` static, and force every method body to early-return with safe defaults (`null`, `false`, empty dictionaries). Implement via Harmony patch on the Awake → assign `Instance` so `GetCurrentLobby()`/`GetLobbyId()`/`GetSearchResults()` return non-null sentinels (empty `Dictionary`, sentinel `Lobby` with empty id, etc.) — every caller is generated stub code, so safe non-null is more compatible than null.
- [ ] **B2.** Patch `LobbyHeartbeat` constructor + `Update` to no-op (offline only). Already partially handled by `Patch_LobbyManager_UpdateLobbyHeartbeat_OfflineOnly`, but the `LobbyHeartbeat` Component itself runs independently — patch its `Awake` and `Update` directly.
- [ ] **B3.** Patch `_Scripts.UI.Pre_Game.UILobbyExplorer` Awake/Refresh to early-return when `OfflineModeManager.OfflineModeActive`, so the lobby browser UI doesn't try to call into the shim.
- [ ] **B4.** Patch `_Scripts.Managers.GameInfo` lobby-related properties to return mod-supplied values when offline.

### Phase C — Replace the FishyEOS transport with a loopback transport

- [ ] **C1.** At `OfflineModeManager.StartOfflineLocalGame`, before `ServerManager.StartConnection`, walk the `NetworkManager` GO components, find the `Transport` reference, and either:
    - **C1a (preferred)** swap to FishNet's bundled `Tugboat` (TCP loopback). It ships with FishNet so the type should already be in the AOT image — verify with `PatchHelpers.SafeTypeByName("FishNet.Transporting.Tugboat.Tugboat")`.
    - **C1b (fallback)** keep `FishyEOS` but Harmony-patch every `IterateIncoming` / `IterateOutgoing` to no-op when `OfflineModeActive`. This is fragile — prefer C1a.
- [ ] **C2.** Set the transport on `TransportManager` via reflection (`SetTransport` or its private backing field). Re-init the `NetworkManager`.
- [ ] **C3.** Confirm `Player.log` no longer shows `FishNet.Transporting.FishyEOSPlugin.FishyEOS:HandleClientReceivedDataArgs` after the swap.

### Phase D — Block the native EOS DLL load (defence in depth)

If A+B+C aren't sufficient:

- [ ] **D1.** Patch `PlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton.LoadEOSLibraries` and `LoadDynamicLibrary` (already in `EOSPatches.cs`, but they currently never apply — Phase A fixes that).
- [ ] **D2.** As a hard fallback, BepInEx preloader patcher to NOP the static-ctor of `PlatformManager` in IL before Unity loads the assembly. Document but don't implement unless A+D1 fail.

### Phase E — IL2CPP-AOT hardening sweep

The freeze hunt has uncovered several stripped overloads. The contract is in
[docs/runtime_hardening.md](docs/runtime_hardening.md). Fresh violations:

- [x] **E1.** Done. New central probe table: [src/Aot.cs](src/Aot.cs) (`Aot.GetComponentsBehaviourAvailable`), initialised from `Plugin.Load`. The TextMeshPro scan in `Patch_PlayerUsernameController_GuestBind` was refactored ([src/Patches/PlayerSocialPatches.cs](src/Patches/PlayerSocialPatches.cs)) to: (a) gate on `Aot.GetComponentsBehaviourAvailable`, (b) move the suspect generic call into an isolated helper `TrySetTextMeshProInChildren` so `WalkTransform`'s body contains no missing-overload reference (previously the IL2CPP binder failed every time `WalkTransform` was invoked, dumping a warning per spawn frame), (c) self-disable on first runtime failure via `_textMeshProScanDisabled`. **Run test required.**
- [ ] **E2.** Audit every other `GetComponents<` callsite. Confirmed-broken in current AOT: `GetComponents<Behaviour>()` (parameterless), `GetComponents<Component>()`, `GetComponentsInChildren<T>(bool)`, `FindObjectsByType<T>(FindObjectsSortMode)`, `FindFirstObjectByType<T>()` is unverified — keep using `FindObjectOfType<T>` (deprecated but present).
- [ ] **E3.** Promote the AOT probe results to a single static `Aot` table read by every patch. Add probes for any new generic overload before referencing it. Document each new probe in [docs/runtime_hardening.md](docs/runtime_hardening.md).
- [ ] **E4.** Replace any remaining `AccessTools.Method(t, "x")` / `AccessTools.TypeByName(...)` outside `PatchHelpers` with `PatchHelpers.FindMethod` / `PatchHelpers.SafeTypeByName`. Grep is clean today; re-grep after every PR.
- [ ] **E5.** Throttle-and-disable contract: any patch prefix that throws once should self-disable after N occurrences (3 is sane), to prevent per-frame log floods if the AOT image changes again. Apply to `Patch_CharacterCanvas_GuestBind`, the `OnStopClient` finalizer (already throttled), and any future patch that touches reflection.

### Phase F — Re-enable `FishNetOwnershipPatcher` (post-A success)

Currently disabled (`SceneWatcher._fishNetOwnershipDisabled = true`). After A+C
land, re-enable behind a config flag and verify `IsOwner=true` for `SledCoopP*`
clones across one full session. Networked sled / snowball / build paths depend
on it.

---

## 5. Working order

1. **A1–A4** (lazy EOS patcher) — without this, nothing else matters.
2. **E1** (Patch_CharacterCanvas_GuestBind hardening) — easy, removes log noise that may be masking the real freeze.
3. **C1a** (Tugboat swap) — simplest path to confirm FishyEOS is the freeze culprit.
4. Run a clean session. If EOS log entries are gone and the game advances past frame 19022, **B** and **D** can be deferred.
5. **B** (LobbyManager shim) — only needed if the lobby UI is reachable from the offline custom server flow; verify with a UI walkthrough.
6. **F** (re-enable ownership patcher) — last, after end-to-end stability is proven.

---

## 6. Done / proven

Tracked in [IMPLEMENTATION_GUIDE.md §Known Gaps](IMPLEMENTATION_GUIDE.md). Major:
clone spawn + camera + HUD bind, offline custom server start, ownership-patcher
disable workaround, two-tier suppressor, points/race/snowball/building paths.

---

## 7. Hard gaps (unchanged)

- Voice chat (Dissonance) per-slot — no IL2CPP-safe path identified.
- EOS/Steam party presence for guests — platform SDK constraint.
- Per-guest cosmetics (`PlayerSavedStats` / `CharacterModelGeneralized`) — clones share host model.
- Networked `PlayerRacingController` — local `GuestRacingTracker` is the active substitute.
