using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SledCoopMod.Patches;

namespace SledCoopMod
{
    // Lazy patcher for EOS / FishyEOS targets.
    //
    // Background: PatchHelpers.SafeTypeByName builds its type cache from
    // AppDomain.CurrentDomain.GetAssemblies() on first call.  At BepInEx
    // chainload time, only BepInEx + Unity + Assembly-CSharp are loaded —
    // the PlayEveryWare.EpicOnlineServices, EOSSDK CSharp wrapper, and
    // Fishnet.Plugins.FishyEOS assemblies load lazily later (deserialised
    // from the Boot scene).  Patches whose TargetMethod() resolves through
    // SafeTypeByName at Plugin.Load therefore return null and Harmony skips
    // them, with the result that EOS boots normally even though every
    // suppression target is "covered" in EOSPatches.cs.
    //
    // Fix: skip [SledCoopEosPatch]-tagged classes during the initial
    // PatchAll, then subscribe to AppDomain.AssemblyLoad.  Whenever an
    // EOS-related assembly loads, invalidate the type cache and re-attempt
    // each tagged class.  Already-applied classes are tracked so we never
    // double-patch.
    internal static class EosLatePatcher
    {
        private static Harmony? _harmony;
        private static readonly HashSet<Type> _appliedTypes = new HashSet<Type>();
        private static Type[]? _eosPatchTypes;
        private static bool _hooked;

        // Names checked as substring (case-insensitive) against
        // AssemblyName.Name when AssemblyLoad fires.
        private static readonly string[] _triggerSubstrings =
        {
            "PlayEveryWare",
            "EpicOnlineServices",
            "EOSSDK",
            "FishyEOS",
            "Epic.OnlineServices",
            "EOS",
            "FishNet",
        };

        public static IReadOnlyCollection<Type> EosPatchTypes
        {
            get
            {
                _eosPatchTypes ??= DiscoverEosPatchTypes();
                return _eosPatchTypes;
            }
        }

        public static void Initialize(Harmony harmony)
        {
            _harmony = harmony;
            if (_hooked) return;
            _hooked = true;

            try
            {
                AppDomain.CurrentDomain.AssemblyLoad += OnAssemblyLoad;
                Plugin.Log.LogInfo(
                    "[EosLatePatcher] Subscribed to AppDomain.AssemblyLoad. " +
                    $"{EosPatchTypes.Count} EOS-tagged patch class(es) pending.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(
                    $"[EosLatePatcher] Failed to subscribe to AssemblyLoad: {e.Message}. " +
                    "Falling back to a single immediate retry.");
            }

            // Immediate retry in case any EOS assembly is already loaded but
            // wasn't indexed in the first SafeTypeByName cache build.
            TryApplyPending("initial");
        }

        private static void OnAssemblyLoad(object? sender, AssemblyLoadEventArgs args)
        {
            string asmName = args.LoadedAssembly?.GetName().Name ?? "";
            if (!IsEosRelated(asmName)) return;

            try
            {
                PatchHelpers.InvalidateCache();
                Plugin.Log.LogInfo(
                    $"[EosLatePatcher] EOS-related assembly loaded: '{asmName}'. " +
                    "Invalidating type cache and re-running tagged patches.");
                TryApplyPending(asmName);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(
                    $"[EosLatePatcher] OnAssemblyLoad('{asmName}') threw: {e.Message}");
            }
        }

        private static bool IsEosRelated(string asmName)
        {
            if (string.IsNullOrEmpty(asmName)) return false;
            foreach (var s in _triggerSubstrings)
            {
                if (asmName.IndexOf(s, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }
            return false;
        }

        private static void TryApplyPending(string trigger)
        {
            if (_harmony == null) return;
            int applied = 0, stillPending = 0;

            foreach (var type in EosPatchTypes)
            {
                if (_appliedTypes.Contains(type)) continue;

                bool didPatch = false;
                try
                {
                    var result = _harmony.CreateClassProcessor(type).Patch();
                    if (result != null && result.Count > 0)
                    {
                        didPatch = true;
                        applied++;
                        _appliedTypes.Add(type);
                    }
                    else
                    {
                        stillPending++;
                    }
                }
                catch (Exception e)
                {
                    // HarmonyX raises a "Patching exception in method ...
                    // TargetMethod()" when the target type/method still
                    // can't be resolved.  Treat as expected — try again on
                    // the next AssemblyLoad fire.
                    stillPending++;
                    Plugin.Log.LogDebug(
                        $"[EosLatePatcher] {type.Name} still pending after '{trigger}': {e.Message}");
                }

                if (didPatch && ModConfig.VerboseLogging.Value)
                    Plugin.Log.LogInfo($"[EosLatePatcher] Applied {type.Name}.");
            }

            if (applied > 0 || stillPending > 0)
                Plugin.Log.LogInfo(
                    $"[EosLatePatcher] After '{trigger}': applied={applied}, " +
                    $"stillPending={stillPending}, total={_appliedTypes.Count}/{EosPatchTypes.Count}.");
        }

        private static Type[] DiscoverEosPatchTypes()
        {
            var list = new List<Type>();
            try
            {
                foreach (var t in typeof(Plugin).Assembly.GetTypes())
                {
                    if (t == null || !t.IsClass) continue;
                    var attr = t.GetCustomAttribute<SledCoopEosPatchAttribute>(inherit: false);
                    if (attr != null) list.Add(t);
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning(
                    $"[EosLatePatcher] DiscoverEosPatchTypes threw: {e.Message}");
            }
            return list.ToArray();
        }
    }
}
