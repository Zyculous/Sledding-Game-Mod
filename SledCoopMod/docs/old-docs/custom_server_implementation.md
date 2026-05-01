# Custom Server Implementation

Reference for how SledCoopMod registers clone pawns with FishNet's server, the full API surface derived from the game's decompiled stubs, and what is needed for remaining networked features.

---

## Overview

FishNet is the game's networking framework. Without a `NetworkObject` registered with `ServerManager`, a clone pawn's `NetworkBehaviour` components have no `_networkObject` reference, so every `RpcLogic___` method (sled, snowball, building) bails out on the `IsOwner` / `IsServerInitialized` checks before doing anything.

## Related docs

- [Network / Fallback](docs/network_fallback.md) — custom local server startup and offline host fallback behavior.
- [Split-screen Architecture](docs/splitscreen_architecture.md) — how guest clone pawns, cameras, and HUDs fit into the overall coop architecture.
- [Slot Input Lifecycle](docs/slot_input_lifecycle.md) — how local guest input is routed and isolated from the host.

**Note:** `NetworkManager` and `ServerManager` state are the foundation for the custom server flow described in `Network / Fallback`.

**Two execution contexts:**

| Context | Server started? | Spawn path |
|---------|-----------------|-----------|
| Online host (solo or lobby) | Yes | `TryNetworkSpawnPawn` → `ServerManager.Spawn(nob, null)` |
| Offline / no-server | No | `RemoveNetworkObjectFromHierarchy`; local-only pawn |

**Fallback chain for each clone spawn:**
1. `TryNetworkSpawnPawn` — tries `ServerManager.Spawn`. On success, clone has a live `ObjectId`.
2. If server not started or Spawn throws → `RemoveNetworkObjectFromHierarchy` (destroys all `NetworkObject` components on the clone hierarchy so FishNet never sees them). `DisableFishNetBehaviours` is NOT called — it uses `GetComponents<T>()` which causes an uncatchable `MissingMethodException` in this IL2CPP build.

---

## FishNet API Reference

All types come from `FishNet.Runtime.dll`. The game ships IL2CPP stubs; method bodies are empty — all behaviour is in native code. The signatures below are accurate from the decompiled stubs.

### `NetworkManager`

Singleton. Obtain via `UObject.FindObjectOfType<NetworkManager>()`.

```csharp
bool IsServerStarted          // true when server transport is connected
bool IsClientStarted          // true when client transport is connected
ServerManager ServerManager
ClientManager ClientManager
PrefabObjects SpawnablePrefabs // IL2CPP stub: Prefabs property returns empty list;
                               // real data is in the backing _prefabs SerializeField
                               // (read via reflection — see FindPlayerPrefab)
```

### `ServerManager` (`FishNet.Managing.Server.ServerManager`)

```csharp
bool Started
ServerObjects Objects          // Objects.Spawned: Dictionary<int, NetworkObject>
Dictionary<int, NetworkConnection> Clients   // connected clients by connection ID

// Spawn a NetworkObject, optionally owned by a connection.
// ownerConnection = null → server-owned (correct for clone pawns to avoid
// double-registration under the host's connection).
void Spawn(NetworkObject nob, NetworkConnection ownerConnection = null, Scene scene = default)
void Spawn(GameObject go,    NetworkConnection ownerConnection = null, Scene scene = default)

// Remove a NetworkObject from the network. DespawnType controls whether the
// GO is also destroyed (Destroy) or just unregistered (Pool).
void Despawn(NetworkObject networkObject, DespawnType? despawnType = null)
void Despawn(GameObject go,              DespawnType? despawnType = null)

bool IsAnyServerStarted(int excludedIndex = -1)
bool IsOnlyOneServerStarted()
bool AreAllServersStopped()

bool StartConnection()           // start on default port
bool StartConnection(ushort port)
bool StopConnection(bool sendDisconnectMessage)

// Events
event Action<ServerConnectionStateArgs>               OnServerConnectionState
event Action<NetworkConnection, RemoteConnectionStateArgs> OnRemoteConnectionState
event Action<NetworkConnection, bool>                 OnAuthenticationResult

// Broadcast helpers (omitted — not used by this mod)
```

