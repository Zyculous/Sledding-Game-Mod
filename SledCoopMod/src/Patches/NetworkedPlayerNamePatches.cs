using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SledCoopMod.Patches
{
    // ─────────────────────────────────────────────────────────────────────────
    // Source-level kill for the "Playerwithareallylongname" placeholder text
    // that survives our Refresh in networked mode.
    //
    // PlayersListNameItem (per monodis) ships a public UpdateNameItem method
    // and a Start lifecycle method that read from _lobbyMember /
    // _playerReference / _isLocalUser etc. and re-stamp nameText from the
    // resolved EOS lobby member name. In loopback mode those EOS refs are all
    // null, so the native code either no-ops (leaving the prefab placeholder
    // visible) or writes an empty string. Either way we lose the name we set
    // in NetworkedPlayerListUi.PopulateRow.
    //
    // Strategy: prefix-skip both Start and UpdateNameItem in networked mode.
    // After our Refresh writes the text, nothing native is allowed to
    // overwrite it.
    // ─────────────────────────────────────────────────────────────────────────

    [HarmonyPatch]
    internal static class Patch_PlayersListNameItem_PreserveModText
    {
        private static readonly HashSet<string> _logged =
            new HashSet<string>(StringComparer.Ordinal);

        static IEnumerable<MethodBase> TargetMethods()
        {
            var t = PatchHelpers.SafeTypeByName("PlayersListNameItem");
            if (t == null)
                yield break;

            foreach (string name in new[] { "Start", "UpdateNameItem", "UpdatePingText" })
            {
                var m = PatchHelpers.FindMethod(t, name);
                if (m != null)
                    yield return m;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            string key = __originalMethod.Name;
            if (_logged.Add(key))
                Plugin.Log.LogInfo($"[NetworkedPlayerName] Suppressing PlayersListNameItem.{key} so mod-supplied row text survives.");

            return false;
        }

        // The native methods read _lobbyMember.ProductUserId and friends,
        // which are null in loopback mode, so they NRE out of the gate.
        // Swallow that exception so any caller in the menu state machine
        // doesn't bubble it into UiReferenceController.
        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured) return __exception;

            Plugin.Log.LogDebug(
                $"[NetworkedPlayerName] Swallowed {__exception.GetType().Name} from PlayersListNameItem.{__originalMethod.Name}.");
            return null;
        }
    }
}
