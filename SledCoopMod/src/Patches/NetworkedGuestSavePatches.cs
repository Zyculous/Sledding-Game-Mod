using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;

namespace SledCoopMod.Patches
{
    [HarmonyPatch]
    internal static class Patch_PlayerPrefsManager_GuestProfileFiles
    {
        private static readonly HashSet<string> LoggedProfiles = new HashSet<string>(StringComparer.Ordinal);
        private static readonly HashSet<string> LoggedFiles = new HashSet<string>(StringComparer.Ordinal);

        static IEnumerable<MethodBase> TargetMethods()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerPrefsManager");
            if (t == null)
                yield break;

            foreach (string name in new[]
            {
                "InitialiseSavedSettings",
                "InitialiseSavedStats",
                "SaveSettingsAsync",
                "SaveStatsAsync",
                "CreateNewPlayerSavedStats",
                "CreateNewPlayerSavedSettings",
            })
            {
                var method = PatchHelpers.FindMethod(t, name);
                if (method != null)
                    yield return method;
            }
        }

        [HarmonyPrefix]
        static void Prefix(ref string fileName)
        {
            if (!NetworkedGuestProfile.TryRewriteGameSaveFileName(fileName, out string rewritten))
                return;

            fileName = rewritten;

            string profile = NetworkedInstanceManager.Instance?.ProfileName ?? "";
            if (LoggedProfiles.Add(profile))
                Plugin.Log.LogInfo($"[NetworkedGuestSave] Redirecting child save data to guest profile '{profile}'.");

            if (LoggedFiles.Add(rewritten))
            {
                string mode = NetworkedGuestProfile.GuestSaveFileExists(rewritten)
                    ? "loading existing"
                    : "creating default";
                Plugin.Log.LogInfo($"[NetworkedGuestSave] {mode} guest save file '{rewritten}'.");
            }
        }
    }
}
