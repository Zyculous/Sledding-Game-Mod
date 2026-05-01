# Runtime Hardening

Constraints and safe-call patterns for this game's specific
BepInEx 6 / Il2CppInterop / Unity 6 / Wine combination. Every entry below was
introduced in response to an observed startup or gameplay failure; do not
revert them without re-running the boot log to confirm the underlying issue
is fixed in interop.

## 1. Type & method lookup

`HarmonyLib.AccessTools.TypeByName(name)` and
`HarmonyLib.AccessTools.Method(type, name)` both fall back to a full
`GetTypesFromAssembly()` sweep over every loaded assembly when the lookup
misses. In this build the following assemblies throw
`ReflectionTypeLoadException` from `Type[] GetTypes()`:

- `UnityEngine.CoreModule` — `<>c`, `IdentityAttributes` invalid format
- `UnityEngine.PhysicsModule` — `Flags`, `SupportedUnityFeatures`
- `UnityEngine.UIElementsModule` — `PageStatistics`, `UxmlSerializedData` ×27,
  `PointerStationaryEvent` constraint violation
- `UnityEngine.ParticleSystemModule` — `Seed4`, `Initial`, `Force`, `Collision`
- `UnityEngine.VirtualTexturingModule` — `TextureStackBase\`1` ×2
- `__Generated` — single bulk failure

Each failed lookup produces ~27 HarmonyX warning lines plus stack traces.

**Required pattern** for every patch `TargetMethod()`:

```csharp
static MethodBase? TargetMethod()
{
    var t = PatchHelpers.SafeTypeByName("MyType");          // cached dict, no scan
    return t == null ? null : PatchHelpers.FindMethod(t, "Awake"); // Type.GetMethod, no scan
}
```

`PatchHelpers.SafeTypeByName` is in
[src/Patches/DiagnosticPatches.cs](../src/Patches/DiagnosticPatches.cs) and
builds its cache once from `AppDomain.CurrentDomain.GetAssemblies()`, skipping
the unsafe assemblies above. Type lookups outside the patches package
should use the fully-qualified name
`SledCoopMod.Patches.PatchHelpers.SafeTypeByName(...)`.

`PatchHelpers.FindMethod(type, names)` calls `Type.GetMethod` directly with
`DeclaredOnly` instance flags. `PatchHelpers.FindMethodInherited` walks up
the chain (used for FishNet network callbacks declared on `NetworkBehaviour`).

## 2. Forbidden Unity / IL2CPP APIs

These all `throw MissingMethodException` from the trampoline and the
exception escapes any local `try/catch`:

| API | Symptom | Substitute |
|-----|---------|------------|
| `UnityEngine.Object.FindObjectsOfType<T>()` (and `(Type)` overload) | hard freeze inside `PlayerControl.Awake` | `Object.FindFirstObjectByType<T>()` (parameter-free generic) |
| `UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode)` | `MissingMethodException` at trampoline; subsequent IL2CPP calls freeze | walk `SceneManager.GetActiveScene().GetRootGameObjects()` (via `SpawnHooks.GetSceneRootGameObjects`) + manual `Transform.GetChild(i)` recursion + per-node `GetComponent<Canvas>()` |
| `GameObject.GetComponentsInChildren<T>(bool)` and `GameObject.GetComponentsInChildren<T>()` | both `MissingMethodException` in this AOT image | `Transform.GetChild(int)` recursion + per-node `GetComponent<T>()` |
| `GameObject.GetComponents<Component>()` | `MissingMethodException`; `<Component>` overload not in AOT | `GetComponents<Behaviour>()` (verified in AOT — but probe on plugin load before relying on it) |
| `UnityEngine.Object.FindFirstObjectByType<T>()` | `MissingMethodException` for `Canvas` (and likely other non-NetworkManager types) | same as above; only `NetworkManager` has been observed to resolve |
| `SceneManager.sceneLoaded += handler` | `MissingMethodException: UnityAction\`2..ctor(Object, IntPtr)` | `SceneWatcher.Update()` polls `SceneManager.GetActiveScene().name` and arms a one-frame defer for the recheck path |
| `GetComponent(string)` and `GetComponent(Type)` | **uncatchable** `MissingMethodException` — exception escapes the local `try/catch` and the trampoline state is corrupted, freezing Unity on the next IL2CPP call (e.g. the very next `AddComponent<Camera>`) | use the generic `GetComponent<T>()` / `GetComponents<Component>()` with a compile-time type and resolve the target by `Type.IsAssignableFrom` in managed code (see `LocalPlayerSpawner.FindComponentOfType`) |
| `GetComponentInChildren(Type)` | same | walk `transform` recursively yourself |
| `MakeGenericMethod` on a runtime-resolved type that is not in the AOT image | `MissingMethodException` from the trampoline | only invoke this from cold paths (settings overlay, etc.); never on the spawn path |

