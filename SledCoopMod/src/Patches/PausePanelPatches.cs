using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SledCoopMod.Patches
{
    // ─────────────────────────────────────────────────────────────────────────
    // _Scripts.UI.In_Game.UIPausePanel
    //
    // In networked-instance mode, several systems the pause panel queries are
    // intentionally suppressed (Dissonance voice, EOS lobby code/password,
    // platform invite). Their fields/singletons are null when ShowPanel() runs
    // its mic-toggle / lobby-code / invite branches — the panel throws NRE
    // partway through ShowPanel, the resume button never gets re-enabled, and
    // pressing Escape stops working entirely.
    //
    // Strategy: Finalize the throwing methods so the exception is swallowed
    // and the panel still toggles its activeSelf state. Resume / Hide work
    // because UIPanel.HidePanel (the base method) is simple and not affected.
    // ─────────────────────────────────────────────────────────────────────────

    internal static class PausePanelPatchHelpers
    {
        private static readonly HashSet<string> _logged =
            new HashSet<string>(StringComparer.Ordinal);

        internal static bool ShouldSuppress() =>
            NetworkedInstanceManager.IsNetworkedModeConfigured;

        internal static void LogOnce(string key, string message)
        {
            if (_logged.Add(key))
                Plugin.Log.LogInfo(message);
        }

        internal static MethodBase? FindOn(string typeName, string methodName)
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName(typeName);
                return t == null ? null : PatchHelpers.FindMethod(t, methodName);
            }
            catch { return null; }
        }

        internal static bool HandleNetworkedQuit(string site)
        {
            if (!ShouldSuppress())
                return true;

            LogOnce(site, $"[PausePanelPatches] Routing {site} through networked quit handler.");
            NetworkedInstanceManager.TryHandleConfiguredQuitToDesktop();
            return false;
        }

        internal static bool HandleNetworkedLeave(string site)
        {
            if (!ShouldSuppress())
                return true;

            if (NetworkedInstanceManager.IsQuitInProgress)
            {
                LogOnce($"{site}-quit-in-progress", $"[PausePanelPatches] Ignoring {site} while networked quit is already in progress.");
                return false;
            }

            LogOnce(site, $"[PausePanelPatches] Routing {site} through networked leave handler.");
            NetworkedInstanceManager.TryHandleConfiguredLeaveLobby();
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_UIPausePanel_ShowPanel_NreGuard
    {
        static MethodBase? TargetMethod() =>
            PausePanelPatchHelpers.FindOn("_Scripts.UI.In_Game.UIPausePanel", "ShowPanel");

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, object __instance)
        {
            if (__exception == null) return null;
            if (!PausePanelPatchHelpers.ShouldSuppress()) return __exception;

            // Force the panel GameObject active so the resume/quit buttons are
            // reachable even though ShowPanel partially failed.
            try
            {
                var go = ReflectionHelper.GetGameObject(__instance);
                if (go != null && !go.activeSelf)
                    go.SetActive(true);
            }
            catch { }

            PausePanelPatchHelpers.LogOnce(
                "UIPausePanel.ShowPanel",
                $"[PausePanelPatches] Suppressed {__exception.GetType().Name} in UIPausePanel.ShowPanel: {__exception.Message}");
            return null;
        }
    }

    [HarmonyPatch]
    internal static class Patch_UIPausePanel_HidePanel_NreGuard
    {
        static MethodBase? TargetMethod() =>
            PausePanelPatchHelpers.FindOn("_Scripts.UI.In_Game.UIPausePanel", "HidePanel");

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, object __instance)
        {
            if (__exception == null) return null;
            if (!PausePanelPatchHelpers.ShouldSuppress()) return __exception;

            // Make sure the pause overlay actually goes away even if HidePanel
            // threw before tweening out.
            try
            {
                var go = ReflectionHelper.GetGameObject(__instance);
                if (go != null && go.activeSelf)
                    go.SetActive(false);
            }
            catch { }

            PausePanelPatchHelpers.LogOnce(
                "UIPausePanel.HidePanel",
                $"[PausePanelPatches] Suppressed {__exception.GetType().Name} in UIPausePanel.HidePanel: {__exception.Message}");
            return null;
        }
    }

    [HarmonyPatch]
    internal static class Patch_UIPausePanel_Update_NreGuard
    {
        static MethodBase? TargetMethod() =>
            PausePanelPatchHelpers.FindOn("_Scripts.UI.In_Game.UIPausePanel", "Update");

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!PausePanelPatchHelpers.ShouldSuppress()) return __exception;

            PausePanelPatchHelpers.LogOnce(
                "UIPausePanel.Update",
                $"[PausePanelPatches] Suppressed {__exception.GetType().Name} in UIPausePanel.Update.");
            return null;
        }
    }

    // Resume button: ensure the pause overlay GO becomes inactive even if the
    // native handler throws or its CloseMenu reference path NREs.
    [HarmonyPatch]
    internal static class Patch_UIPausePanel_ButtonResume_Force
    {
        static MethodBase? TargetMethod() =>
            PausePanelPatchHelpers.FindOn("_Scripts.UI.In_Game.UIPausePanel", "Button_Resume");

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            if (!PausePanelPatchHelpers.ShouldSuppress()) return;
            try
            {
                var go = ReflectionHelper.GetGameObject(__instance);
                if (go != null && go.activeSelf)
                    go.SetActive(false);
            }
            catch { }

            // Also restore the gameplay cursor state and tell the game we are
            // no longer in the menu.
            try
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            catch { }
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, object __instance)
        {
            if (__exception == null) return null;
            if (!PausePanelPatchHelpers.ShouldSuppress()) return __exception;

            try
            {
                var go = ReflectionHelper.GetGameObject(__instance);
                if (go != null && go.activeSelf)
                    go.SetActive(false);
            }
            catch { }

            PausePanelPatchHelpers.LogOnce(
                "UIPausePanel.Button_Resume",
                $"[PausePanelPatches] Suppressed {__exception.GetType().Name} in UIPausePanel.Button_Resume.");
            return null;
        }
    }

    // Mic-toggle indicator + lobby-code/password helpers: the most likely
    // sources of the NRE inside ShowPanel. Skip them entirely in networked
    // local-coop because Dissonance and the EOS lobby code system are
    // intentionally inert.
    [HarmonyPatch]
    internal static class Patch_UIPausePanel_CheckMicToggleIndicators_NoOp
    {
        static MethodBase? TargetMethod() =>
            PausePanelPatchHelpers.FindOn("_Scripts.UI.In_Game.UIPausePanel", "CheckMicToggleIndicators");

        [HarmonyPrefix]
        static bool Prefix() => !PausePanelPatchHelpers.ShouldSuppress();
    }

    [HarmonyPatch]
    internal static class Patch_UIPausePanel_CheckShowButtonVariables_NoOp
    {
        static MethodBase? TargetMethod() =>
            PausePanelPatchHelpers.FindOn("_Scripts.UI.In_Game.UIPausePanel", "CheckShowButtonVariables");

        [HarmonyPrefix]
        static bool Prefix() => !PausePanelPatchHelpers.ShouldSuppress();
    }

    [HarmonyPatch]
    internal static class Patch_UIPausePanel_EnableLobbyCodeButton_NoOp
    {
        static MethodBase? TargetMethod() =>
            PausePanelPatchHelpers.FindOn("_Scripts.UI.In_Game.UIPausePanel", "EnableLobbyCodeButton");

        [HarmonyPrefix]
        static bool Prefix() => !PausePanelPatchHelpers.ShouldSuppress();
    }

    [HarmonyPatch]
    internal static class Patch_UIPausePanel_EnablePasswordButton_NoOp
    {
        static MethodBase? TargetMethod() =>
            PausePanelPatchHelpers.FindOn("_Scripts.UI.In_Game.UIPausePanel", "EnablePasswordButton");

        [HarmonyPrefix]
        static bool Prefix() => !PausePanelPatchHelpers.ShouldSuppress();
    }

    [HarmonyPatch]
    internal static class Patch_UiReferenceController_QuitGameConfirmed_Networked
    {
        static MethodBase? TargetMethod() =>
            PausePanelPatchHelpers.FindOn("UiReferenceController", "QuitGameConfirmed");

        [HarmonyPrefix]
        static bool Prefix() =>
            PausePanelPatchHelpers.HandleNetworkedQuit("UiReferenceController.QuitGameConfirmed");
    }

    [HarmonyPatch]
    internal static class Patch_BinaryQuit_Networked
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var t = PatchHelpers.SafeTypeByName("_Scripts.Binary.Quit");
            if (t == null) yield break;

            foreach (string name in new[] { "SafeQuit", "SaveInstantQuit" })
            {
                var method = PatchHelpers.FindMethod(t, name);
                if (method != null)
                    yield return method;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod) =>
            PausePanelPatchHelpers.HandleNetworkedQuit(
                $"Quit.{__originalMethod.Name}");
    }

    [HarmonyPatch]
    internal static class Patch_UIPausePanel_ButtonLeaveLobby_Networked
    {
        static MethodBase? TargetMethod() =>
            PausePanelPatchHelpers.FindOn("_Scripts.UI.In_Game.UIPausePanel", "Button_LeaveLobby");

        [HarmonyPrefix]
        static bool Prefix() =>
            PausePanelPatchHelpers.HandleNetworkedLeave("UIPausePanel.Button_LeaveLobby");
    }

    [HarmonyPatch]
    internal static class Patch_UiReferenceController_LeaveGame_Networked
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var t = PatchHelpers.SafeTypeByName("UiReferenceController");
            if (t == null) yield break;

            foreach (string name in new[]
            {
                "Button_LeaveGame",
                "LeaveGame",
                "KickLeaveGame",
                "OnLeaveGameDoAllUiStuff",
                "OnLobbyLeft",
            })
            {
                var method = PatchHelpers.FindMethod(t, name);
                if (method != null)
                    yield return method;
            }

            var sweeper = PatchHelpers.SafeTypeByName("_Scripts.Binary.EOSSweeper");
            if (sweeper != null)
            {
                var method = PatchHelpers.FindMethod(sweeper, "Button_LeaveLobby");
                if (method != null)
                    yield return method;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod) =>
            PausePanelPatchHelpers.HandleNetworkedLeave(
                $"{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}");
    }

    [HarmonyPatch]
    internal static class Patch_UiReferenceController_LeaveQuit_Finalizers
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            foreach (string typeName in new[]
            {
                "UiReferenceController",
                "_Scripts.UI.In_Game.UIPausePanel",
                "_Scripts.Binary.EOSSweeper",
                "_Scripts.Binary.Quit",
            })
            {
                var t = PatchHelpers.SafeTypeByName(typeName);
                if (t == null) continue;

                foreach (string name in new[]
                {
                    "Button_LeaveLobby",
                    "Button_QuitGame",
                    "QuitGameConfirmed",
                    "Button_LeaveGame",
                    "LeaveGame",
                    "KickLeaveGame",
                    "OnLeaveGameDoAllUiStuff",
                    "OnLobbyLeft",
                    "ReturnToMainMenu",
                    "SafeQuit",
                    "SaveInstantQuit",
                })
                {
                    var method = PatchHelpers.FindMethod(t, name);
                    if (method != null)
                        yield return method;
                }
            }
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            if (!PausePanelPatchHelpers.ShouldSuppress()) return __exception;

            PausePanelPatchHelpers.LogOnce(
                $"leave-quit-finalizer-{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}",
                $"[PausePanelPatches] Suppressed {__exception.GetType().Name} in {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}.");
            return null;
        }
    }
}
