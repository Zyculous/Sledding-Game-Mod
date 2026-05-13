using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SledCoopMod.Patches
{
    // ─────────────────────────────────────────────────────────────────────────────
    // UI / HUD detection patches
    //
    // Goal: detect when the game's HUD canvas is created so HudManager can register
    // it as the source for slot-0 and clone it for extra players.
    //
    // Two approaches run concurrently:
    //   1.  Canvas.Awake (UnityEngine built-in) – catches every canvas, filtered
    //       for likely-HUD candidates by name heuristics.
    //   2.  _Scripts.UI.Components.UIPanel.Start – game-specific panel init,
    //       confirmed in RuntimeInitializeOnLoads.json as an entry point.
    // ─────────────────────────────────────────────────────────────────────────────

    // Canvas has no patchable lifecycle method in IL2CPP interop.
    // HUD detection is handled by Patch_UIPanel_Awake below.


    // ── UIPanel.Awake ─────────────────────────────────────────────────────────────
    // _Scripts.UI.Components.UIPanel fires Awake when a panel initialises.
    // This is a more targeted hook than Canvas.Awake.
    // (Note: IL2CPP strips Start; Awake is the correct lifecycle hook here.)

    [HarmonyPatch]
    internal static class Patch_UIPanel_Awake
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.UI.Components.UIPanel");
                return t == null ? null : PatchHelpers.FindMethod(t, "Awake");
            }
            catch { return null; }
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            if (NetworkedInstanceManager.IsNetworkedModeConfigured)
                return;

            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo(
                    $"[DIAG] UIPanel.Awake – type={__instance?.GetType().FullName}");

            // Look for a Canvas on this panel's GameObject and forward it.
            var go = ReflectionHelper.GetGameObject(__instance);
            if (go == null) return;
            var canvas = go.GetComponent<Canvas>();
            if (canvas != null && !canvas.name.StartsWith("SledCoopHUD_"))
                SpawnHooks.ConsiderHudCanvas(canvas);
        }
    }

    // ── UiReferenceController.HandleInput_RegardlessOfNetwork ─────────────────────
    // Phase 4 / Phase 5: Suppress the game's input-handling loop while the mod
    // settings overlay is open.  This prevents the game from opening its own
    // inventory/pause menus in response to key presses that our UI is consuming.

    [HarmonyPatch]
    internal static class Patch_UiReferenceController_HandleInput
    {
        private static bool _networkedExceptionLogged;
        private static int _networkedExceptionCount;

        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("UiReferenceController");
                return t == null ? null
                    : PatchHelpers.FindMethod(t, "HandleInput_RegardlessOfNetwork");
            }
            catch { return null; }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            // The mod-settings overlay (Ctrl+P modal) consumes keystrokes the
            // native UiReferenceController would otherwise route to inventory /
            // pause / etc. Skip the native handler entirely while it's open.
            if (ModSettingsUi.IsAnyOpen)
                return false;

            if (LocalCoopUI.Instance?.IsSettingsOpen == true)
                return false;

            if (OfflineModeManager.CustomServerRequested && SceneWatcher.IsInGameplayScene && HasExtraLocalSlot())
                return false;

            return true;
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception)
        {
            if (__exception == null) return null;

            if (NetworkedInstanceManager.IsNetworkedModeConfigured && SceneWatcher.IsInGameplayScene)
            {
                if (!NetworkedPlayerIdentity.IsPlayerIdNullArgument(__exception))
                    return __exception;

                _networkedExceptionCount++;
                if (!_networkedExceptionLogged || _networkedExceptionCount <= 3)
                {
                    _networkedExceptionLogged = true;
                    Plugin.Log.LogWarning("[UIPatches] Suppressed null platform playerId in UiReferenceController.HandleInput_RegardlessOfNetwork.");
                }
                NetworkedUiState.ApplyMenuCursorState();
                return null;
            }

            if (OfflineModeManager.CustomServerRequested && SceneWatcher.IsInGameplayScene)
            {
                Plugin.Log.LogWarning($"[UIPatches] Suppressed UiReferenceController.HandleInput_RegardlessOfNetwork exception in offline custom mode: {__exception.GetType().Name}");
                return null;
            }

            return __exception;
        }

        private static bool HasExtraLocalSlot()
        {
            try
            {
                if (LocalPlayerManager.Instance == null) return false;
                foreach (var slot in LocalPlayerManager.Instance.ActiveSlots)
                {
                    if (slot.SlotIndex > 0 && slot.IsJoined)
                        return true;
                }
            }
            catch { }

            return false;
        }
    }

    // ── Canvas.Awake — scan every new canvas for HUD / game panels ───────────────
    // IL2CPP Canvas.Awake is accessible on some builds; when it is, use it to catch
    // any canvas that wasn't detected by the UIHUD / UIPanel hooks.

    [HarmonyPatch]
    internal static class Patch_Canvas_Awake
    {
        static MethodBase? TargetMethod()
        {
            try { return PatchHelpers.FindMethod(typeof(Canvas), "Awake"); }
            catch { return null; }
        }

        [HarmonyPostfix]
        static void Postfix(Canvas __instance)
        {
            try
            {
                if (NetworkedInstanceManager.IsNetworkedModeConfigured)
                    return;

                if (__instance == null) return;
                if (__instance.name.StartsWith("SledCoop")) return;
                SpawnHooks.ConsiderHudCanvas(__instance);
            }
            catch { }
        }
    }

    [HarmonyPatch]
    internal static class Patch_UiReferenceController_StartupCleanupHooks
    {
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var t = PatchHelpers.SafeTypeByName("UiReferenceController");
            if (t == null)
                yield break;

            foreach (string name in new[]
            {
                "DisableLoading",
                "CloseAllOpenMenus",
                "OpenMenu",
            })
            {
                var method = PatchHelpers.FindMethod(t, name);
                if (method != null)
                    yield return method;
            }
        }

        [HarmonyPostfix]
        static void Postfix(MethodBase __originalMethod)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return;

            NetworkedUiState.ForceStartupUiCleanup($"UiReferenceController.{__originalMethod.Name}");
        }
    }
}
