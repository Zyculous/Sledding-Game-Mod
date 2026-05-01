using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SledCoopMod.Patches
{
    internal static class PatchHelpers
    {
        private static readonly Dictionary<string, Type> TypeCache = new Dictionary<string, Type>(StringComparer.Ordinal);
        private static bool _typeCacheBuilt;

        internal static Type? SafeTypeByName(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            EnsureTypeCache();
            TypeCache.TryGetValue(name, out var t);
            return t;
        }

        internal static void InvalidateCache()
        {
            lock (TypeCache)
            {
                TypeCache.Clear();
                _typeCacheBuilt = false;
            }
        }

        private static void EnsureTypeCache()
        {
            if (_typeCacheBuilt) return;
            _typeCacheBuilt = true;

            int safe = 0, skipped = 0;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                string asmName = asm.GetName().Name ?? "";
                if (asmName.StartsWith("UnityEngine.") || asmName == "__Generated")
                {
                    skipped++;
                    continue;
                }

                Type[] types;
                try { types = asm.GetTypes(); }
                catch (ReflectionTypeLoadException rtle) { types = rtle.Types ?? Array.Empty<Type>(); }
                catch { continue; }

                foreach (var type in types)
                {
                    if (type == null) continue;
                    TypeCache[type.Name] = type;
                    if (!string.IsNullOrEmpty(type.FullName))
                        TypeCache[type.FullName!] = type;
                }
                safe++;
            }

            Plugin.Log.LogInfo($"[PatchHelpers] Type cache built: {TypeCache.Count} entries from {safe} assemblies (skipped {skipped}).");
        }

        internal static MethodBase? FindMethod(Type t, params string[] names)
        {
            const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly;
            foreach (string name in names)
            {
                var m = t.GetMethod(name, flags);
                if (m != null) return m;
            }
            return null;
        }

        internal static MethodBase? FindMethodInherited(Type t, string name)
        {
            try
            {
                const BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static;
                return t.GetMethod(name, flags);
            }
            catch { return null; }
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerControl_Awake
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerControl");
            return t == null ? null : PatchHelpers.FindMethod(t, "Awake");
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            var go = ReflectionHelper.GetGameObject(__instance);
            if (go != null)
                SpawnHooks.QueueHostPawn(go);
        }
    }

    internal static class PlayerLocalInputNetworkedHelper
    {
        public static bool TryGetNetworkedMove(out Vector2 value)
        {
            value = Vector2.zero;
            return InputBlockHelper.TryGetNetworkedChildMoveInput(out value);
        }

        public static bool TryGetNetworkedLook(out Vector2 value)
        {
            value = Vector2.zero;
            return InputBlockHelper.TryGetNetworkedChildLookInput(out value);
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerLocalInput_GetMoveInput
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerLocalInput");
            return t == null ? null : PatchHelpers.FindMethod(t, "GetMoveInput");
        }

        [HarmonyPrefix]
        static bool Prefix(ref Vector2 __result)
        {
            if (NetworkedUiState.IsNativeMenuOpen())
            {
                __result = Vector2.zero;
                return false;
            }

            if (PlayerLocalInputNetworkedHelper.TryGetNetworkedMove(out var move))
            {
                __result = move;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerLocalInput_GetMoveInputRaw
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerLocalInput");
            return t == null ? null : PatchHelpers.FindMethod(t, "GetMoveInputRaw");
        }

        [HarmonyPrefix]
        static bool Prefix(ref Vector2 __result)
        {
            if (NetworkedUiState.IsNativeMenuOpen())
            {
                __result = Vector2.zero;
                return false;
            }

            if (PlayerLocalInputNetworkedHelper.TryGetNetworkedMove(out var move))
            {
                __result = move;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerLocalInput_GetLookInput
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerLocalInput");
            return t == null ? null : PatchHelpers.FindMethod(t, "GetLookInput");
        }

        [HarmonyPrefix]
        static bool Prefix(ref Vector2 __result)
        {
            if (NetworkedUiState.IsNativeMenuOpen())
            {
                __result = Vector2.zero;
                return false;
            }

            if (PlayerLocalInputNetworkedHelper.TryGetNetworkedLook(out var look))
            {
                __result = look;
                return false;
            }

            return true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerLocalInput_GetLookInputRaw
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerLocalInput");
            return t == null ? null : PatchHelpers.FindMethod(t, "GetLookInputRaw");
        }

        [HarmonyPrefix]
        static bool Prefix(ref Vector2 __result)
        {
            if (NetworkedUiState.IsNativeMenuOpen())
            {
                __result = Vector2.zero;
                return false;
            }

            if (PlayerLocalInputNetworkedHelper.TryGetNetworkedLook(out var look))
            {
                __result = look;
                return false;
            }

            return true;
        }
    }
}
