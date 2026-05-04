using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SledCoopMod.Patches
{
    // ─────────────────────────────────────────────────────────────────────────
    // Source-level kill for the residual "LOADING" text that paints over the
    // gameplay viewport after the EOS-skip boot.
    //
    // NetworkedUiState.ClearStuckLoadingIfNeeded already hides the loading
    // panel and nukes orphaned LOADING TMP_Text components, but two animator
    // hosts keep repainting the string every frame:
    //
    //   1. UILoadingTextAnimation.Update — re-stamps a localised "LOADING…"
    //      key into its `loadingText` TMP_Text.  Some of these animators live
    //      on detached boot canvases that survive the panel deactivation,
    //      so the cleanup pass can't reach them.
    //
    //   2. _Scripts.UI.Misc.UILoading.SetLoadingText(LoadingState) — the
    //      callback the EOS-skipped boot path would otherwise invoke to set
    //      the visible loading string.  Skipping this stops the writes at
    //      the source so the cleanup pass never has stale text to clear.
    //
    // Both patches are guarded by NetworkedInstanceManager.IsNetworkedModeConfigured
    // so vanilla mode is unaffected.
    // ─────────────────────────────────────────────────────────────────────────

    internal static class NetworkedLoadingTextHelpers
    {
        private static readonly HashSet<int> _clearedAnimators = new HashSet<int>();
        private static bool _suppressLogged;

        internal static void NoteSuppression()
        {
            if (_suppressLogged)
                return;

            _suppressLogged = true;
            Plugin.Log.LogInfo("[NetworkedLoadingText] Suppressing UILoadingTextAnimation/UILoading writes in networked mode.");
        }

        // Clear the animator's TMP_Text exactly once per animator instance.
        // The first time we suppress its Update, also blank the previously-
        // painted string so the current frame doesn't show a stale "LOADING".
        // Tries both the public `loadingText` field and the private
        // `_loadingText` fallback in case a future game patch renames it.
        internal static void ClearAnimatorLoadingTextOnce(object animator)
        {
            try
            {
                int id = (animator as UnityEngine.Object)?.GetInstanceID() ?? animator.GetHashCode();
                if (!_clearedAnimators.Add(id))
                    return;

                object? tmp = GetField(animator, "loadingText")
                    ?? GetField(animator, "_loadingText");
                if (tmp == null)
                    return;

                // TMP_Text.text is a public property in TextMesh Pro; use
                // BindingFlags.Public|NonPublic to be safe across builds.
                var prop = tmp.GetType().GetProperty(
                    "text",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null && prop.CanWrite)
                    prop.SetValue(tmp, "");
            }
            catch { }
        }

        private static object? GetField(object instance, string name)
        {
            try
            {
                var f = instance.GetType().GetField(
                    name,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f?.GetValue(instance);
            }
            catch { return null; }
        }
    }

    [HarmonyPatch]
    internal static class Patch_UILoadingTextAnimation_Update_Suppress
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("UILoadingTextAnimation");
            return t == null ? null : PatchHelpers.FindMethod(t, "Update");
        }

        [HarmonyPrefix]
        static bool Prefix(object __instance)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            // Only suppress once we're past the boot frames where the game's
            // own loading animation is visually expected.  The mod's
            // ShouldHideNetworkedOverlay flag flips true the moment a host or
            // child connection is in-flight and stays true through gameplay.
            if (!ShouldSuppress())
                return true;

            NetworkedLoadingTextHelpers.ClearAnimatorLoadingTextOnce(__instance);
            NetworkedLoadingTextHelpers.NoteSuppression();
            return false;
        }

        private static bool ShouldSuppress()
        {
            return SceneWatcher.IsInGameplayScene
                || NetworkedInstanceManager.ShouldHideNetworkedOverlay
                || NetworkedInstanceManager.Instance?.IsChildClient == true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_UILoading_SetLoadingText_Suppress
    {
        static MethodBase? TargetMethod()
        {
            // Namespaced as _Scripts.UI.Misc.UILoading; PatchHelpers.SafeTypeByName
            // indexes both Name and FullName, so either form resolves.
            var t = PatchHelpers.SafeTypeByName("_Scripts.UI.Misc.UILoading")
                 ?? PatchHelpers.SafeTypeByName("UILoading");
            return t == null ? null : PatchHelpers.FindMethod(t, "SetLoadingText");
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            NetworkedLoadingTextHelpers.NoteSuppression();
            return false;
        }
    }
}