### `ClientManager` (`FishNet.Managing.Client.ClientManager`)

```csharp
NetworkConnection Connection   // local client's active connection to the server;
                               // pass as ownerConnection to give a clone to the host client
bool Started
Dictionary<int, NetworkConnection> Clients

bool StartConnection()
bool StartConnection(string address)
bool StartConnection(string address, ushort port)
bool StopConnection()

event Action<ClientConnectionStateArgs>  OnClientConnectionState
event Action<ConnectedClientsArgs>       OnConnectedClients
event Action                             OnAuthenticated
```

**Note on `ownerConnection`:** In host mode, `ClientManager.Connection` is the same player as the host pawn. Passing it as `ownerConnection` for a clone would register two NetworkObjects under one connection, breaking FishNet's per-connection ownership tables. Always use `null` (server-owned) for clones and let `FishNetOwnershipPatcher` fake ownership.

### `ServerObjects` / `ManagedObjects` (base class)

`ServerManager.Objects` is a `ServerObjects` which extends `ManagedObjects`.

```csharp
// ManagedObjects
Dictionary<int, NetworkObject> Spawned          // key = nob.ObjectId; all live NOs
IReadOnlyDictionary<ulong, NetworkObject> SceneObjects   // NOs embedded in scene hierarchy

// FishNet internally subscribes to SceneManager.sceneLoaded via this:
internal void SubscribeToSceneLoaded(bool subscribe)

// Called by the subscription above for every scene load:
protected internal virtual void SceneManager_sceneLoaded(Scene s, LoadSceneMode arg1)

NetworkObject GetSpawnedNetworkObject(int objectId)
bool WriteSpawn(NetworkObject nob, PooledWriter writer, NetworkConnection connection)

// ServerObjects only
bool RecentlyDespawned(int objectId, uint ticks)  // guards ID reuse after fresh despawn
event Action<NetworkConnection> OnPreDestroyClientObjects

// Observer management (needed when adding/removing players mid-session)
void RebuildObservers(bool timedOnly = false)
void RebuildObservers(NetworkObject nob, bool timedOnly = false)
void RebuildObservers(NetworkConnection connection, bool timedOnly = false)
// ... many more overloads
```

### `PlayerSpawner` (`FishNet.Component.Spawning.PlayerSpawner`)

Game component — not used by this mod, but documents the intended FishNet pattern.

```csharp
[SerializeField] NetworkObject _playerPrefab   // set in Inspector or SetPlayerPrefab()
Transform[] Spawns                             // round-robin spawn points
event Action<NetworkObject> OnSpawned          // fires after each successful spawn

void SetPlayerPrefab(NetworkObject nob)

// Internal — hooks SceneManager_OnClientLoadedStartScenes so the prefab spawns
// automatically for each connecting client. The mod bypasses this by spawning
// in SpawnHooks.OnHostPawnSpawned instead.
private void SceneManager_OnClientLoadedStartScenes(NetworkConnection conn, bool asServer)
```

### `ServerSpawner` (`FishNet.Component.Spawning.ServerSpawner`)

Game component — not used by this mod.

```csharp
[SerializeField] bool _automaticallySpawn      // spawn when server starts
[SerializeField] List<NetworkObject> _networkObjects

public void Spawn()   // iterates _networkObjects, calls ServerManager.Spawn on each
// Internal — auto-calls Spawn() on ServerManager_OnServerConnectionState(Started=true)
```

### `UnityEngine.SceneManagement.SceneManager` (static)

