using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SledCoopMod.Patches
{
    [HarmonyPatch]
    internal static class Patch_PlayerHoldingController_HostGamepadBleed
    {
        static IEnumerable<MethodBase> TargetMethods()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerHoldingController");
            if (t == null)
                yield break;

            foreach (string name in new[]
            {
                "Cmd_StartPickingUpSnow",
                "Cmd_StopPickingUpSnow",
                "StartChargeThrow_Local",
                "Cmd_StartChargeThrow",
                "Cmd_CancelChargeThrow",
                "Cmd_ThrowObject",
                "DoActionStart_Local",
                "Cmd_DoActionStart_Server",
                "DoActionEnd_Local",
                "Cmd_DoActionEnd_Server",
                "DoSecondaryActionStart_Local",
                "Cmd_DoSecondaryActionStart_Server",
                "DoSecondaryActionEnd_Local",
                "Cmd_DoSecondaryActionEnd_Server",
            })
            {
                var method = PatchHelpers.FindMethod(t, name);
                if (method != null)
                    yield return method;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return true;

            if (NetworkedUiState.IsNativeMenuOpen())
                return false;

            if (!InputBlockHelper.ShouldSuppressHostHandAction())
                return true;

            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogDebug($"[NetworkedInputIsolation] Suppressed host hand action '{__originalMethod.Name}' from claimed child gamepad.");

            return false;
        }
    }
}
