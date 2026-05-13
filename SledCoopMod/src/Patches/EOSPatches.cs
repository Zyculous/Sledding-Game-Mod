using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SledCoopMod.Patches
{
    // EOS suppression patches block functions that the game polls every
    // frame in some boot states (e.g. LoadDelegatesWithEOSBindingAPI runs
    // on every Update tick of the EOS loader). Logging at LogInfo per call
    // floods Player.log when VerboseLogging is on. Dedupe per call-site
    // so each block prints exactly once per process lifetime.
    internal static class EosBlockLogger
    {
        private static readonly HashSet<string> _logged =
            new HashSet<string>(StringComparer.Ordinal);

        internal static void LogOnce(string site)
        {
            if (!ModConfig.VerboseLogging.Value) return;
            if (!_logged.Add(site)) return;
            Plugin.Log.LogInfo($"[EOS] Blocking {site}.");
        }
    }

    // ─────────────────────────────────────────────────────────────────────────────
    // EOS / PlayEveryWare boot-time suppression patches
    //
    // These patches disable EOS platform config initialization and skip the game’s
    // EOS authenticator boot steps so the mod can start local offline custom servers
    // without invoking EOS/Steam startup paths.
    //
    // Patches whose target types live in lazily-loaded assemblies
    // (PlayEveryWare.EpicOnlineServices, EOSSDK CSharp wrapper, sample
    // assemblies) are tagged with [SledCoopEosPatch] so they are skipped in
    // the initial PatchAll pass and re-applied by EosLatePatcher when those
    // assemblies actually load.  Without this tag, every TargetMethod() that
    // calls SafeTypeByName at Plugin.Load resolves to null (the type cache
    // is built before the assembly is loaded) and Harmony silently skips
    // the patch.  The Assembly-CSharp targets (BootSceneManager,
    // EOSAuthenticator, ExternalAuthenticationManager, _Scripts.Managers
    // .LobbyManager) are NOT tagged because their types are present at
    // Plugin.Load.  FishyEOS patches are tagged too; the custom-server path now
    // swaps to FishNet Tugboat loopback before startup, so FishyEOS should never
    // be the offline transport.
    //
    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_PlayEveryWarePlatformManager_StaticCtor
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.PlatformManager");
                return t?.TypeInitializer;
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Suppressing PlayEveryWare.EpicOnlineServices.PlatformManager static initializer.");
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_PlayEveryWarePlatformManager_InitializePlatformConfigs
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.PlatformManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "InitializePlatformConfigs");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Suppressing PlatformManager.InitializePlatformConfigs.");
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_PlayEveryWarePlatformManager_GetPlatformConfig
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.PlatformManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "GetPlatformConfig");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref object __result)
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Suppressing PlatformManager.GetPlatformConfig.");
            __result = null;
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_PlayEveryWarePlatformManager_TryGetConfig
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.PlatformManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "TryGetConfig");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref object platformConfig, ref bool __result)
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Suppressing PlatformManager.TryGetConfig.");
            platformConfig = null;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_PlayEveryWarePlatformManager_GetConfigFilePath_NoArgs
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.PlatformManager");
                return t?.GetMethod("GetConfigFilePath", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly, null, Type.EmptyTypes, null);
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref string __result)
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Suppressing PlatformManager.GetConfigFilePath().");
            __result = null;
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_PlayEveryWarePlatformManager_GetConfigFilePath_WithPlatform
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.PlatformManager");
                if (t == null) return null;
                var platformType = t.GetNestedType("Platform", BindingFlags.Public | BindingFlags.NonPublic);
                return platformType == null
                    ? null
                    : t.GetMethod("GetConfigFilePath", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly, null, new[] { platformType }, null);
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref string __result)
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Suppressing PlatformManager.GetConfigFilePath(platform).");
            __result = null;
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_PlayEveryWarePlatformManager_TryGetConfigFilePath
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.PlatformManager");
                return t == null ? null : t.GetMethod("TryGetConfigFilePath", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref string configFilePath, ref bool __result)
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Suppressing PlatformManager.TryGetConfigFilePath.");
            configFilePath = null;
            __result = false;
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_PlayEveryWarePlatformManager_GetFullName
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.PlatformManager");
                if (t == null) return null;
                var platformType = t.GetNestedType("Platform", BindingFlags.Public | BindingFlags.NonPublic);
                return platformType == null
                    ? null
                    : t.GetMethod("GetFullName", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly, null, new[] { platformType }, null);
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref string __result)
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Suppressing PlatformManager.GetFullName.");
            __result = string.Empty;
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSManager_LoadEOSLibraries
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton");
                return t == null ? null : PatchHelpers.FindMethod(t, "LoadEOSLibraries");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            EosBlockLogger.LogOnce("EOSManager.EOSSingleton.LoadEOSLibraries");
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSManager_LoadDynamicLibrary
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton");
                return t == null ? null : PatchHelpers.FindMethod(t, "LoadDynamicLibrary");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref object __result)
        {
            EosBlockLogger.LogOnce("EOSManager.EOSSingleton.LoadDynamicLibrary");
            __result = null;
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSManager_LoadDelegatesWithEOSBindingAPI
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton");
                return t == null ? null : PatchHelpers.FindMethod(t, "LoadDelegatesWithEOSBindingAPI");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            EosBlockLogger.LogOnce("EOSManager.EOSSingleton.LoadDelegatesWithEOSBindingAPI");
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSManager_Init
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton");
                return t == null ? null : PatchHelpers.FindMethod(t, "Init");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            EosBlockLogger.LogOnce("EOSManager.EOSSingleton.Init");
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSManager_InitializePlatformInterface
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton");
                return t == null ? null : PatchHelpers.FindMethod(t, "InitializePlatformInterface");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            EosBlockLogger.LogOnce("EOSManager.EOSSingleton.InitializePlatformInterface");
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSManager_CreatePlatformInterface
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton");
                return t == null ? null : PatchHelpers.FindMethod(t, "CreatePlatformInterface");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref object __result)
        {
            EosBlockLogger.LogOnce("EOSManager.EOSSingleton.CreatePlatformInterface");
            __result = null;
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSManager_InitializeOverlay
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton");
                return t == null ? null : PatchHelpers.FindMethod(t, "InitializeOverlay");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            EosBlockLogger.LogOnce("EOSManager.EOSSingleton.InitializeOverlay");
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSManager_GetEOSPlatformInterface
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.EOSManager+EOSSingleton");
                return t == null ? null : PatchHelpers.FindMethod(t, "GetEOSPlatformInterface");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref object __result)
        {
            EosBlockLogger.LogOnce("EOSManager.EOSSingleton.GetEOSPlatformInterface");
            __result = null;
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_PlayEveryWarePlatformSpecifics_InitializeOverlay
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.PlatformSpecifics`1");
                return t == null ? null : PatchHelpers.FindMethod(t, "InitializeOverlay");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            EosBlockLogger.LogOnce("PlatformSpecifics.InitializeOverlay");
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_PlayEveryWarePlatformSpecifics_UpdateNetworkStatus
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.PlatformSpecifics`1");
                return t == null ? null : PatchHelpers.FindMethod(t, "UpdateNetworkStatus");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            EosBlockLogger.LogOnce("PlatformSpecifics.UpdateNetworkStatus");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_BootSceneManager_RemoveEOSAuthenticator
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.Boot.BootSceneManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "InitializeBootables");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static void Prefix(object __instance)
        {
            try
            {
                var field = __instance.GetType().GetField("bootableComponents", BindingFlags.Instance | BindingFlags.NonPublic);
                if (field == null) return;

                if (field.GetValue(__instance) is IList list)
                {
                    for (int i = list.Count - 1; i >= 0; --i)
                    {
                        var item = list[i];
                        if (item == null) continue;
                        var fullName = item.GetType().FullName;
                        if (fullName == "_Scripts.Boot.EOSAuthenticator" ||
                            fullName == "_Scripts.Binary.ExternalAuthenticationManager")
                        {
                            Plugin.Log.LogInfo($"[EOS] Removed {fullName} from BootSceneManager bootables.");
                            list.RemoveAt(i);
                        }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[EOS] BootSceneManager removal failed: {e.Message}");
            }
        }
    }

    [HarmonyPatch]
    internal static class Patch_EOSAuthenticator_StartBoot
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.Boot.EOSAuthenticator");
                return t == null ? null : PatchHelpers.FindMethod(t, "StartBoot");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(object __instance)
        {
            try
            {
                if (ModConfig.VerboseLogging.Value)
                    Plugin.Log.LogInfo("[EOS] Skipping EOSAuthenticator.StartBoot.");

                var field = __instance.GetType().GetField("_initialisationCompleted", BindingFlags.Instance | BindingFlags.NonPublic);
                field?.SetValue(__instance, true);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[EOS] EOSAuthenticator.StartBoot prefix failed: {e.Message}");
            }
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_EOSAuthenticator_IsBooted
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.Boot.EOSAuthenticator");
                return t == null ? null : PatchHelpers.FindMethod(t, "IsBooted");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref bool __result)
        {
            __result = true;
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_EOSAuthenticator_StartConnect
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.Boot.EOSAuthenticator");
                return t == null ? null : PatchHelpers.FindMethod(t, "StartConnect");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Skipping EOSAuthenticator.StartConnect.");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_ExternalAuthenticationManager_StartConnect
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.Binary.ExternalAuthenticationManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "StartConnect");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Skipping ExternalAuthenticationManager.StartConnect.");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_LobbyManager_CreateLobby_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.Managers.LobbyManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "CreateLobby");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(object __instance)
        {
            if (NetworkedInstanceManager.IsNetworkedModeConfigured)
            {
                Plugin.Log.LogInfo("[EOS] Replacing LobbyManager.CreateLobby with networked Tugboat local host flow.");
                NetworkedInstanceManager.TryStartConfiguredNetworkedGame();
                return false;
            }

            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Replacing LobbyManager.CreateLobby with offline custom server flow.");
                OfflineModeManager.HandleOfflineCreateLobby(__instance);
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_LobbyManager_JoinLobby_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.Managers.LobbyManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "JoinLobby");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Skipping LobbyManager.JoinLobby for offline custom server mode.");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_LobbyManager_SearchByAttributes_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.Managers.LobbyManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "SearchByAttributes");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Skipping LobbyManager.SearchByAttributes for offline custom server mode.");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_LobbyManager_UpdateLobbyHeartbeat_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.Managers.LobbyManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "UpdateLobbyHeartbeat");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Skipping LobbyManager.UpdateLobbyHeartbeat for offline custom server mode.");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_LobbyManager_GetAttributeByKey_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.Managers.LobbyManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "GetAttributeByKey");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref object __result)
        {
            if (OfflineModeManager.OfflineModeActive
                || OfflineModeManager.CustomServerRequested
                || NetworkedInstanceManager.IsNetworkedModeConfigured)
            {
                __result = null;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerControl_IsPeaceful_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayerControl");
                return t == null ? null : PatchHelpers.FindMethod(t, "get_IsPeaceful");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref bool __result)
        {
            if (OfflineModeManager.OfflineModeActive
                || OfflineModeManager.CustomServerRequested
                || NetworkedInstanceManager.IsNetworkedModeConfigured)
            {
                __result = false;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerControl_YoureAllowedToPickupSnow_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayerControl");
                return t == null ? null : PatchHelpers.FindMethod(t, "YoureAllowedTo_PickupSnow");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref bool __result)
        {
            if (OfflineModeManager.OfflineModeActive
                || OfflineModeManager.CustomServerRequested
                || NetworkedInstanceManager.IsNetworkedModeConfigured)
            {
                __result = true;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_PlayEveryWarePlatformManager_CurrentPlatform
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.PlatformManager");
                return t == null
                    ? null
                    : t.GetMethod("get_CurrentPlatform", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Suppressing PlatformManager.CurrentPlatform getter.");
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSManager_InstanceGetter
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.EOSManager");
                return t?.GetMethod("get_Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref object __result)
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Suppressing EOSManager.Instance getter.");
            __result = null;
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSManagerPlatformSpecificsSingleton_InstanceGetter
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.EOSManagerPlatformSpecificsSingleton");
                return t?.GetMethod("get_Instance", BindingFlags.Public | BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref object __result)
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Suppressing EOSManagerPlatformSpecificsSingleton.Instance getter.");
            __result = null;
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSManagerPlatformSpecificsSingleton_InitOnPlayMode
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.EOSManagerPlatformSpecificsSingleton");
                return t?.GetMethod("InitOnPlayMode", BindingFlags.NonPublic | BindingFlags.Static | BindingFlags.DeclaredOnly);
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo("[EOS] Suppressing EOSManagerPlatformSpecificsSingleton.InitOnPlayMode.");
            return false;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSLobbyManager_CreateLobby_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.Samples.EOSLobbyManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "CreateLobby");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Skipping EOSLobbyManager.CreateLobby for offline custom server mode.");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSLobbyManager_JoinLobby_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.Samples.EOSLobbyManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "JoinLobby");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Skipping EOSLobbyManager.JoinLobby for offline custom server mode.");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSLobbyManager_SearchByLobbyId_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.Samples.EOSLobbyManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "SearchByLobbyId");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Skipping EOSLobbyManager.SearchByLobbyId for offline custom server mode.");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSLobbyManager_SearchByAttribute_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.Samples.EOSLobbyManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "SearchByAttribute");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Skipping EOSLobbyManager.SearchByAttribute for offline custom server mode.");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_FishyEOS_StartConnection_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("FishNet.Transporting.FishyEOSPlugin.FishyEOS");
                return t == null ? null : PatchHelpers.FindMethod(t, "StartConnection");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(bool server, ref bool __result)
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Skipping FishyEOS.StartConnection for offline custom server mode.");
                __result = true;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_FishyEOS_ServerPeer_StartConnection_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("FishNet.Transporting.FishyEOSPlugin.ServerPeer");
                return t == null ? null : PatchHelpers.FindMethod(t, "StartConnection");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref bool __result)
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Skipping ServerPeer.StartConnection for offline custom server mode.");
                __result = true;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_FishyEOS_ClientPeer_StartConnection_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("FishNet.Transporting.FishyEOSPlugin.ClientPeer");
                return t == null ? null : PatchHelpers.FindMethod(t, "StartConnection");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Skipping ClientPeer.StartConnection for offline custom server mode.");
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_FishyEOS_ClientHostPeer_StartConnection_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("FishNet.Transporting.FishyEOSPlugin.ClientHostPeer");
                return t == null ? null : PatchHelpers.FindMethod(t, "StartConnection");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(object serverPeer, ref bool __result)
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Skipping ClientHostPeer.StartConnection for offline custom server mode.");
                __result = true;
                return false;
            }
            return true;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_FishyEOS_IterateIncoming_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("FishNet.Transporting.FishyEOSPlugin.FishyEOS");
                return t == null ? null : PatchHelpers.FindMethod(t, "IterateIncoming");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
                return false;
            return true;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_FishyEOS_IterateOutgoing_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("FishNet.Transporting.FishyEOSPlugin.FishyEOS");
                return t == null ? null : PatchHelpers.FindMethod(t, "IterateOutgoing");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
                return false;
            return true;
        }
    }

    [HarmonyPatch]
    [SledCoopEosPatch]
    internal static class Patch_EOSTransportManager_Initialize_OfflineOnly
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("PlayEveryWare.EpicOnlineServices.Samples.Network.EOSTransportManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "Initialize");
            }
            catch
            {
                return null;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(ref bool __result)
        {
            if (OfflineModeManager.OfflineModeActive || OfflineModeManager.CustomServerRequested)
            {
                Plugin.Log.LogInfo("[EOS] Skipping EOSTransportManager.Initialize for offline custom server mode.");
                __result = false;
                return false;
            }
            return true;
        }
    }
}