```csharp
// Events (subscribe in Awake/Start; unsubscribe in OnDestroy)
static event UnityAction<Scene, LoadSceneMode> sceneLoaded      // after every scene load
static event UnityAction<Scene>                sceneUnloaded     // after every scene unload
static event UnityAction<Scene, Scene>         activeSceneChanged // prev + next

// Scene queries
static Scene GetActiveScene()
static Scene GetSceneByName(string name)
static Scene GetSceneAt(int index)        // 0-based; use sceneCount to bound
static int   sceneCount

// Scene manipulation
static void MoveGameObjectToScene(GameObject go, Scene scene)
    // Moves a DontDestroyOnLoad GO into a specific scene.
    // Critical for scene-transition handling of clone pawns (see below).
static AsyncOperation LoadSceneAsync(string sceneName, LoadSceneParameters parameters)
static AsyncOperation UnloadSceneAsync(Scene scene)
```

---

## Current Spawn Implementation

**Entry point:** `LocalPlayerSpawner.TryNetworkSpawnPawn(GameObject pawn)`  
**Called from:** `LocalPlayerSpawner.TryPrepareLocalPawn` → `SpawnHooks.SpawnPawnForSlot`

**Prerequisites before this is called:**
- Clone GO is already named `"SledCoopP{n}"` — the name guard in both suppressor patches keys off this prefix.
- `GameplayPatcher.Apply()` has already fired — suppressor trampolines exist on the IL2CPP vtable.
- `FishNetOwnershipPatcher.Apply()` has already fired — `IsOwner` etc. are patched.

**Step-by-step:**

```
1. FindObjectOfType<NetworkManager>()
   → if null: log "absent", return false

2. nm.IsServerStarted
   → if false: log "not started", return false

3. pawn.GetComponent<NetworkObject>()
   → if null: log "no NetworkObject", return false
   (clone was Instantiate'd from the prefab so it always has one)

4. nob.IsSpawned
   → if already true: log ObjectId, return true (idempotent)

5. nm.ServerManager.Spawn(nob, null)
   → FishNet assigns nob.ObjectId
   → fires OnStartServer / OnStartClient on all NetworkBehaviours on the clone:
       - suppressed by Patch_ClonePawnScriptSuppressor for passive types
       - allowed through by Patch_CloneGameplayOnlySuppressor for
         PlayerHoldingController and PlayerBuildingController
   → FishNetOwnershipPatcher ensures IsOwner/IsServerInitialized/IsClientInitialized
     return true for this GO in all subsequent RpcLogic___ calls

6. log success, return true

On exception:
   - If nob.IsSpawned became true before the exception:
     → call ServerManager.Despawn(nob) to keep FishNet's tables consistent
   - return false
```

**After return:**  
`SpawnHooks.SpawnPawnForSlot` stores `slot.NetworkObjectId = nob.ObjectId` if spawn succeeded, else `-1`.

---

## Despawn Flow

**Entry point:** `SpawnHooks.DestroyPawnGO(GameObject pawn)`

```
1. pawn.GetComponent<NetworkObject>() → nob
2. nob != null && nob.IsSpawned && server started
   → nm.ServerManager.Despawn(nob)
     FishNet calls OnStopServer/OnStopClient — suppressed by Patch_ClonePawnScriptSuppressor
     FishNet destroys the GO → return (no further action needed)

3. Otherwise:
   → UObject.Destroy(pawn)
```

`slot.Pawn = null` and `slot.NetworkObjectId = -1` are set by the caller (`DespawnExtraPlayers` or `DespawnPlayerForSlot`).

---

## Ownership Model and `FishNetOwnershipPatcher`

Clone pawns are spawned **server-owned** (`ownerConnection = null`). This means:

- `nob.IsOwner` → normally `false` for any client
- `nb.IsServerInitialized` → `true` (server owns it, so server is initialized)
- `nb.IsClientInitialized` → depends on client state

Game `RpcLogic___` methods check one or more of these before acting. Since `IsOwner` would be false, they'd bail without `FishNetOwnershipPatcher`.

**What `FishNetOwnershipPatcher` does:**

