using System;
using UnityEngine;

namespace SledCoopMod
{
    /// <summary>
    /// MonoBehaviour attached to a persistent DontDestroyOnLoad GameObject
    /// by Plugin.Load().  Bootstraps all mod sub-systems in the correct order
    /// and ensures they survive scene transitions.
    /// </summary>
    public class ModBootstrap : MonoBehaviour
    {
        private void Awake()
        {
            DontDestroyOnLoad(gameObject);

            // Keep error logging compact. Repeated IL2CPP exception stacks can flood
            // Player.log badly enough to stall the game under Wine.
            try
            {
                Application.SetStackTraceLogType(LogType.Exception, StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Error,     StackTraceLogType.None);
                Application.SetStackTraceLogType(LogType.Assert,    StackTraceLogType.None);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ModBootstrap] SetStackTraceLogType threw: {e.Message}");
            }

            // Dissonance + EOS log filtering is handled by Harmony Prefix patches on
            // UnityEngine.Logger.{Log,LogFormat} (see LogFilter.cs).  Those are wired up
            // automatically by the [HarmonyPatch] sweep in Plugin.Load — no manual install
            // call needed here.  An earlier ILogHandler-replacement approach blew up
            // because UnityEngine.ILogHandler appears as a CLASS (not an interface) in the
            // Il2CppInterop runtime metadata, so any "class : ILogHandler" declaration
            // throws TypeLoadException the moment its type is touched, killing this Awake
            // before any of the AddComponent<...>() calls below run.

            // Attach all manager components to the same persistent object.
            AddManager<LocalPlayerManager>();
            AddManager<InputRouter>();
            AddManager<SceneWatcher>();
            AddManager<NetworkedInstanceManager>();
            AddManager<NetworkedRosterManager>();
            AddManager<NetworkedFocusManager>();
            AddManager<NetworkedRewiredIsolationManager>();
            AddManager<NetworkedUiStateManager>();
            AddManager<LocalCoopUI>();
            AddManager<ModSettingsUi>();
            AddManager<DebugInspector>();

            Plugin.Log.LogInfo(
                $"SledCoopMod: all manager components attached. " +
                $"MaxLocalPlayers={ModConfig.MaxLocalPlayers.Value}, " +
                $"MultiDisplay={ModConfig.MultiDisplayEnabled.Value}, " +
                $"OnlineHybrid={ModConfig.OnlineHybridEnabled.Value}");
        }

        private void Start()
        {
        }

        private void AddManager<T>() where T : Component
        {
            try
            {
                gameObject.AddComponent<T>();
            }
            catch (Exception e)
            {
                Plugin.Log.LogError($"[ModBootstrap] Failed to attach manager {typeof(T).Name}: {e.GetType().Name}: {e.Message}");
            }
        }

        private int _heartbeatFrame;

        // Counts ticks observed since gameplay started so we can log a finer-grained
        // heartbeat for the first 30 ticks after a host-pawn spawn.  Without this we
        // can't distinguish "Update never ran again" from "Update ran but the next
        // 300-frame heartbeat hadn't fired yet" when investigating post-spawn freezes.
        private int _ticksSinceGameplayStart;
        private bool _wasInGameplay;

        private void Update()
        {
            if (++_heartbeatFrame >= 300)
            {
                _heartbeatFrame = 0;
                Plugin.Log.LogInfo(
                    $"[ModBootstrap] Heartbeat frame={UnityEngine.Time.frameCount} " +
                    $"scene='{UnityEngine.SceneManagement.SceneManager.GetActiveScene().name}'");
            }

            // Per-tick heartbeat for the first 30 ticks after gameplay starts —
            // gives us frame-level resolution to spot a deadlock in the freeze window.
            if (SceneWatcher.IsInGameplayScene)
            {
                if (!_wasInGameplay) { _wasInGameplay = true; _ticksSinceGameplayStart = 0; }
                if (_ticksSinceGameplayStart < 30)
                {
                    _ticksSinceGameplayStart++;
                    Plugin.Log.LogInfo($"[ModBootstrap] post-spawn tick {_ticksSinceGameplayStart} (frame={UnityEngine.Time.frameCount}).");
                }
            }
            else
            {
                _wasInGameplay = false;
            }

            // Process host-pawn setup deferred by one frame from PlayerControl.Awake.
            // Running here lets the host pawn's OnEnable and Start fire first so the
            // native camera and FishNet ownership settle before we register gameplay.
            SpawnHooks.ProcessPendingHostPawn();

            OfflineModeManager.ProcessPendingServerStartup();

        }
    }
}
