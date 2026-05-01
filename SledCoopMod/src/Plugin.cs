using System;
using System.Reflection;
using BepInEx;
using BepInEx.Unity.IL2CPP;
using BepInEx.Logging;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace SledCoopMod
{
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public class Plugin : BasePlugin
    {
        internal new static ManualLogSource Log = null!;
        internal static Plugin Instance = null!;

        private Harmony? _harmony;

        public override void Load()
        {
            Instance = this;
            Log = base.Log;
            Log.LogInfo($"{MyPluginInfo.PLUGIN_NAME} v{MyPluginInfo.PLUGIN_VERSION} loading...");

            ModConfig.Init(Config);

            if (!ModConfig.Enabled.Value)
            {
                Log.LogWarning("SledCoopMod is disabled via config. Skipping load.");
                return;
            }

            // Apply all Harmony patches in this assembly, skipping any whose
            // TargetMethod() returns null (type/method not found in interop stubs).
            // Skips are expected — many patches probe optional types (race UI, etc.)
            // that don't exist in every build — so they log at Debug, not Warning.
            //
            // Patches tagged [SledCoopEosPatch] target lazily-loaded assemblies
            // (PlayEveryWare/EOSSDK/FishyEOS samples) that are NOT yet loaded at
            // BepInEx chainload.  EosLatePatcher applies them later via
            // AppDomain.AssemblyLoad — skip them here so they don't waste a
            // patch-attempt against a not-yet-indexed type cache.
            _harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            int patched = 0, skipped = 0, deferred = 0;
            foreach (var type in AccessTools.GetTypesFromAssembly(typeof(Plugin).Assembly))
            {
                if (type.GetCustomAttribute<SledCoopEosPatchAttribute>(false) != null)
                {
                    deferred++;
                    continue;
                }
                try
                {
                    var result = _harmony.CreateClassProcessor(type).Patch();
                    if (result != null && result.Count > 0) patched++;
                }
                catch (Exception ex)
                {
                    skipped++;
                    // HarmonyX raises a "Patching exception in method ... TargetMethod()"
                    // exception when TargetMethod returns null.  Treat as expected.
                    Log.LogDebug($"[Harmony] Skipped {type.Name}: {ex.Message}");
                }
            }
            Log.LogInfo($"Harmony: {patched} patch(es) applied, {skipped} skipped, {deferred} deferred to EosLatePatcher.");

            SpawnHooks.PreResolve();
            OfflineModeManager.PreResolve();

            // Subscribe to AppDomain.AssemblyLoad so deferred EOS patches can be
            // applied once their target assemblies actually load.  Initialize()
            // also runs an immediate retry in case any EOS assembly is somehow
            // already loaded at this point.
            EosLatePatcher.Initialize(_harmony);

            // Register all custom MonoBehaviours with Il2CppInterop before use.
            // Without this, AddComponent<T>() throws TypeInitializationException.
            ClassInjector.RegisterTypeInIl2Cpp<ModBootstrap>();
            ClassInjector.RegisterTypeInIl2Cpp<LocalPlayerManager>();
            ClassInjector.RegisterTypeInIl2Cpp<InputRouter>();
            ClassInjector.RegisterTypeInIl2Cpp<SceneWatcher>();
            ClassInjector.RegisterTypeInIl2Cpp<NetworkedInstanceManager>();
            ClassInjector.RegisterTypeInIl2Cpp<NetworkedRosterManager>();
            ClassInjector.RegisterTypeInIl2Cpp<NetworkedFocusManager>();
            ClassInjector.RegisterTypeInIl2Cpp<NetworkedRewiredIsolationManager>();
            ClassInjector.RegisterTypeInIl2Cpp<NetworkedUiStateManager>();
            ClassInjector.RegisterTypeInIl2Cpp<LocalCoopUI>();

            // Spawn a persistent GameObject that hosts our manager components.
            // This runs in the BepInEx/Unity player-loop context, so it survives scene loads.
            // We use a new GameObject rather than AddComponent<T>() because the IL2CPP
            // interop constraint requires Il2CppObjectBase; spawning a GO is always safe.
            var modGo = new UnityEngine.GameObject("SledCoopMod");
            modGo.AddComponent<ModBootstrap>();
            Log.LogInfo("ModBootstrap component attached.");
        }

        public override bool Unload()
        {
            _harmony?.UnpatchSelf();
            return base.Unload();
        }
    }
}
