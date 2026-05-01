using System;
using System.Collections.Generic;
using UnityEngine;

namespace SledCoopMod
{
    public static class SpawnHooks
    {
        private static readonly Queue<GameObject> PendingHostPawns = new Queue<GameObject>();

        public static bool IsSpawningSelfManagedPawn => false;

        public static void PreResolve()
        {
        }

        public static void OnBootComplete()
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("SpawnHooks.OnBootComplete fired.");
        }

        public static void QueueHostPawn(GameObject pawn)
        {
            if (pawn == null)
                return;

            PendingHostPawns.Enqueue(pawn);
        }

        public static void ProcessPendingHostPawn()
        {
            while (PendingHostPawns.Count > 0)
            {
                GameObject pawn = PendingHostPawns.Dequeue();
                if (pawn == null)
                    continue;

                RegisterNativePawn(pawn);
            }
        }

        private static void RegisterNativePawn(GameObject pawn)
        {
            try
            {
                var slot0 = LocalPlayerManager.Instance?.GetSlot(0);
                if (slot0 == null)
                    return;

                if (slot0.Pawn == null)
                {
                    slot0.Pawn = pawn;
                    Plugin.Log.LogInfo($"[SpawnHooks] Registered native local pawn '{pawn.name}'.");
                    SceneWatcher.NotifyGameplayStarted();
                }
                else if (slot0.Pawn != pawn && ModConfig.VerboseLogging.Value)
                {
                    Plugin.Log.LogDebug($"[SpawnHooks] Ignoring remote FishNet pawn '{pawn.name}'.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[SpawnHooks] RegisterNativePawn threw: {e.Message}");
            }
        }

        public static void TryRegisterHostCamera()
        {
        }

        public static void ConsiderHudCanvas(Canvas canvas)
        {
        }

        public static void ConsiderCamera(object cameraLike)
        {
        }

        public static void SpawnPlayerForSlot(int slotIndex)
        {
            Plugin.Log.LogInfo($"[SpawnHooks] Legacy local clone spawn ignored for slot {slotIndex}; networked instances are the only supported path.");
        }

        public static void DespawnPlayerForSlot(int slotIndex)
        {
        }

        public static void DespawnExtraPlayers()
        {
        }
    }
}
