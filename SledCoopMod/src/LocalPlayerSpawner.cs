using System;
using FishNet.Managing;
using FishNet.Object;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace SledCoopMod
{
    public static class LocalPlayerSpawner
    {
        public static GameObject? SpawnLocalPlayer(LocalPlayerSlot slot, GameObject sourcePawn)
        {
            if (slot == null || sourcePawn == null)
                return null;

            Vector3 spawnPos = sourcePawn.transform.position + sourcePawn.transform.right * (slot.SlotIndex * 3f);
            Quaternion spawnRot = sourcePawn.transform.rotation;

            GameObject? pawnClone = CreateLocalPawn(sourcePawn, spawnPos, spawnRot);
            if (pawnClone == null)
            {
                Plugin.Log.LogError($"[LocalPlayerSpawner] Failed to spawn pawn for slot {slot.SlotIndex}.");
                return null;
            }

            pawnClone.name = $"SledCoopP{slot.SlotIndex}";

            TryPrepareLocalPawn(slot, pawnClone);
            UObject.DontDestroyOnLoad(pawnClone);

            return pawnClone;
        }

        private static GameObject? CreateLocalPawn(GameObject sourcePawn, Vector3 position, Quaternion rotation)
        {
            GameObject? clone = null;
            try
            {
                if (!OfflineModeManager.CustomServerActive)
                {
                    NetworkObject? prefab = SpawnHooks.FindPlayerPrefab(sourcePawn);
                    if (prefab != null)
                    {
                        clone = UObject.Instantiate(prefab.gameObject, position, rotation);
                        Plugin.Log.LogInfo($"[LocalPlayerSpawner] Spawned local pawn from prefab '{prefab.gameObject.name}'.");
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] Prefab instantiate failed: {e.Message}");
            }

            if (clone == null)
            {
                try
                {
                    clone = UObject.Instantiate(sourcePawn, position, rotation);
                    Plugin.Log.LogWarning("[LocalPlayerSpawner] Prefab not available or local-only mode active; fell back to cloning host pawn.");
                }
                catch (Exception e)
                {
                    Plugin.Log.LogError($"[LocalPlayerSpawner] Host clone failed: {e.Message}");
                    return null;
                }
            }

            return clone;
        }

        // Component types whose Awake/Start are suppressed on clone pawns but may hold
        // Inspector-assigned fields that reference OTHER runtime objects (not prefabs).
        // Serialized fields survive Instantiate, but fields written in Awake/Start that
        // reference scene objects or other runtime components will be null on the clone.
        // We copy non-null values from the host to the clone so RpcLogic___ methods that
        // check these internal references can still fire (e.g. PlayerSledController needs
        // its sled-prefab NeworkObject reference to call ServerManager.Spawn).
        private static readonly string[] _hostCopyComponentTypeNames =
        {
            "PlayerSledController",
        };

        // ── Runtime AOT-overload probe ────────────────────────────────────────────
        // GameObject.GetComponents<Behaviour>() is presumed present in this AOT image
        // (used in PlayerSocialPatches.cs after a clean host spawn) but had not been
        // verified on the freeze-critical clone-spawn path.  Probe once at PreResolve
        // time on a throwaway GameObject — if it throws, skip TryCopyHostComponentFields
        // entirely so the spawn path never raises a MissingMethodException that could
        // corrupt IL2CPP trampoline state and freeze the next AddComponent call.
        //
        // CRITICAL: do NOT reference GetComponents<Component>() (or any other
        // never-instantiated generic GameObject overload) anywhere in this source
        // file. The IL2CPP runtime binds generic instantiations at method-load time,
        // and a missing overload throws MissingMethodException BEFORE the enclosing
        // try block is entered — taking down the entire plugin load.
        // Wrap each suspect call site in its own helper method so the binder only
        // touches the offending overload when that helper is invoked.
        private static bool _hostFieldCopyEnabled;

        public static void PreResolve()
        {
            _hostFieldCopyEnabled = false;
            UnityEngine.GameObject? probe = null;
            try
            {
                probe = new UnityEngine.GameObject("SledCoopProbe");
                probe.SetActive(false);
                _hostFieldCopyEnabled = TryProbeGetComponentsBehaviour(probe);
                Plugin.Log.LogInfo($"[LocalPlayerSpawner] AOT probe: GetComponents<Behaviour> {(_hostFieldCopyEnabled ? "present" : "missing")} → host-field-copy enabled={_hostFieldCopyEnabled}.");
            }
            catch (Exception e)
            {
                _hostFieldCopyEnabled = false;
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] AOT probe outer threw ({e.GetType().Name}: {e.Message}); host-field-copy disabled.");
            }
            finally
            {
                if (probe != null) try { UnityEngine.Object.Destroy(probe); } catch { }
            }
        }

        // Isolated helper: only this method body causes the IL2CPP binder to resolve
        // GameObject.GetComponents<Behaviour>().  If the overload is missing the
        // exception fires at the call boundary, where the catch in PreResolve sees it.
        private static bool TryProbeGetComponentsBehaviour(UnityEngine.GameObject probe)
        {
            try
            {
                var arr = probe.GetComponents<UnityEngine.Behaviour>();
                return arr != null;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] GetComponents<Behaviour>() probe threw: {e.Message}");
                return false;
            }
        }

        private static void TryPrepareLocalPawn(LocalPlayerSlot slot, GameObject pawn)
        {
            // Disable camera/audio-listener conflicts before anything else.
            SpawnHooks.DisableConflictingPawnComponents(pawn);

            // Preferred path: register the clone's existing NetworkObject with FishNet's
            // ServerManager so that NetworkBehaviour._networkObject is valid on all game
            // components.  This lets RpcLogic methods call ServerManager.Spawn for child
            // objects (sled physics, snowballs) with a real owner context.
            // FishNet network lifecycle callbacks (OnStartServer, OnStartClient, etc.)
            // are suppressed for "SledCoopP*" roots by Patch_ClonePawnScriptSuppressor,
            // so game-specific startup code won't run — only FishNet's own internal
            // _networkObject assignment fires.
            //
            // Fallback: if the server isn't started (pre-lobby / pure offline), remove
            // the NetworkObject only.  Do NOT call DisableFishNetBehaviours — it uses
            // GetComponents<Behaviour>() which throws an uncatchable MissingMethodException
            // in this Unity 6 IL2CPP / Il2CppInterop build (same class as the
            // FindObjectsOfType<T>() crash documented in FindBestHostCamera).
            // In the local-only path ServerManager.Spawn was never called, so FishNet's
            // TimeManager holds no tick-delegate subscriptions for this object.
            // Patch_ClonePawnScriptSuppressor suppresses Update/FixedUpdate on all
            // SledCoopP* objects as the remaining safeguard.
            bool networkSpawned = TryNetworkSpawnPawn(pawn);
            if (!networkSpawned)
            {
                RemoveNetworkObjectFromHierarchy(pawn.transform);
                // DisableFishNetBehaviours intentionally NOT called — see comment above.
            }

            // Copy runtime-assigned (non-serialized) fields from the host pawn's
            // components to the corresponding clone components.  Suppressed Awake/Start
            // never ran on the clone, so scene-object and runtime-component references
            // that would normally be set there are null.
            LocalPlayerSlot? slot0 = LocalPlayerManager.Instance?.GetSlot(0);
            if (slot0?.Pawn != null && _hostFieldCopyEnabled)
                TryCopyHostComponentFields(pawn, slot0.Pawn);

            if (slot.LocalInputProvider == null)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] Slot {slot.SlotIndex} has no LocalInputProvider at prepare time.");
            }
            else
            {
                LocalCoopMovement.PendingProviders[pawn.GetInstanceID()] = slot.LocalInputProvider;
                Plugin.Log.LogInfo($"[LocalPlayerSpawner] Queued input provider for pawn id={pawn.GetInstanceID()} slot={slot.SlotIndex} device={slot.LocalInputProvider.Device}.");
            }

            // Each AddComponent is wrapped independently so one failure doesn't
            // prevent the rest from being added.
            try
            {
                pawn.AddComponent<LocalCoopMovement>();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] AddComponent<LocalCoopMovement> failed: {e.Message}");
            }

            try
            {
                pawn.AddComponent<LocalCharacterVisualController>();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] AddComponent<LocalCharacterVisualController> failed: {e.Message}");
            }

            try
            {
                pawn.AddComponent<LocalCoopActions>();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] AddComponent<LocalCoopActions> failed: {e.Message}");
            }

            try
            {
                var localPlayerComponent = pawn.AddComponent<LocalPlayer>();
                if (localPlayerComponent != null)
                    localPlayerComponent.Initialize(slot);
                else
                    Plugin.Log.LogWarning("[LocalPlayerSpawner] LocalPlayer component could not be added to pawn.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] AddComponent<LocalPlayer> failed: {e.Message}");
            }

            try
            {
                var tracker = pawn.AddComponent<GuestRacingTracker>();
                tracker?.Initialize(slot.SlotIndex);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] AddComponent<GuestRacingTracker> failed: {e.Message}");
            }

            if (!pawn.activeInHierarchy)
            {
                pawn.SetActive(true);
                Plugin.Log.LogInfo($"[LocalPlayerSpawner] Activated pawn GameObject for slot {slot.SlotIndex}.");
            }

            // Re-activate the character model child in case any script hid it during Awake/OnEnable.
            // Harmony patches (Patch_ClonePawnScriptSuppressor) prevent PlayerRagdollTypeHandler
            // and others from hiding it again in subsequent frames.
            try
            {
                EnablePlayerModel(pawn);
                SpawnHooks.EnsurePawnVisuals(pawn, $"slot {slot.SlotIndex}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] EnablePlayerModel threw: {e.Message}");
            }

            Plugin.Log.LogInfo($"[LocalPlayerSpawner] Prepared local pawn for slot {slot.SlotIndex}.");
        }

        /// <summary>
        /// For each type in <see cref="_hostCopyComponentTypeNames"/>, copies any
        /// non-null object-reference fields from the host pawn's component to the
        /// clone's corresponding component.  Only fields that are null on the clone
        /// are overwritten, so serialized (Inspector-assigned) values are preserved.
        ///
        /// This compensates for suppressed Awake/Start on the clone: any field that
        /// the game normally sets in Awake by calling GetComponent / FindObjectOfType
        /// on the live scene will be null on the clone, causing RpcLogic___ methods
        /// to bail early.
        /// </summary>
        private static void TryCopyHostComponentFields(GameObject clone, GameObject host)
        {
            // FORBIDDEN in this IL2CPP/Wine build:
            //   - GameObject.GetComponent(Type)            — uncatchable MissingMethod
            //   - GameObject.GetComponents<Component>()    — parameterless <Component> overload missing
            //   - GameObject.GetComponentsInChildren(bool) — bool overload missing
            // VERIFIED PRESENT (used at runtime in PlayerSocialPatches.cs):
            //   - GameObject.GetComponents<Behaviour>()    — parameterless <Behaviour> overload
            //
            // Every type we care about copying (PlayerControl, FishNet NetworkBehaviours,
            // PlayerHoldingController, PlayerBuildingController, …) derives from Behaviour,
            // so this overload covers the field-copy use case.  Resolve each target type
            // by IsAssignableFrom in managed code; never call GetComponent(Type).
            Behaviour[] hostComponents;
            Behaviour[] cloneComponents;
            try
            {
                hostComponents  = host.GetComponents<Behaviour>();
                cloneComponents = clone.GetComponents<Behaviour>();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] GetComponents<Behaviour>() threw: {e.Message}");
                return;
            }

            foreach (string typeName in _hostCopyComponentTypeNames)
            {
                Type? compType = null;
                try { compType = SledCoopMod.Patches.PatchHelpers.SafeTypeByName(typeName); } catch { }
                if (compType == null) continue;

                Behaviour? hostComp  = FindComponentOfType(hostComponents,  compType);
                Behaviour? cloneComp = FindComponentOfType(cloneComponents, compType);
                if (hostComp == null || cloneComp == null) continue;

                int copied = 0;
                var fields = compType.GetFields(
                    System.Reflection.BindingFlags.Public |
                    System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Instance);

                foreach (var f in fields)
                {
                    if (f.FieldType.IsValueType) continue; // skip structs / primitives
                    try
                    {
                        object? cloneVal = f.GetValue(cloneComp);
                        if (cloneVal != null) continue; // already set — keep it
                        object? hostVal = f.GetValue(hostComp);
                        if (hostVal == null) continue;
                        f.SetValue(cloneComp, hostVal);
                        copied++;
                    }
                    catch { }
                }

                if (copied > 0 || ModConfig.VerboseLogging.Value)
                    Plugin.Log.LogInfo(
                        $"[LocalPlayerSpawner] Copied {copied} field(s) from host {typeName} → clone.");
            }
        }

        // Find the first component in the array whose runtime type equals (or derives
        // from) compType.  IL2CPP-safe: never calls GetComponent(Type).  Uses Behaviour[]
        // because GetComponents<Component>() is missing from this AOT image.
        private static Behaviour? FindComponentOfType(Behaviour[] comps, Type compType)
        {
            if (comps == null) return null;
            foreach (var c in comps)
            {
                if (c == null) continue;
                Type ct;
                try { ct = c.GetType(); } catch { continue; }
                if (ct == compType || compType.IsAssignableFrom(ct)) return c;
            }
            return null;
        }

        /// <summary>
        /// Tries to register the clone pawn's existing NetworkObject with FishNet's
        /// ServerManager (null owner = server-owned, correct for offline host mode).
        ///
        /// On success: all NetworkBehaviours on the clone gain a valid _networkObject
        /// reference so RpcLogic methods (sled, snowball, building) can call
        /// ServerManager.Spawn for child objects with a real owner context.
        ///
        /// On failure: returns false; caller removes the NetworkObject from the hierarchy
        /// (RemoveNetworkObjectFromHierarchy) but does NOT call DisableFishNetBehaviours —
        /// that function uses GetComponents&lt;Behaviour&gt;() which crashes this IL2CPP build.
        ///
        /// Prerequisites: the pawn must already be named "SledCoopP*" so the
        /// Patch_ClonePawnScriptSuppressor guards fire during ServerManager.Spawn's
        /// internal OnStartServer / OnStartClient / OnStartNetwork calls.
        /// </summary>
        // Force local-only mode for clone pawns.  Network-spawning the clone causes a
        // hard freeze on host startup: FishNet fires PlayerSledController.OnStartClient
        // on the loopback client which auto-issues CmdSpawnSled for the clone, and the
        // FishNet-woven RpcLogic___CmdSpawnSled enters a state that hangs the player
        // loop.  HarmonyX in this IL2CPP/Il2CppInterop/Wine build cannot intercept the
        // woven Cmd*/RpcWriter___/RpcLogic___ methods (verified across multiple runs:
        // those patches load successfully but their Prefix never fires), and the woven
        // body does NOT call the regular [Server] Server_EquipSled workhorse — so we
        // can't surgically intercept either path.
        //
        // The only reliable cut-point is upstream: never give the clone a live
        // NetworkObject in the first place.  Returning false here makes the caller
        // strip the NetworkObject component from the clone hierarchy via
        // RemoveNetworkObjectFromHierarchy, after which FishNet ignores the clone
        // entirely and OnStartClient never fires.
        //
        // Trade-off: networked sled / snowball / buildable spawning for guests is
        // unavailable in this mode.  Those features are TBD pending a working hook
        // for FishNet's woven RPCs in this IL2CPP build.
        private const bool ForceLocalOnlyClones = true;

        private static bool TryNetworkSpawnPawn(GameObject pawn)
        {
            if (ForceLocalOnlyClones)
            {
                Plugin.Log.LogInfo($"[LocalPlayerSpawner] '{pawn.name}' kept local-only (ForceLocalOnlyClones=true).");
                return false;
            }

            try
            {
                var nm = UObject.FindFirstObjectByType<NetworkManager>();
                if (nm == null)
                {
                    Plugin.Log.LogInfo("[LocalPlayerSpawner] NetworkManager absent; using local-only path.");
                    return false;
                }

                if (!nm.IsServerStarted)
                {
                    Plugin.Log.LogInfo("[LocalPlayerSpawner] Server not started; using local-only path.");
                    return false;
                }

                NetworkObject? nob = pawn.GetComponent<NetworkObject>();
                if (nob == null)
                {
                    Plugin.Log.LogWarning("[LocalPlayerSpawner] Clone has no NetworkObject; using local-only path.");
                    return false;
                }

                if (nob.IsSpawned)
                {
                    Plugin.Log.LogInfo($"[LocalPlayerSpawner] '{pawn.name}' already network-spawned (ObjectId={nob.ObjectId}).");
                    return true;
                }

                // Spawn server-owned (null owner). Using the host's ClientManager.Connection
                // as owner would register two objects under the same connection (host pawn +
                // clone), which confuses FishNet's per-connection ownership tables.
                // FishNetOwnershipPatcher patches IsOwner/IsServerInitialized/IsClientInitialized
                // to return true for any SledCoopP* object, so correct FishNet ownership
                // behavior is guaranteed without assigning the clone to the host connection.
                nm.ServerManager.Spawn(nob, null);
                Plugin.Log.LogInfo($"[LocalPlayerSpawner] Network-spawned '{pawn.name}', ObjectId={nob.ObjectId}, owner=server.");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] TryNetworkSpawnPawn failed: {e.Message}");
                // If the spawn partially completed (nob.IsSpawned became true before the
                // exception), clean up via Despawn so FishNet's object tables stay consistent.
                try
                {
                    var nm2 = UObject.FindFirstObjectByType<NetworkManager>();
                    NetworkObject? nob2 = pawn.GetComponent<NetworkObject>();
                    if (nm2 != null && nob2 != null && nob2.IsSpawned && nm2.IsServerStarted)
                    {
                        nm2.ServerManager.Despawn(nob2);
                        Plugin.Log.LogInfo($"[LocalPlayerSpawner] Despawned partially-spawned '{pawn.name}' after failed spawn.");
                    }
                }
                catch { }
                return false;
            }
        }

        // INTENTIONAL NO-OP: GetComponents<Behaviour>() / GetComponents<T>() throws an
        // uncatchable MissingMethodException in this Unity 6 IL2CPP / Il2CppInterop build
        // (the exception bypasses managed try-catch and propagates into native IL2CPP code,
        // causing a hard freeze).  This method is kept as a named stub so future callers
        // are explicit.  In the local-only path (where it was previously called) FishNet's
        // TimeManager never subscribed to the object's ticks, so disabling behaviours is
        // unnecessary. Patch_ClonePawnScriptSuppressor suppresses Update/FixedUpdate on all
        // SledCoopP* roots instead.
        private static void DisableFishNetBehaviours(Transform t) { }  // safe no-op

        // Only GetComponent<T>() for specific known types works in this IL2CPP build.
        // NetworkObject (FishNet) is referenced at compile time, so this call is safe.
        // Other conflicting scripts are handled by Harmony in DiagnosticPatches.cs.
        private static void RemoveNetworkObjectFromHierarchy(Transform t)
        {
            try
            {
                NetworkObject? no = t.gameObject.GetComponent<NetworkObject>();
                if (no != null)
                {
                    UObject.DestroyImmediate(no);
                    Plugin.Log.LogInfo($"[LocalPlayerSpawner] Destroyed NetworkObject on '{t.gameObject.name}'.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] RemoveNetworkObject failed on '{t.gameObject.name}': {e.Message}");
            }

            for (int i = 0; i < t.childCount; i++)
                RemoveNetworkObjectFromHierarchy(t.GetChild(i));
        }

        private static void EnablePlayerModel(GameObject pawn)
        {
            try
            {
                GameObject? charGo = null;

                // Try well-known child names first (game uses 'Graphics (all characters start enabled)').
                foreach (string cname in new[] { "Graphics (all characters start enabled)", "character", "Character", "CharacterRoot", "Model", "model" })
                {
                    Transform? t = pawn.transform.Find(cname);
                    if (t != null) { charGo = t.gameObject; break; }
                }

                // Fall back: first inactive child with a mesh renderer anywhere in its subtree.
                // Skips physics-only nodes like 'Non Local Ragdoll Pull' that have no renderer.
                if (charGo == null)
                    charGo = FindFirstInactiveChildWithMesh(pawn.transform);

                if (charGo != null)
                {
                    if (!charGo.activeSelf)
                    {
                        charGo.SetActive(true);
                        Plugin.Log.LogInfo($"[LocalPlayerSpawner] Activated character GO '{charGo.name}'.");
                    }
                    else
                    {
                        Plugin.Log.LogInfo($"[LocalPlayerSpawner] Character GO '{charGo.name}' already active.");
                    }
                }
                else
                {
                    Plugin.Log.LogWarning($"[LocalPlayerSpawner] Could not find character child with mesh on '{pawn.name}'.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[LocalPlayerSpawner] EnablePlayerModel threw: {e.Message}");
            }
        }

        private static bool HasMeshInSubtree(Transform t)
        {
            // Generic GetComponent<T>() is AOT-compiled and works in this IL2CPP build.
            // String and Type overloads both crash (ReadOnlySpan path is missing).
            if (t.GetComponent<SkinnedMeshRenderer>() != null) return true;
            if (t.GetComponent<MeshRenderer>() != null) return true;
            for (int i = 0; i < t.childCount; i++)
                if (HasMeshInSubtree(t.GetChild(i))) return true;
            return false;
        }

        private static GameObject? FindFirstInactiveChildWithMesh(Transform t)
        {
            for (int i = 0; i < t.childCount; i++)
            {
                Transform child = t.GetChild(i);
                if (!child.gameObject.activeSelf && HasMeshInSubtree(child))
                    return child.gameObject;
            }
            // Descend into active children.
            for (int i = 0; i < t.childCount; i++)
            {
                if (t.GetChild(i).gameObject.activeSelf)
                {
                    var found = FindFirstInactiveChildWithMesh(t.GetChild(i));
                    if (found != null) return found;
                }
            }
            return null;
        }
    }
}