```csharp
// Postfix on NetworkBehaviour.IsOwner getter
// Postfix on NetworkBehaviour.IsServerInitialized getter
// Postfix on NetworkBehaviour.IsClientInitialized getter
// Postfix on NetworkObject.IsOwner getter
static void OwnershipPostfix(object __instance, ref bool __result)
{
    if (__result) return;   // already true → don't override
    var go = ReflectionHelper.GetGameObject(__instance);
    if (go != null && go.name.StartsWith("SledCoopP"))
        __result = true;
}
```

Applied by `SceneWatcher.NotifyGameplayStarted()`, removed by `NotifyGameplayEnded()`. Not applied during Boot because these getters fire in tight FishNet initialization loops — every IL2CPP→managed trampoline adds 5–50µs, which stalls Boot by 10–20 seconds if applied globally.

---

## Prefab Lookup (`SpawnHooks.FindPlayerPrefab`)

`NetworkManager.SpawnablePrefabs.Prefabs` returns an empty list from the IL2CPP stub. Actual prefab data is in the backing `_prefabs` SerializeField on the concrete type (`DefaultPrefabObjects` or `SinglePrefabObjects`).

**Strategy 1 — `GetObject` reflection:**
```csharp
MethodInfo getObj = AccessTools.Method(spawnables.GetType(), "GetObject");
// signature: NetworkObject GetObject(bool asServer, int prefabId)
result = getObj.Invoke(spawnables, new object[] { true, hostNob.PrefabId }) as NetworkObject;
```

**Strategy 2 — `TryCast` + `_prefabs` field:**
```csharp
foreach (string typeName in new[] { "DefaultPrefabObjects", "SinglePrefabObjects" })
{
    var targetType = AccessTools.TypeByName("FishNet.Managing.Object." + typeName);
    var cast = tryCastDef.MakeGenericMethod(targetType).Invoke(spawnables, null);
    var prefabsField = targetType.GetField("_prefabs", NonPublic | Instance);
    var list = prefabsField.GetValue(cast);
    int idx = Math.Min((int)hostNob.PrefabId, count - 1);
    return list[idx] as NetworkObject;
}
```

The host pawn's `PrefabId` is used as the prefab index — it's the same prefab the game uses to spawn remote players, so it will have all the correct components.

---

## Scene Transition Problem and Pending Fix

### The problem

Clone pawns are `DontDestroyOnLoad` so they survive scene transitions. However:

1. FishNet's `ManagedObjects.SubscribeToSceneLoaded(true)` subscribes to `SceneManager.sceneLoaded` internally.
2. On scene load, `SceneManager_sceneLoaded` reconciles the `Spawned` dictionary — it may clear or reassign ObjectIds for objects that FishNet considers scene-bound.
3. `ServerObjects.RecentlyDespawnedIds` may temporarily block the original ObjectId from being reused.
4. The clone's `NetworkObject` component holds a `sceneId` that may reference the old scene, causing FishNet to misroute network messages.
5. Result: after a scene transition, `nm.ServerManager.Objects.Spawned.ContainsKey(slot.NetworkObjectId)` may be `false`, meaning the clone is no longer registered.

### Pending fix approach (not yet implemented)

**Option A — Detect and re-register after load (lower risk):**

In `SceneWatcher`, subscribe to `SceneManager.sceneLoaded`:

```csharp
// In SceneWatcher.Awake():
SceneManager.sceneLoaded += OnSceneLoaded;

private void OnSceneLoaded(Scene scene, LoadSceneMode mode)
{
    if (!IsInGameplayScene) return;
    // Give FishNet one frame to finish its own sceneLoaded handler.
    StartCoroutine(RecheckCloneRegistrations());
}

private IEnumerator RecheckCloneRegistrations()
{
    yield return null;  // one frame
    var nm = UObject.FindObjectOfType<NetworkManager>();
    if (nm == null || !nm.IsServerStarted) yield break;

    for (int i = 1; i <= 3; i++)
    {
        var slot = LocalPlayerManager.Instance?.GetSlot(i);
        if (slot?.Pawn == null) continue;
        if (slot.NetworkObjectId >= 0 &&
            nm.ServerManager.Objects.Spawned.ContainsKey(slot.NetworkObjectId))
            continue;  // still registered, no action needed
        // Clone lost its registration — re-register (GO still exists).
        SpawnHooks.DespawnPlayerForSlot(i);   // cleans slot.NetworkObjectId
        SpawnHooks.SpawnPlayerForSlot(i);     // calls TryNetworkSpawnPawn again
    }
}
```

**Option B — Move into new scene before FishNet processes it (experimental):**

Subscribe to `SceneManager.sceneLoaded` with a very early priority and call:
```csharp
SceneManager.MoveGameObjectToScene(slot.Pawn, scene);
```
This re-parents the DontDestroyOnLoad clone into the new scene so FishNet's `SceneObjects` tracking sees it as a scene object. Risk: FishNet may not expect a previously-runtime-spawned object to become a scene object mid-session.

**Recommended:** Implement Option A first. It is additive (re-spawn if missing) and uses only the already-tested `SpawnPlayerForSlot` path. Option B is a future optimization if re-spawning is observable to players.

---

## Sled Physics Gap

`PlayerSledController.RpcLogic___Cmd_Sled___` is the target. With the current setup:

- Clone has `_networkObject` wired (TryNetworkSpawnPawn succeeded)
- `IsOwner` returns `true` (FishNetOwnershipPatcher)
- `PlayerSledController` is in `ConflictingTypeNames` (full suppression), so its `Awake/Start/Update` don't run on clones — but `RpcLogic___` is a plain method call, not a lifecycle callback, so it can still be invoked via reflection

**The outstanding uncertainty:** whether `PlayerSledController` has a valid internal state to call `ServerManager.Spawn(sledNOB, ...)` from inside `RpcLogic___`. Its `Awake`/`Start` are suppressed, which means any internal initialization that normally happens there (fetching the sled prefab reference, setting up the component) has not run on the clone.

**Verification steps:**
1. Log the return value of the reflected `RpcLogic___Cmd_Sled___` call in `LocalCoopActions`.
2. Check whether `PlayerSledController` stores the sled prefab as a field set in Inspector (serialized, so present without Awake) or set in Awake/Start (suppressed, so null on clone).
3. If the field is null: patch the field assignment out of Awake via reflection before suppression, or read the value from P1's `PlayerSledController` instance and copy it to the clone's component.
4. Check the sled prefab's `PrefabId` — the same `_prefabs` reflection used in `FindPlayerPrefab` should work if the sled prefab is registered with the NetworkManager.

**Local fallback (current active behavior):** `LocalCoopActions` spawns a visual-only sled cube as a placeholder when the sled action fires and the clone is offline or the networked call fails.

---

## IL2CPP Constraints Summary (networking-relevant)

| API | Status | Notes |
|-----|--------|-------|
| `GetComponent<T>()` (compile-time T) | Safe | Only works for types known at compile time |
| `GetComponent(string)` | Broken — returns null | No exception, silent failure |
| `GetComponent(Type)` | Broken | Same as string overload |
| `GetComponents<T>()` | Crashes — uncatchable `MissingMethodException` | Bypasses managed try-catch; hard freeze |
| `FindObjectOfType<T>()` (singular) | Safe | Use for NetworkManager, Canvas, etc. |
| `FindObjectsOfType<T>()` (plural) | Crashes — uncatchable | Same freeze as GetComponents |
| `GetComponentsInChildren<T>` | Crashes | Never use |
| `Type.GetMethod(name, flags)` | Safe | Use for all reflection on Assembly-CSharp types |
| `AccessTools.Method(type, name)` | Use with caution | Falls back to full assembly scan on miss; use `PatchHelpers.FindMethod` wrapper instead |