## 3. Logging

- `Plugin.Log.LogWarning` is reserved for things the player should investigate.
- Every probe that legitimately fails on a clean boot (optional UI type missing,
  Camera.main not available yet, SourceCameraPrefab not registered yet) must
  log at `Debug`.
- `Patch_FishNetNetworkBehaviour_OnStopClient` (finalizer) swallows benign
  `NullReferenceException` / `MissingReferenceException` during scene teardown.
  Logs only the 1st swallow and every 300th — the underlying behaviour fires
  hundreds of times per second from FishNet's own teardown loop.
- The `[Harmony] Skipped {patch_class}` line in `Plugin.Load` is at `Debug`
  because optional probes intentionally return null from `TargetMethod`.

## 4. Boot sequence

```
Plugin.Load()
  ├── ModConfig.Init
  ├── Harmony.PatchAll       (~30 patches; misses log at Debug)
  ├── GameplayPatcher.PreResolve
  ├── SpawnHooks.PreResolve  (HUD type cache via SafeTypeByName)
  ├── OfflineModeManager.PreResolve
  ├── ClassInjector.RegisterTypeInIl2Cpp<T>() ×14
  └── new GameObject("SledCoopMod").AddComponent<ModBootstrap>()

ModBootstrap.Awake adds the manager singletons under itself.

Boot scene → Main Mountain Scene → PlayerControl.Awake (host pawn)
  └── SpawnHooks.OnHostPawnSpawned
        ├── TryRegisterHostCamera           (Camera.main; may defer one frame)
        ├── TryScanForHudCanvas             (FindObjectOfType<Canvas>)
        ├── SpawnExtraLocalPlayers          (clone for each joined slot 1-3)
        ├── CameraLayoutManager.RefreshLayout
        ├── WireAllCameraFollowers          (idempotent retry path)
        └── HudManager.RefreshHuds
```

`InputRouter` may register a slot before any of the above runs (P2 plugs in
controller during the boot screen). All slot-aware methods accept that
state and defer; once the host pawn spawns the layout pass picks up every
joined slot.

## 5. AOT-overload probe pattern

Some generic `GameObject` overloads are present in the AOT image and others are
silently stripped, with no obvious pattern. When a code path that uses one of
these is on the spawn-critical sequence, validate the overload at plugin load
time on a throwaway GameObject and gate the call on the result.

**Critical:** the IL2CPP runtime resolves every generic instantiation referenced
in a method body at *method invocation time* (one of its first steps). A missing
overload throws `MissingMethodException` from that resolution pass — *before*
any of the method's `try` blocks are entered.  Putting `try { ... } catch { }`
around the call site is therefore not enough; the exception escapes and tears
down the *caller* (we hit this with `BepInEx Error loading [SledCoopMod 0.1.0]
... at LocalPlayerSpawner.PreResolve()` after referencing both
`GetComponents<Behaviour>()` and `GetComponents<Component>()` in the same method).

Two rules:

1. **Never reference a generic overload you have not confirmed is in the AOT
   image** — even in unreachable code paths. If you must conditionally use one,
   put the call in its own helper method so the binder only resolves it when
   that helper is invoked.
2. **Probe each suspect overload from inside its own helper**, then gate the
   real call site on the probe result.  Example:

