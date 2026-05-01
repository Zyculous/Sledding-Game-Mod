using System;
using System.Reflection;
using Cysharp.Threading.Tasks;
using HarmonyLib;

namespace SledCoopMod.Patches
{
    [HarmonyPatch]
    internal static class Patch_CargoUsers_IsInitialized_Networked
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("cargo32.Port.API.Users");
            return t == null ? null : PatchHelpers.FindMethod(t, "get_IsInitialized");
        }

        [HarmonyPrefix]
        static bool Prefix(ref bool __result)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            __result = true;
            return false;
        }
    }

    internal static class NetworkedPlatformUserIds
    {
        private static bool _loggedSyntheticIds;

        internal static int GetSyntheticUserId()
        {
            int slot = Math.Max(0, NetworkedInstanceManager.Instance?.SlotIndex ?? 0);
            return 90_000 + slot;
        }

        internal static long GetSyntheticPlatformUserId()
        {
            int slot = Math.Max(0, NetworkedInstanceManager.Instance?.SlotIndex ?? 0);
            return 9_000_000L + slot;
        }

        internal static void LogSyntheticIdsOnce()
        {
            if (_loggedSyntheticIds) return;
            _loggedSyntheticIds = true;
            Plugin.Log.LogInfo(
                $"[NetworkedPlatformUser] Supplying synthetic platform user ids userId={GetSyntheticUserId()}, longUserId={GetSyntheticPlatformUserId()}.");
        }
    }

    [HarmonyPatch]
    internal static class Patch_CargoUsers_GetUserId_Networked
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("cargo32.Port.API.Users");
            return t == null ? null : PatchHelpers.FindMethod(t, "GetUserId");
        }

        [HarmonyPrefix]
        static bool Prefix(ref UniTask<int> __result)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            __result = UniTask.FromResult(NetworkedPlatformUserIds.GetSyntheticUserId());
            NetworkedPlatformUserIds.LogSyntheticIdsOnce();
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_CargoUsers_GetLongUserId_Networked
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("cargo32.Port.API.Users");
            return t == null ? null : PatchHelpers.FindMethod(t, "GetLongUserId");
        }

        [HarmonyPrefix]
        static bool Prefix(ref UniTask<long> __result)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            __result = UniTask.FromResult(NetworkedPlatformUserIds.GetSyntheticPlatformUserId());
            NetworkedPlatformUserIds.LogSyntheticIdsOnce();
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_CargoUsers_GetLocalUsername_Networked
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("cargo32.Port.API.Users");
            return t == null ? null : PatchHelpers.FindMethod(t, "GetLocalUsername");
        }

        [HarmonyPrefix]
        static bool Prefix(ref UniTask<string> __result)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            int slot = Math.Max(0, NetworkedInstanceManager.Instance?.SlotIndex ?? 0);
            string name = NetworkedPlayerIdentity.GetDisplayNameForConnectionId(slot);

            __result = UniTask.FromResult(name);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_CargoUsers_GetSessionTicket_Networked
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("cargo32.Port.API.Users");
            return t == null ? null : PatchHelpers.FindMethod(t, "GetSessionTicket");
        }

        [HarmonyPrefix]
        static bool Prefix(ref UniTask<string> __result)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            int slot = Math.Max(0, NetworkedInstanceManager.Instance?.SlotIndex ?? 0);
            __result = UniTask.FromResult($"sledcoop-session-{slot}");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_SteamUsersModule_CurrentId_Networked
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("cargo32.Port.Steam.Users.Steam_UsersModule");
            return t == null ? null : PatchHelpers.FindMethod(t, "get_CurrentID");
        }

        [HarmonyPrefix]
        static bool Prefix(ref long __result)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            __result = NetworkedPlatformUserIds.GetSyntheticPlatformUserId();
            NetworkedPlatformUserIds.LogSyntheticIdsOnce();
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_SteamUsersModule_GetLocalUserDisplayName_Networked
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("cargo32.Port.Steam.Users.Steam_UsersModule");
            return t == null ? null : PatchHelpers.FindMethod(t, "GetLocalUserDisplayName");
        }

        [HarmonyPrefix]
        static bool Prefix(ref UniTask<string> __result)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            int slot = Math.Max(0, NetworkedInstanceManager.Instance?.SlotIndex ?? 0);
            string name = NetworkedPlayerIdentity.GetDisplayNameForConnectionId(slot);

            __result = UniTask.FromResult(name);
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_SteamUsersModule_GetSessionTicket_Networked
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("cargo32.Port.Steam.Users.Steam_UsersModule");
            return t == null ? null : PatchHelpers.FindMethod(t, "GetSessionTicket");
        }

        [HarmonyPrefix]
        static bool Prefix(ref UniTask<string> __result)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            int slot = Math.Max(0, NetworkedInstanceManager.Instance?.SlotIndex ?? 0);
            __result = UniTask.FromResult($"sledcoop-session-{slot}");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_UiReferenceController_InitializeUserInfo_Networked
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("UiReferenceController");
            return t == null ? null : PatchHelpers.FindMethod(t, "InitializeUserInfoAsync");
        }

        [HarmonyPrefix]
        static bool Prefix(object __instance)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            TrySetUserText(__instance);
            Plugin.Log.LogInfo("[NetworkedPlatformUser] Skipped native user-info async lookup in networked local mode.");
            return false;
        }

        private static void TrySetUserText(object ui)
        {
            try
            {
                int slot = Math.Max(0, NetworkedInstanceManager.Instance?.SlotIndex ?? 0);
                string name = NetworkedPlayerIdentity.GetDisplayNameForConnectionId(slot);
                var textField = ui.GetType().GetField("bigNameForDemoReasons", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? text = textField?.GetValue(ui);
                text?.GetType().GetProperty("text", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)?.SetValue(text, name);
            }
            catch { }
        }
    }

    [HarmonyPatch]
    internal static class Patch_PrivilegeChecker_Update_Networked
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("_Scripts.Binary.PrivilegeChecker");
            return t == null ? null : PatchHelpers.FindMethod(t, "Update");
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            return !NetworkedInstanceManager.IsNetworkedModeConfigured;
        }
    }
}
