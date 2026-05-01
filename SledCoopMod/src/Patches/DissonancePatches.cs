using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SledCoopMod.Patches
{
    // Dissonance voice networking is not needed for same-machine local coop. In
    // the networked-instance Tugboat path the fresh crash log shows Dissonance
    // repeatedly throwing from PlayerVoiceController.CheckShouldCommsBeDisabled,
    // disconnecting, and restarting the voice client immediately after P2 joins.
    // Keep voice completely quiet in mod-owned local sessions.
    internal static class DissonancePatchHelpers
    {
        private static readonly System.Collections.Generic.HashSet<string> _logged =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        internal static bool AllowWhenOnline(string site)
        {
            if (!ShouldSuppressLocalVoice())
                return true;

            if (_logged.Add(site))
                Plugin.Log.LogInfo($"[DissonancePatches] {site} skipped for local coop network mode.");
            return false;
        }

        internal static bool ShouldSuppressLocalVoice()
        {
            return NetworkedInstanceManager.IsNetworkedModeConfigured
                || OfflineModeManager.OfflineModeActive
                || OfflineModeManager.CustomServerRequested;
        }

        internal static bool IsPlayerIdNullArgument(Exception e)
        {
            if (e is ArgumentNullException ane &&
                string.Equals(ane.ParamName, "playerId", StringComparison.Ordinal))
                return true;

            if (e.InnerException != null && IsPlayerIdNullArgument(e.InnerException))
                return true;

            try
            {
                string text = e.ToString();
                return text.IndexOf("playerId", StringComparison.OrdinalIgnoreCase) >= 0
                    && (text.IndexOf("Value cannot be null", StringComparison.OrdinalIgnoreCase) >= 0
                        || text.IndexOf("ArgumentNullException", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { return false; }
        }

        internal static MethodBase? Find(string typeName, params string[] methodNames)
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName(typeName);
                return t == null ? null : PatchHelpers.FindMethod(t, methodNames);
            }
            catch { return null; }
        }

        internal static MethodBase? FindInherited(string typeName, string methodName)
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName(typeName);
                return t == null ? null : PatchHelpers.FindMethodInherited(t, methodName);
            }
            catch { return null; }
        }
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceComms_Start_OfflineOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.DissonanceComms", "Start");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceComms.Start");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceComms_Update_OfflineOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.DissonanceComms", "Update");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceComms.Update");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceComms_FindPlayer_NullGuard
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.DissonanceComms", "FindPlayer");

        [HarmonyPrefix]
        static bool Prefix(string playerId, ref object __result)
        {
            if (!DissonancePatchHelpers.ShouldSuppressLocalVoice() || !string.IsNullOrWhiteSpace(playerId))
                return true;

            __result = null!;
            DissonancePatchHelpers.AllowWhenOnline("DissonanceComms.FindPlayer(null)");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceProximityTriggers_Update_LocalOnly
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string typeName in new[]
            {
                "Dissonance.BaseCommsTrigger",
                "Dissonance.VoiceBroadcastTrigger",
                "Dissonance.VoiceReceiptTrigger",
                "Dissonance.VoiceProximityBroadcastTrigger",
                "Dissonance.VoiceProximityReceiptTrigger",
            })
            {
                var method = DissonancePatchHelpers.Find(typeName, "Update");
                if (method != null && !method.ContainsGenericParameters)
                    yield return method;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod) =>
            DissonancePatchHelpers.AllowWhenOnline(
                $"DissonanceTrigger.{__originalMethod.DeclaringType?.Name}.Update");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetComms_CreateServer_OfflineOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetComms", "CreateServer");

        [HarmonyPrefix]
        static bool Prefix(ref object __result)
        {
            if (DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetComms.CreateServer"))
                return true;
            __result = null!;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetComms_Awake_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetComms", "Awake");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetComms.Awake");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetComms_OnEnable_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetComms", "OnEnable");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetComms.OnEnable");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetComms_Update_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.FindInherited("Dissonance.Integrations.FishNet.DissonanceFishNetComms", "Update");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetComms.Update");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetComms_CreateClient_OfflineOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetComms", "CreateClient");

        [HarmonyPrefix]
        static bool Prefix(ref object __result)
        {
            if (DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetComms.CreateClient"))
                return true;
            __result = null!;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_OnOwnershipClient_OfflineOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer", "OnOwnershipClient");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.OnOwnershipClient");
    }

    [HarmonyPatch]
    internal static class Patch_PlayerVoiceController_Awake_OfflineOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("PlayerVoiceController", "Awake");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("PlayerVoiceController.Awake");
    }

    [HarmonyPatch]
    internal static class Patch_PlayerVoiceController_Start_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("PlayerVoiceController", "Start");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("PlayerVoiceController.Start");
    }

    [HarmonyPatch]
    internal static class Patch_PlayerVoiceController_CheckShouldCommsBeDisabled_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("PlayerVoiceController", "CheckShouldCommsBeDisabled");

        [HarmonyPrefix]
        static bool Prefix(ref bool __result)
        {
            if (!DissonancePatchHelpers.ShouldSuppressLocalVoice())
                return true;

            __result = true;
            DissonancePatchHelpers.AllowWhenOnline("PlayerVoiceController.CheckShouldCommsBeDisabled");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerVoiceController_OnPlayerJoinedSession_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("PlayerVoiceController", "OnPlayerJoinedSession");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("PlayerVoiceController.OnPlayerJoinedSession");
    }

    // ────────────────────────────────────────────────────────────────────────
    // DissonanceFishNetPlayer SyncVar / RPC / lifecycle methods.
    //
    // These fire from FishNet's <Iterate>g__ProcessObject path when a remote
    // player NetworkObject is spawned/synced. Their inner code calls
    // Dissonance.DissonanceComms.Get/Find/Remove(string playerId), which throws
    // ArgumentNullException("playerId") when our networked instances supply
    // an empty/null synced player name. The trace is suppressed but the
    // single-line "ArgumentNullException: Value cannot be null. Parameter
    // name: playerId" floods Player.log. Block the methods entirely while in
    // local-coop networked mode — voice features are intentionally disabled.
    // ────────────────────────────────────────────────────────────────────────

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_OnSyncedPlayerNameUpdated_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer", "OnSyncedPlayerNameUpdated");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.OnSyncedPlayerNameUpdated");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_SetPlayerName_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer", "SetPlayerName");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.SetPlayerName");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_ServerRpcSetPlayerName_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer", "ServerRpcSetPlayerName");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.ServerRpcSetPlayerName");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_RpcLogicSetPlayerName_LocalOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer");
                if (t == null) return null;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name.StartsWith("RpcLogic___ServerRpcSetPlayerName", StringComparison.Ordinal))
                        return m;
                }
            }
            catch { }
            return null;
        }

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.RpcLogic_ServerRpcSetPlayerName");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_RpcReaderSetPlayerName_LocalOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer");
                if (t == null) return null;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name.StartsWith("RpcReader___ServerRpcSetPlayerName", StringComparison.Ordinal))
                        return m;
                }
            }
            catch { }
            return null;
        }

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.RpcReader_ServerRpcSetPlayerName");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_ManageTrackingState_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer", "ManageTrackingState");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.ManageTrackingState");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_TryStopTrackingImmediate_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer", "TryStopTrackingImmediate");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.TryStopTrackingImmediate");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_AwakeUserLogic_LocalOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer");
                if (t == null) return null;
                foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                {
                    if (m.Name.StartsWith("Awake_UserLogic_", StringComparison.Ordinal))
                        return m;
                }
            }
            catch { }
            return null;
        }

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.AwakeUserLogic");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_Awake_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer", "Awake");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.Awake");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_OnEnable_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer", "OnEnable");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.OnEnable");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_OnDisable_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer", "OnDisable");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.OnDisable");
    }

    [HarmonyPatch]
    internal static class Patch_DissonanceFishNetPlayer_OnDestroy_LocalOnly
    {
        static MethodBase? TargetMethod() =>
            DissonancePatchHelpers.Find("Dissonance.Integrations.FishNet.DissonanceFishNetPlayer", "OnDestroy");

        [HarmonyPrefix]
        static bool Prefix() => DissonancePatchHelpers.AllowWhenOnline("DissonanceFishNetPlayer.OnDestroy");
    }

    // Catch-all finalizer: any remaining Dissonance method that throws an
    // ArgumentNullException("playerId") gets swallowed silently in local mode.
    // Targets the most common entry points where Dissonance.DissonanceComms or
    // its player collection is touched with a null/empty id.
    [HarmonyPatch]
    internal static class Patch_DissonanceComms_PlayerIdFinalizers
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            string[] typeNames =
            {
                "Dissonance.DissonanceComms",
                "Dissonance.Players.LocalVoicePlayerState",
                "Dissonance.Players.RemoteVoicePlayerState",
                "Dissonance.Audio.Playback.PlaybackPool",
            };
            string[] methodNames =
            {
                "FindPlayer", "Get", "Remove", "TryGet",
                "Net_PlayerJoined", "Net_PlayerLeft",
                "OnPlayerJoinedSession", "OnPlayerLeftSession",
            };

            foreach (var typeName in typeNames)
            {
                var t = PatchHelpers.SafeTypeByName(typeName);
                if (t == null) continue;
                foreach (var name in methodNames)
                {
                    var m = PatchHelpers.FindMethod(t, name);
                    if (m != null) yield return m;
                }
            }
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            if (!DissonancePatchHelpers.ShouldSuppressLocalVoice()) return __exception;
            if (!DissonancePatchHelpers.IsPlayerIdNullArgument(__exception))
                return __exception;

            DissonancePatchHelpers.AllowWhenOnline(
                $"DissonancePlayerIdFinalizer.{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}");
            return null;
        }
    }
}
