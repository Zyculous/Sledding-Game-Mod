# EOS Disable Plan

This document describes a full patch plan for disabling Epic Online Services when `SledCoopMod` is enabled.
The goal is to make the mod boot and host local split-screen sessions without allowing the game to initialize EOS/Steam services or create online lobbies.

> **Quick checklist / current state:** [NEW_PLAN.md](../NEW_PLAN.md).
> **2026-04-30 root-cause update:** see §New diagnosis below — the problem is not which
> targets we patch, it's *when* we apply them. EOS plugin assemblies are not
> loaded yet at `Plugin.Load`, so every `EOSPatches.cs` `TargetMethod()` returns
> null and Harmony silently skips the class (`21 skipped` in the log).

## New diagnosis (2026-04-30) — patch timing, not patch coverage

`PatchHelpers.SafeTypeByName` builds its type-name cache once on first call from
`AppDomain.CurrentDomain.GetAssemblies()`
([SledCoopMod/src/Patches/DiagnosticPatches.cs:60](../src/Patches/DiagnosticPatches.cs#L60)).
At BepInEx chainload time the only loaded assemblies are BepInEx, Unity, and
Assembly-CSharp. The PlayEveryWare/EOS plugin and the FishyEOS transport assemblies
load lazily later (from the Boot scene), so:

- Every `Patch_PlayEveryWare*` and `Patch_EOSManager_*` `TargetMethod()`
  resolves to `null` → Harmony skips the patch.
- The mod logs `Harmony: 26 patch(es) applied, 21 skipped.` The 21 skips are
  almost entirely the EOS suppressors.
- EOS then boots normally — Player.log shows `EOSSDK-Win64-Shipping.dll`
  loading, `EOS_Initialize` and `EOS_Platform_Create` succeeding,
  `EOSAuthenticator:StartConnect`, EOSAuth `TokenGrant` HTTP, RTC + Stomp
  init, and `FishNet.Transporting.FishyEOSPlugin.FishyEOS` carrying every
  packet on the loopback host.

**Fix: deferred patching.** Subscribe to `AppDomain.CurrentDomain.AssemblyLoad`
in `Plugin.Load`. When an assembly named `PlayEveryWare.*`, `EOSSDK*`,
`Epic.OnlineServices*`, or `*FishyEOS*` loads, invalidate the type cache
(`PatchHelpers.InvalidateCache()`) and re-run a tagged subset of EOS-only
patch classes through Harmony. Tag those classes with a marker attribute
(`[SledCoopEosPatch]`) so the late pass iterates exactly the right set.

This is the **single most important change** in this document. None of the
existing EOS patches help until this is done.

## Why this is needed

Recent logs show EOS still initializes even after the mod's existing offline-only patches:

- `PlayEveryWare.EpicOnlineServices.PlatformManager:.cctor()`
- `NativePlugin (INFORM): Loading path ... EOSSDK-Win64-Shipping.dll`
- `NativePlugin (INFORM): start eos init`
- `NativePlugin (INFORM): call EOS_Initialize`
- `PlayEveryWare.EpicOnlineServices.PlatformManager:GetPlatformConfig()`
- `PlayEveryWare.EpicOnlineServices.EOSSingleton:InitializeOverlay(IEOSCoroutineOwner)`
- `_Scripts.Boot.EOSAuthenticator:StartConnect(LoginContext)`
- `_Scripts.Boot.BootSceneManager:InitializeBootables()`

That means the current mod suppressed some boot-time EOS paths, but not the earliest EOS platform/config/SDK startup path.

## Current coverage in `SledCoopMod/src/Patches/EOSPatches.cs`

The mod already attempts to patch:

- `PlayEveryWare.EpicOnlineServices.PlatformManager` static ctor
- `PlatformManager.InitializePlatformConfigs()`
- `_Scripts.Boot.BootSceneManager.InitializeBootables()` to remove `EOSAuthenticator`
- `_Scripts.Boot.EOSAuthenticator.StartBoot()`
- `_Scripts.Boot.EOSAuthenticator.IsBooted()`
- `_Scripts.Boot.EOSAuthenticator.StartConnect()`
- `_Scripts.Binary.ExternalAuthenticationManager.StartConnect()`
- `_Scripts.Managers.LobbyManager.CreateLobby()` when offline mode is active

These are good first steps, but the logs show EOS is still triggered earlier than those patch points.

## Missing / high-priority patch targets

### 1. Prevent `PlatformManager` from touching EOS config/runtime data

The earliest log evidence is `PlayEveryWare.EpicOnlineServices.PlatformManager:.cctor()` and its platform config recognition. That means patching the static ctor alone may not be enough.

Target methods to patch or wrap:

- `PlayEveryWare.EpicOnlineServices.PlatformManager.GetPlatformConfig()`
- `PlayEveryWare.EpicOnlineServices.PlatformManager.TryGetConfig(...)`
- `PlayEveryWare.EpicOnlineServices.PlatformManager.CurrentPlatform` accessor
- `PlayEveryWare.EpicOnlineServices.PlatformManager.GetConfigFilePath(...)`
- `PlayEveryWare.EpicOnlineServices.PlatformManager.GetFullName(...)`

### 2. Prevent EOS native SDK load / `EOSManager` initialization

The native plugin load indicates the EOS SDK is being initialized by the PlayEveryWare wrapper.

Target methods to patch:

- `PlayEveryWare.EpicOnlineServices.EOSManager.EOSSingleton.Instance` getter
- `PlayEveryWare.EpicOnlineServices.EOSManager.LoadEOSLibraries()`
- `PlayEveryWare.EpicOnlineServices.EOSManager.LoadDynamicLibrary(string)`
- `PlayEveryWare.EpicOnlineServices.EOSManager.Init(IEOSCoroutineOwner, string)`
- `PlayEveryWare.EpicOnlineServices.EOSManager.InitializeOverlay(...)`
- `PlayEveryWare.EpicOnlineServices.EOSManager.InitializePlatformInterface()`
- `PlayEveryWare.EpicOnlineServices.EOSManager.CreatePlatformInterface()`
- `PlayEveryWare.EpicOnlineServices.EOSManager.LoadDelegatesWithEOSBindingAPI()`
- `PlayEveryWare.EpicOnlineServices.EOSManager.GetEOSPlatformInterface()`

If these methods are skipped or made no-op, the native SDK load should stop before `EOS_Initialize` and `EOS_Platform_Create` occur.

### 3. Patch `EOSManagerPlatformSpecificsSingleton` / overlay initialization

The game uses platform-specific overlay management. Block these too:

- `PlayEveryWare.EpicOnlineServices.EOSManagerPlatformSpecificsSingleton.Instance`
- `PlayEveryWare.EpicOnlineServices.PlatformSpecifics.InitializeOverlay(...)`
- `PlayEveryWare.EpicOnlineServices.PlatformSpecifics.UpdateNetworkStatus()`

### 4. Patch boot-time game logic that drives EOS auth and lobby startup

Existing patches already cover some boot paths, but more methods should be patched for completeness:

- `_Scripts.Boot.BootSceneManager.InitializeBootables()` — keep removing EOS bootables, but also inspect any other bootable components that reference EOS.
- `_Scripts.Boot.EOSAuthenticator.Initialise(BootSceneManager)` (if present in the actual game build)
- `_Scripts.Boot.EOSAuthenticator.StartBoot()` / `StartConnect()` / `QueryUserInfo()`
- `_Scripts.Binary.ExternalAuthenticationManager.StartConnect()`
- `_Scripts.Managers.LobbyManager.JoinLobby(...)`
- `_Scripts.Managers.LobbyManager.SearchByAttributes(...)`
- `_Scripts.Managers.LobbyManager.UpdateLobbyHeartbeat()`
- `_Scripts.Managers.LobbyManager.OnLobbyJoinedComplete(...)`
- `_Scripts.Managers.LobbyManager.OnCreateLobbyComplete(...)` (already patched for mod signaling, but should also be blocked for offline-only mode)
- `_Scripts.Managers.LobbyHeartbeat` constructors and heartbeat updates
- `_Scripts.UI.Pre_Game.UILobbyExplorer` / `UILobbyItem` / any UI that triggers lobby search/join

### 5. Patch FishNet EOS transport and sample EOS plugins

The project contains FishNet EOS transport code and PlayEveryWare sample managers.

Candidate file groups:

- `Unity Files/Scripts/Fishnet.Plugins.FishyEOS/FishNet/Plugins/FishyEOS`
- `Unity Files/Scripts/Fishnet.Plugins.FishyEOS/FishNet/Transporting/FishyEOSPlugin/*`
- `Unity Files/Scripts/com.playeveryware.eos.samples.steam/PlayEveryWare/EpicOnlineServices/Samples/Steam/SteamManager.cs`
- `Unity Files/Scripts/com.playeveryware.eos.samples.networking/PlayEveryWare/EpicOnlineServices/Samples/Network/*`

Patch these only if they are actually instantiated or if the game selects EOS transport at runtime. Otherwise ensure the local-only FishNet transport path is forced.

## Patch priority order

0. **Deferred patcher (NEW; blocking)** — `EosLatePatcher` triggered from
   `AppDomain.AssemblyLoad`. Without this, items 1–5 are no-ops at runtime.
1. `PlatformManager` / `EOSManager` native SDK load block
2. `BootSceneManager` / `EOSAuthenticator` / `ExternalAuthenticationManager` boot flow block
3. `LobbyManager` replacement (see "Replacement: SledCoopLobbyShim" below) and lobby-related EOS services block
4. `FishNet` EOS transport / sample manager block — must include actual transport swap on the live `NetworkManager`, not just method-prefix no-ops
5. `PlatformSpecifics` overlay / network status block

## Replacement: `SledCoopLobbyShim` (replaces native `_Scripts.Managers.LobbyManager`)

The decompiled native lobby manager
([Unity Files/AssetRipper_export_20260422_195824/.../Managers/LobbyManager.cs](../../Unity Files/AssetRipper_export_20260422_195824/ExportedProject/Assets/Scripts/Assembly-CSharp/_Scripts/Managers/LobbyManager.cs))
is a `MonoBehaviour` whose public surface uses
`Epic.OnlineServices.Lobby.{Lobby, LobbyDetails, LobbySearch}` and an internal
`EOSLobbyManager` field. Stub bodies return null/false, but several callers
(`GameInfo`, `UILobbyExplorer`, `LobbyHeartbeat`,
`UI.Pre_Game.UILobbyExplorer`) will read `Instance` and the returned
collections without null-checking — so blanket-suppressing each method (the
current `Patch_LobbyManager_*_OfflineOnly` set) leaves callers that read
`GetCurrentLobby()` / `GetSearchResults()` without a safe default.

Plan:

- Add `SledCoopMod/src/SledCoopLobbyShim.cs`. At first scene load, after the
  native `LobbyManager` `Awake` runs, the shim:
  - Captures `LobbyManager.Instance` via reflection.
  - Patches every public method on `LobbyManager` to a static prefix that
    returns "safe non-null" sentinels: empty `Dictionary<Lobby, LobbyDetails>`,
    a sentinel `Lobby` whose `Id` is `"sledcoop-offline"`, `false` for
    `IsJoiningLobby()`, etc.
  - Patches `LobbyHeartbeat.Awake` and `LobbyHeartbeat.Update` to no-op (the
    component runs independently from `LobbyManager.UpdateLobbyHeartbeat`).
  - Patches `_Scripts.UI.Pre_Game.UILobbyExplorer.Awake` /
    `Refresh` / `OnEnable` to early-return when offline.
  - Patches `_Scripts.Managers.GameInfo` lobby getters to return mod-supplied
    state.
- All shim patches go through `EosLatePatcher` (see priority 0) — they target
  Assembly-CSharp types so they *can* apply at `Plugin.Load`, but for symmetry
  and to stay correct if the EOS sample assembly is what carries the type, run
  them in the late pass too.

The existing `Patch_LobbyManager_*_OfflineOnly` patches in
[SledCoopMod/src/Patches/EOSPatches.cs](../src/Patches/EOSPatches.cs) become
the shim's "method short-circuit" layer; they stay.

## Replacement: FishyEOS transport → `Tugboat` (loopback TCP)

[docs/game_logs/Player.log](game_logs/Player.log) shows every packet on the
loopback host flowing through
`FishNet.Transporting.FishyEOSPlugin.FishyEOS:HandleClientReceivedDataArgs`.
That transport drives EOS internally even when the suppression patches do
apply, because the transport is the live transport on the NetworkManager
component instance.

Plan:

- In `OfflineModeManager.StartOfflineLocalGame`, before
  `ServerManager.StartConnection()`:
  1. `Object.FindObjectOfType<NetworkManager>()` (deprecated overload — the
     only AOT-safe one in this build).
  2. `PatchHelpers.SafeTypeByName("FishNet.Transporting.Tugboat.Tugboat")`.
     Tugboat ships with FishNet so it should be present in this AOT image —
     verify on first run and log presence/absence.
  3. If present, `nm.gameObject.AddComponent<Tugboat>()` (compile-time generic
     — safe), then assign it as the active transport via
     `TransportManager.SetTransport(...)` or its private backing field.
  4. Disable the FishyEOS component (`fishy.enabled = false`).
- Fallback if Tugboat is not present: prefix-patch `FishyEOS.IterateIncoming`
  and `IterateOutgoing` to no-op when `OfflineModeManager.OfflineModeActive`.

## Freeze diagnosis (2026-04-30) — and the hardening sweep

[docs/game_logs/LogOutput.log](game_logs/LogOutput.log) ends at:

```
[ModBootstrap] post-spawn tick 1 (frame=19022).
[HostPatches] Server_CheckForFalselyJoinedPlayers skipped (custom server active).
```

No further heartbeats or warnings — the main thread stops on the next tick.

The most consistent explanation, given Player.log shows EOS fully booted plus
RTC frame-skip and overlay-not-configured warnings, is that one of the
unsuppressed EOS / RTC paths is blocking the main thread once
`PlayerControl.OnStartClient` triggers Dissonance ownership wiring (Player.log
shows `Dissonance.Integrations.FishNet.DissonanceFishNetPlayer:OnOwnershipClient`
in earlier sessions). We can't isolate the exact call until the late-patcher
(§Patch priority 0) actually applies the EOS suppression patches — at that
point the next test log will either:

- advance past frame 19022 cleanly → freeze was an EOS/FishyEOS path; close.
- still freeze → bisect by toggling each EOS patch class and watching for the
  first call that lands in the unblocked native code.

### Hardening sweep — IL2CPP-AOT-safe pattern audit

The mod has accumulated several spots that use generic overloads that are
*not* in this AOT image. Every one is a future freeze candidate. Confirmed
broken in the current build:

- `GameObject.GetComponents<T>()` (parameterless; **including `<Behaviour>`**)
- `GameObject.GetComponents<Component>()`
- `GameObject.GetComponentsInChildren<T>(bool)`
- `Object.FindObjectsByType<T>(FindObjectsSortMode)`
- `Object.FindFirstObjectByType<T>()` (unverified — keep using `FindObjectOfType<T>`)
- `Component.GetComponent(Type)` (use compile-time generic instead)
- `UnityAction<Scene, LoadSceneMode>..ctor(object, IntPtr)` — used by
  `SceneManager.sceneLoaded += ...` (workaround already in place: poll scene
  name in `SceneWatcher.Update`).

Active offenders the latest log proves are still firing:

- [SledCoopMod/src/Patches/PlayerSocialPatches.cs:122](../src/Patches/PlayerSocialPatches.cs#L122) — `Patch_CharacterCanvas_GuestBind` calls `probe.GetComponents<Behaviour>()` and the catch only logs once; the call still throws on every spawn (LogOutput line 148: `[Patch_CharacterCanvas_GuestBind] Method not found...`).
- [SledCoopMod/src/Patches/PlayerSocialPatches.cs:154](../src/Patches/PlayerSocialPatches.cs#L154) — same pattern, second call site.

Fix template:

```csharp
private static int _aotProbeFailures;
private const int AotProbeFailureLimit = 3;
private static bool _aotBehaviourArrayDisabled;

static Behaviour[]? TryGetBehaviours(GameObject go)
{
    if (_aotBehaviourArrayDisabled) return null;
    try { return go.GetComponents<Behaviour>(); }
    catch (Exception e)
    {
        if (++_aotProbeFailures >= AotProbeFailureLimit)
        {
            _aotBehaviourArrayDisabled = true;
            Plugin.Log.LogWarning(
                $"[AOT] GetComponents<Behaviour>() unavailable in this build " +
                $"after {_aotProbeFailures} attempts; disabling. ({e.Message})");
        }
        return null;
    }
}
```

Apply the same self-disable pattern to every reflection / generic-overload
call that has thrown once. Promote the disabled flags into a single static
`Aot` table that other patches read before invoking the same overload.

**Hardening rules to enforce in review (and in
[docs/runtime_hardening.md](runtime_hardening.md)):**

1. Every generic overload referenced anywhere in source must be on the
   AOT-present list, or it must be inside a self-disabling helper.
2. `MissingMethodException` must never silently retry — the trampoline
   corruption persists and the next unrelated IL2CPP call can hang.
3. No `AccessTools.TypeByName` / `AccessTools.Method` outside `PatchHelpers`.
4. No `SceneManager.sceneLoaded += ...` — poll in `Update`.
5. Any patch finalizer that catches NREs must throttle (mirror
   `Patch_FishNetNetworkBehaviour_OnStopClient` in DiagnosticPatches.cs).

## Validation checklist

- [ ] Boot logs do not contain `PlayEveryWare.EpicOnlineServices.PlatformManager:.cctor()`
- [ ] Boot logs do not contain `NativePlugin ... EOSSDK-Win64-Shipping.dll`
- [ ] Boot logs do not contain `EOS_Initialize` / `EOS_Platform_Create`
- [ ] Boot logs do not contain `EOSSingleton:InitializeOverlay(...)`
- [ ] Boot logs do not contain `_Scripts.Boot.EOSAuthenticator:StartConnect(...)`
- [ ] Boot logs do not contain `LobbyManager`, `LobbyHeartbeat`, `EOSLobbyManager`, `UI Lobby`, or `SteamManager` EOS startup paths
- [ ] Local split-screen custom host still starts via `OfflineModeManager` and FishNet
- [ ] No online lobby UI or external authentication flow is triggered in the offline branch

## Recommended implementation structure

- Keep `SledCoopMod/src/Patches/EOSPatches.cs` as the EOS suppression module.
- Add a second phase of patches for `PlayEveryWare.EpicOnlineServices.EOSManager` and `EOSManagerPlatformSpecificsSingleton` methods.
- Add an explicit `OfflineOnlyMode` guard so any game-side lobby/auth path is skipped when the mod is active.
- Log every suppression in verbose mode so boot behavior can be audited.

## Notes from the log

The early log clearly shows the problem is not only `LobbyManager` or `EOSAuthenticator`: the native EOS plugin is being loaded before the mod’s existing boot suppression is enough.

This means the mod must stop EOS at the platform/config/manager layer, not just at the later `BootSceneManager`/`LobbyManager` layer.

## Next patch targets to add

- `PlayEveryWare.EpicOnlineServices.PlatformManager.CurrentPlatform`
- `PlayEveryWare.EpicOnlineServices.PlatformManager.GetPlatformConfig`
- `PlayEveryWare.EpicOnlineServices.EOSManager.Instance`
- `PlayEveryWare.EpicOnlineServices.EOSManager.EOSSingleton.Instance`
- `PlayEveryWare.EpicOnlineServices.EOSManager.LoadEOSLibraries`
- `PlayEveryWare.EpicOnlineServices.EOSManager.Init`
- `PlayEveryWare.EpicOnlineServices.EOSManager.InitializeOverlay`
- `PlayEveryWare.EpicOnlineServices.EOSManagerPlatformSpecificsSingleton.Instance`
- `PlayEveryWare.EpicOnlineServices.Samples.EOSLobbyManager.CreateLobby`
- `PlayEveryWare.EpicOnlineServices.Samples.EOSLobbyManager.JoinLobby`
- `PlayEveryWare.EpicOnlineServices.Samples.EOSLobbyManager.SearchByLobbyId`
- `PlayEveryWare.EpicOnlineServices.Samples.EOSLobbyManager.SearchByAttribute`
- `_Scripts.Managers.LobbyManager.JoinLobby`
- `_Scripts.Managers.LobbyManager.SearchByAttributes`
- `_Scripts.Managers.LobbyManager.UpdateLobbyHeartbeat`
- `FishNet.Transporting.FishyEOSPlugin.FishyEOS.StartConnection`
- `FishNet.Transporting.FishyEOSPlugin.ServerPeer.StartConnection`
- `FishNet.Transporting.FishyEOSPlugin.ClientPeer.StartConnection`
- `FishNet.Transporting.FishyEOSPlugin.ClientHostPeer.StartConnection`
- `PlayEveryWare.EpicOnlineServices.Samples.Network.EOSTransportManager.Initialize`

If these are blocked, the game should reach the offline local host path without booting EOS.
