using System;
using System.Reflection;
using HarmonyLib;

namespace SledCoopMod.Patches
{
    // ─────────────────────────────────────────────────────────────────────────────
    // Host-side patches: skip "falsely joined" sweep + auto-mark lobbies as MODDED
    // ─────────────────────────────────────────────────────────────────────────────

    // ── HostControls.Server_CheckForFalselyJoinedPlayers ─────────────────────────
    //
    // Fires on the server every time a PlayerReference is added.  The check sweeps
    // active players against expected join state and treats anything unexpected as
    // a bogus join (the host then tries to correct/kick).  With local-coop clones
    // sharing the host's productUserId the check trips and the corrective action it
    // initiates (auto-spawning a default sled for the suspect entity, among other
    // things) is the last call before the freeze in every recent log.
    //
    // The mod's clones are intentional, not hostile — skip the sweep entirely while
    // the mod-owned custom server is running.
    [HarmonyPatch]
    internal static class Patch_HostControls_Server_CheckForFalselyJoinedPlayers
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("HostControls");
                return t == null ? null : PatchHelpers.FindMethod(t, "Server_CheckForFalselyJoinedPlayers");
            }
            catch { return null; }
        }

        private static bool _logged;

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (!OfflineModeManager.CustomServerActive) return true;
            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo("[HostPatches] Server_CheckForFalselyJoinedPlayers skipped (custom server active).");
            }
            return false;
        }
    }

    // ── UICreateLobby.CreateNewLobbyButtonOnClick → networked local host ──────────
    //
    // In networked-instance mode the vanilla Host flow still tries to create an
    // EOS/Steam lobby after the lobby settings panel.  Same-machine local coop uses
    // Tugboat loopback instead, so route the confirm button into the mod host path.
    [HarmonyPatch]
    internal static class Patch_UICreateLobby_CreateNewLobbyButtonOnClick_Networked
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.UI.Pre_Game.UICreateLobby");
                return t == null ? null : PatchHelpers.FindMethod(t, "CreateNewLobbyButtonOnClick");
            }
            catch { return null; }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            if (NetworkedInstanceManager.Instance?.IsChildClient == true)
                return false;

            Plugin.Log.LogInfo("[HostPatches] Routing vanilla host confirm button to networked Tugboat local host.");
            NetworkedInstanceManager.TryStartConfiguredNetworkedGame();
            return false;
        }
    }

    // ── UIMainMenu.OnHostClicked → networked local host ───────────────────────────
    //
    // The first vanilla HOST button starts LobbyManager.InitCargoDelayed before the
    // create-lobby panel is confirmed. In networked-instance mode that EOS path is
    // unnecessary and currently fails when EOS has been disabled, so start Tugboat
    // directly from the first host button too.
    [HarmonyPatch]
    internal static class Patch_UIMainMenu_OnHostClicked_Networked
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.UI.Pre_Game.UIMainMenu");
                return t == null ? null : PatchHelpers.FindMethod(t, "OnHostClicked");
            }
            catch { return null; }
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            if (NetworkedInstanceManager.Instance?.IsChildClient == true)
                return false;

            Plugin.Log.LogInfo("[HostPatches] Routing vanilla HOST button to networked Tugboat local host.");
            NetworkedInstanceManager.TryStartConfiguredNetworkedGame();
            return false;
        }
    }

    // ── LobbyManager.OnCreateLobbyComplete → auto-mark MODDED ────────────────────
    //
    // When the host creates a lobby with the mod loaded, mark it as modded so other
    // clients can see this is a non-vanilla session.  SetLobbyToModded is a public
    // method on LobbyManager that toggles the MODDED lobby attribute; OnCreateLobbyComplete
    // fires once the EOS lobby has been created, so the lobby is guaranteed to exist
    // by the time we call it.
    [HarmonyPatch]
    internal static class Patch_LobbyManager_OnCreateLobbyComplete
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("_Scripts.Managers.LobbyManager");
                return t == null ? null : PatchHelpers.FindMethod(t, "OnCreateLobbyComplete");
            }
            catch { return null; }
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            try
            {
                var setMethod = __instance.GetType().GetMethod(
                    "SetLobbyToModded",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (setMethod == null)
                {
                    Plugin.Log.LogWarning("[HostPatches] SetLobbyToModded not found on LobbyManager.");
                    return;
                }
                setMethod.Invoke(__instance, null);
                Plugin.Log.LogInfo("[HostPatches] Lobby marked as MODDED.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[HostPatches] SetLobbyToModded threw: {e.Message}");
            }
        }
    }
}