```csharp
public static void PreResolve()
{
    UnityEngine.GameObject? probe = null;
    try
    {
        probe = new UnityEngine.GameObject("SledCoopProbe");
        probe.SetActive(false);
        _enabled = TryProbeBehaviour(probe);  // helper isolates the binder hit
    }
    catch { _enabled = false; }
    finally { if (probe != null) try { Object.Destroy(probe); } catch { } }
}

private static bool TryProbeBehaviour(UnityEngine.GameObject probe)
{
    try { return probe.GetComponents<UnityEngine.Behaviour>() != null; }
    catch { return false; }
}
```

Why a probe rather than just a `try/catch` at the call site: a
`MissingMethodException` during the spawn frame can corrupt IL2CPP trampoline
state. The next unrelated IL2CPP call (we observed `AddComponent<Camera>` on
`SledCoopCam_Slot1`) hangs the main thread. Catching the exception logs a
clean error but does not undo the corruption — the freeze still occurs a few
calls later and is hard to attribute back to the bad overload. Probing once
during plugin load (no spawn in flight) and gating the real call site
eliminates the failure mode entirely.

`LocalPlayerSpawner.PreResolve` uses this pattern for the host-field-copy
optimization. If the probe fails, `TryCopyHostComponentFields` is skipped
entirely (the spawn flow continues, sled-physics field-copy fallback may need
attention).

## 6. Spawn-frame discipline

The host-pawn spawn frame (`SpawnHooks.OnHostPawnSpawned` after
`PlayerControl.Awake`) is the most fragile point in the entire mod lifecycle:

- Unity is mid-Instantiate on the host pawn — many components are partially
  initialised, FishNet is mid-spawn, the camera was just registered.
- Every IL2CPP call costs ~5–50 µs through the interop trampoline.
- Any `MissingMethodException` from a stripped overload here can corrupt
  trampoline state and freeze the next unrelated IL2CPP call.

**Rules for any code that runs on the spawn frame:**

1. Never call reflection-based scene walks (e.g.
   `Scene.GetRootGameObjects()` via `MethodInfo.Invoke`).
2. Never recurse over the active scene's transform tree — it has thousands
   of nodes and the per-node IL2CPP cost looks like a hang.
3. Never invoke `MakeGenericMethod` with a runtime-resolved type.
4. Never call any generic `GameObject` overload with parameters that have
   not been verified present in the AOT image.

If a feature requires any of the above, set a "suppress until frame N"
flag (see `HudManager.SuppressScanUntilFrame`) and have the polling path
check it before running.  That moves the work out of the spawn frame
where any failure is non-fatal.

## 7. HUD canvas acceptance

`SpawnHooks.ConsiderHudCanvas`, `SpawnHooks.TryScanForHudCanvas` Strategy 1, and
`SpawnHooks.ScanAndRegisterAllGameCanvases` **must** filter out:

- `Canvas.renderMode == RenderMode.WorldSpace` — these are per-pawn world-space
  nameplates parented under a dynamic player pawn.
- Names containing `character canvas`, `nameplate`, or `playerlabel`.

Why this is a freeze, not a visual glitch: when the host pawn first spawns, its
world-space `Character Canvas` is the first Canvas returned by
`Object.FindFirstObjectByType<Canvas>()`. Without the filter,
`HudManager.RegisterHostHud` accepted it, then `RefreshHuds()` immediately tried
to set `renderMode = ScreenSpaceOverlay` on a WorldSpace canvas attached to the
just-Instantiated host pawn. In the same frame this re-entered Unity's canvas
update path under the player's hierarchy and the main thread stopped — no
further `[ModBootstrap] Heartbeat` lines appear in the log.

The defensive `RefreshHuds()` guard in `HudManager.cs` also skips WorldSpace
canvases so even if a future scan path slips one through, the renderMode flip
is suppressed.

## 8. Known benign log lines

The following appear during a healthy boot and should not be acted on:

```
[Il2CppInterop] During invoking native->managed trampoline
Exception: System.InvalidOperationException: Handle is not initialized.
  ... at Trampoline_VoidThis...OnStopClient
```

Caused by Il2CppInterop calling our injected types' OnStopClient before the
managed GCHandle is allocated — happens during `ClassInjector.RegisterType...`
because the class registration walks every virtual slot. We do not implement
`OnStopClient`, so the trampoline lookup succeeds but the GCHandle resolves
to zero. Harmless: there is no managed instance to dispatch to.
