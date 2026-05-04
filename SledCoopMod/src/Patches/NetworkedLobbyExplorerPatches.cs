using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SledCoopMod.Patches
{
    // ─────────────────────────────────────────────────────────────────────────
    // Source-level kill for the residual "TEXT CHAT ONLY LOBBIES" warning
    // that re-paints during the EOS-skip boot.
    //
    // The warning lives on two owners:
    //
    //   _Scripts.UI.Pre_Game.UILobbyExplorer
    //     fields: textChatOnlyLobbyWarning, loadingThrobber, noResultsGameObject
    //     methods: Update, Refresh, GetListOfLobbies, ShowPanel
    //
    //   SteamLobbyListManager
    //     fields: textChatOnlyLobbyWarning, searchingForOnlyTextChatLobbies,
    //             fullscreenLoadingIndicator, joiningGameIndicator, loadingText
    //     methods: GetListOfLobbies, DisplayLobbies, DisplayNoLobbies,
    //              OnSearchFilterChange, OnSearchSubmit
    //
    // (No Update/OnEnable on SteamLobbyListManager — the game runs its filter
    // sync via DisplayLobbies/DisplayNoLobbies inside the EOS callback.)
    //
    // In networked mode the lobby browser is a dead system: the host is
    // routed straight to NetworkedInstanceManager.TryStartConfiguredNetworkedGame
    // by Patch_LobbyManager_HostLobby, and clients never reach the menu.
    // Skipping every "fetch lobbies / paint results" entry point removes the
    // re-activation source for the warning GameObject.  Each prefix also
    // hides the warning + throbber once for cosmetics so the panel reads as
    // "lobby browser disabled" if a user does navigate to it.
    // ─────────────────────────────────────────────────────────────────────────

    internal static class NetworkedLobbyExplorerHelpers
    {
        private static readonly HashSet<string> _suppressLogged =
            new HashSet<string>(StringComparer.Ordinal);

        internal static void NoteSuppression(string ownerType)
        {
            if (!_suppressLogged.Add(ownerType))
                return;

            Plugin.Log.LogInfo($"[NetworkedLobbyExplorer] Suppressing {ownerType} lobby-list polling in networked mode.");
        }

        internal static void HideOnce(object owner, string fieldName)
        {
            try
            {
                var field = owner.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                    return;

                object? value = field.GetValue(owner);
                if (value is GameObject go)
                {
                    if (go.activeSelf)
                        go.SetActive(false);
                    return;
                }

                // Fallback: some fields are MonoBehaviours; deactivate the GO they're on.
                GameObject? hosted = ReflectionHelper.GetGameObject(value);
                if (hosted != null && hosted.activeSelf)
                    hosted.SetActive(false);
            }
            catch { }
        }

        // Force the "searchingForOnlyTextChatLobbies" bool flag false so any
        // setter chain that races our prefix can't latch it back on.
        internal static void ClearBoolFlag(object owner, string fieldName)
        {
            try
            {
                var field = owner.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null && field.FieldType == typeof(bool))
                    field.SetValue(owner, false);
            }
            catch { }
        }
    }

    [HarmonyPatch]
    internal static class Patch_UILobbyExplorer_Suppress
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var t = PatchHelpers.SafeTypeByName("_Scripts.UI.Pre_Game.UILobbyExplorer")
                 ?? PatchHelpers.SafeTypeByName("UILobbyExplorer");
            if (t == null)
                yield break;

            // Update keeps re-activating textChatOnlyLobbyWarning,
            // GetListOfLobbies kicks off another EOS search,
            // Refresh re-runs UI state mutations after a stall,
            // ShowPanel is the entry hook when the user opens the panel.
            foreach (string name in new[] { "Update", "Refresh", "GetListOfLobbies", "ShowPanel" })
            {
                var m = PatchHelpers.FindMethod(t, name);
                if (m != null)
                    yield return m;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(object __instance)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            NetworkedLobbyExplorerHelpers.HideOnce(__instance, "textChatOnlyLobbyWarning");
            NetworkedLobbyExplorerHelpers.HideOnce(__instance, "loadingThrobber");
            NetworkedLobbyExplorerHelpers.HideOnce(__instance, "noResultsGameObject");
            NetworkedLobbyExplorerHelpers.ClearBoolFlag(__instance, "_isSearching");
            NetworkedLobbyExplorerHelpers.NoteSuppression("UILobbyExplorer");
            return false;
        }

        // If the game's UILobbyExplorer is wired into something we missed and
        // the suppressed call site fails on the way back up, swallow the
        // exception so it doesn't bubble into the menu state machine.
        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured) return __exception;

            Plugin.Log.LogDebug($"[NetworkedLobbyExplorer] Suppressed {__exception.GetType().Name} from {__originalMethod.Name}.");
            return null;
        }
    }

    [HarmonyPatch]
    internal static class Patch_SteamLobbyListManager_Suppress
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var t = PatchHelpers.SafeTypeByName("SteamLobbyListManager");
            if (t == null)
                yield break;

            // SteamLobbyListManager has no Update — the warning GameObjects
            // get re-enabled inside its EOS callback chain.  Skip the
            // search-trigger and result-paint methods.
            foreach (string name in new[]
            {
                "GetListOfLobbies",
                "DisplayLobbies",
                "DisplayNoLobbies",
                "OnSearchFilterChange",
                "OnSearchSubmit",
                "Button_OpenLobbyList_Normal",
                "Button_OpenLobbyList_TextChatOnly",
                "CompleteJoiningLobby",
            })
            {
                var m = PatchHelpers.FindMethod(t, name);
                if (m != null)
                    yield return m;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(object __instance)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            NetworkedLobbyExplorerHelpers.HideOnce(__instance, "textChatOnlyLobbyWarning");
            NetworkedLobbyExplorerHelpers.HideOnce(__instance, "fullscreenLoadingIndicator");
            NetworkedLobbyExplorerHelpers.HideOnce(__instance, "joiningGameIndicator");
            NetworkedLobbyExplorerHelpers.HideOnce(__instance, "loadingText");
            NetworkedLobbyExplorerHelpers.ClearBoolFlag(__instance, "searchingForOnlyTextChatLobbies");
            NetworkedLobbyExplorerHelpers.NoteSuppression("SteamLobbyListManager");
            return false;
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured) return __exception;

            Plugin.Log.LogDebug($"[NetworkedLobbyExplorer] Suppressed {__exception.GetType().Name} from SteamLobbyListManager.{__originalMethod.Name}.");
            return null;
        }
    }
}
