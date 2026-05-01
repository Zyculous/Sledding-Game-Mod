using UnityEngine;
using UnityEngine.SceneManagement;

namespace SledCoopMod
{
    public class SceneWatcher : MonoBehaviour
    {
        public static SceneWatcher? Instance { get; private set; }
        public static bool IsInGameplayScene { get; private set; }
        public static bool IsGamePaused { get; set; }

        private string _lastScene = "";

        private void Awake()
        {
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        public static void NotifyGameplayStarted()
        {
            if (IsInGameplayScene) return;
            IsInGameplayScene = true;
            Plugin.Log.LogInfo("[SceneWatcher] Gameplay started (native FishNet pawn registered).");
            NetworkedUiState.ForceStartupUiCleanup("gameplay-started");
        }

        public static void NotifyGameplayEnded()
        {
            if (!IsInGameplayScene) return;
            IsInGameplayScene = false;
            IsGamePaused = false;
            LocalPlayerManager.Instance?.ClearSessionPawnState();
            Plugin.Log.LogInfo("[SceneWatcher] Gameplay ended.");
        }

        private void Update()
        {
            string current = SceneManager.GetActiveScene().name;
            if (current == _lastScene) return;

            Plugin.Log.LogInfo($"[SceneWatcher] Scene changed: '{_lastScene}' -> '{current}'");
            _lastScene = current;
        }
    }
}
