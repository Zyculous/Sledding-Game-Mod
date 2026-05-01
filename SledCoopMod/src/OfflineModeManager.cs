using System;
using System.Linq;
using System.Reflection;
using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;
using HarmonyLib;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace SledCoopMod
{
    internal static class OfflineModeManager
    {
        public static bool OfflineModeActive { get; private set; }
        public static bool CustomServerRequested { get; private set; }
        public static bool CustomServerActive { get; private set; }

        private static bool _customServerMissingManagerLogged;
        private static bool _transportSwapLogged;
        private static bool _offlineLobbyFlowCompleted;

        // Offline-only mode intentionally avoids native EOS lobby startup.
        internal static void PreResolve()
        {
            Plugin.Log.LogInfo("[OfflineModeManager] PreResolve skipped: offline-only local host mode avoids native EOS lobby startup.");
        }

        public static void StartOfflineLocalGame()
        {
            if (OfflineModeActive)
            {
                Plugin.Log.LogInfo("OfflineModeManager: custom server local game already requested.");
                return;
            }

            if (LocalPlayerManager.Instance == null)
            {
                Plugin.Log.LogWarning("OfflineModeManager: LocalPlayerManager is not available.");
                return;
            }

            ModConfig.LocalCoopEnabled.Value = true;
            OfflineModeActive = true;
            CustomServerRequested = true;
            Plugin.Log.LogInfo($"OfflineModeManager: custom server local game requested for {LocalPlayerManager.Instance.ActiveCount} player(s). " +
                "Existing joined slots will be used; no new slots were auto-joined. " +
                "FishNet loopback startup will run on the next mod tick.");
        }

        public static void ProcessPendingServerStartup()
        {
            if (!CustomServerRequested || CustomServerActive) return;
            TryStartCustomServer();
        }

        public static void ResetOfflineMode()
        {
            if (!OfflineModeActive && !CustomServerRequested && !CustomServerActive) return;
            OfflineModeActive = false;
            CustomServerRequested = false;
            CustomServerActive = false;
            _customServerMissingManagerLogged = false;
            _transportSwapLogged = false;
            _offlineLobbyFlowCompleted = false;
            Plugin.Log.LogInfo("OfflineModeManager: offline mode reset.");
        }

        internal static void HandleOfflineCreateLobby(object? lobbyManagerInstance)
        {
            if (!OfflineModeActive)
            {
                ModConfig.LocalCoopEnabled.Value = true;
                OfflineModeActive = true;
                CustomServerRequested = true;
            }

            TryStartCustomServer();
            CompleteOfflineLobbyFlow(lobbyManagerInstance);
        }


        private static bool TryStartCustomServer()
        {
            if (CustomServerActive) return true;

            var nm = GetNetworkManager();
            if (nm == null)
            {
                if (!_customServerMissingManagerLogged)
                {
                    _customServerMissingManagerLogged = true;
                    Plugin.Log.LogWarning("OfflineModeManager: waiting for FishNet NetworkManager before starting custom server.");
                }
                return false;
            }

            TryUseTugboatTransport(nm);

            bool serverStarted = true;
            try
            {
                if (nm.IsServerStarted)
                {
                    Plugin.Log.LogInfo("OfflineModeManager: server already running.");
                }
                else if (nm.ServerManager == null)
                {
                    Plugin.Log.LogWarning("OfflineModeManager: NetworkManager has no ServerManager yet.");
                    return false;
                }
                else
                {
                    serverStarted = nm.ServerManager.StartConnection();
                    Plugin.Log.LogInfo(serverStarted
                        ? "OfflineModeManager: loopback server started."
                        : "OfflineModeManager: ServerManager.StartConnection() returned false.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"OfflineModeManager: ServerManager.StartConnection threw: {e.Message}");
                return false;
            }

            if (!serverStarted) return false;

            _customServerMissingManagerLogged = false;
            CustomServerActive = true;
            TryStartClientConnection(nm);
            Plugin.Log.LogInfo("OfflineModeManager: custom server backend active.");
            CompleteOfflineLobbyFlow(null);
            return true;
        }

        private static NetworkManager? GetNetworkManager()
        {
            return NetworkManagerFinder.Find();
        }

        private static void TryUseTugboatTransport(NetworkManager nm)
        {
            try
            {
                var tm = nm.TransportManager;
                if (tm == null) return;

                if (tm.Transport is Tugboat existingTugboat)
                {
                    ConfigureTugboat(existingTugboat, nm);
                    if (!_transportSwapLogged)
                    {
                        _transportSwapLogged = true;
                        Plugin.Log.LogInfo("OfflineModeManager: FishNet Tugboat transport already active.");
                    }
                    return;
                }

                var current = tm.Transport;
                var tugboat = nm.gameObject.GetComponent<Tugboat>();
                if (tugboat == null)
                    tugboat = nm.gameObject.AddComponent<Tugboat>();

                SetFishNetTransportSubscriptions(nm, subscribe: false);
                ConfigureTugboat(tugboat, nm);
                tm.Transport = tugboat;
                ReinitializeTransportManager(nm);
                SetFishNetTransportSubscriptions(nm, subscribe: true);

                if (current != null && current != tugboat)
                    current.enabled = false;

                if (!_transportSwapLogged)
                {
                    _transportSwapLogged = true;
                    Plugin.Log.LogInfo($"OfflineModeManager: swapped FishNet transport from '{current?.GetType().FullName ?? "null"}' to Tugboat loopback.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"OfflineModeManager: Tugboat transport swap failed: {e.Message}");
            }
        }

        private static void ReinitializeTransportManager(NetworkManager nm)
        {
            try
            {
                var tm = nm.TransportManager;
                if (tm == null) return;

                var method = tm.GetType().GetMethod(
                    "InitializeOnce_Internal",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                method?.Invoke(tm, new object[] { nm });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"OfflineModeManager: TransportManager reinitialise failed: {e.Message}");
            }
        }

        private static void SetFishNetTransportSubscriptions(NetworkManager nm, bool subscribe)
        {
            InvokeBoolMethod(nm.ServerManager, "SubscribeToTransport", subscribe);
            InvokeBoolMethod(nm.ClientManager, "SubscribeToEvents", subscribe);
        }

        private static void InvokeBoolMethod(object? instance, string methodName, bool value)
        {
            if (instance == null) return;
            try
            {
                var method = instance.GetType().GetMethod(
                    methodName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                method?.Invoke(instance, new object[] { value });
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"OfflineModeManager: {instance.GetType().Name}.{methodName}({value}) failed: {e.Message}");
            }
        }

        private static void ConfigureTugboat(Tugboat tugboat, NetworkManager nm)
        {
            try
            {
                tugboat.enabled = true;
                tugboat.SetClientAddress("127.0.0.1");
                tugboat.SetServerBindAddress("0.0.0.0", IPAddressType.IPv4);
                if (tugboat.GetPort() == 0)
                    tugboat.SetPort(7770);

                int maxPlayers = Math.Max(1, ModConfig.MaxLocalPlayers.Value);
                if (tugboat.GetMaximumClients() < maxPlayers)
                    tugboat.SetMaximumClients(maxPlayers);

                tugboat.Initialize(nm, 0);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"OfflineModeManager: ConfigureTugboat failed: {e.Message}");
            }
        }

        private static void TryStartClientConnection(NetworkManager nm)
        {
            if (nm.ClientManager == null) return;
            try
            {
                if (nm.IsClientStarted)
                {
                    Plugin.Log.LogInfo("OfflineModeManager: client already connected.");
                    return;
                }
                // Connect to loopback (default address used by FishNet for offline host).
                bool clientStarted = nm.ClientManager.StartConnection();
                Plugin.Log.LogInfo(clientStarted
                    ? "OfflineModeManager: client connected to loopback server (full host mode)."
                    : "OfflineModeManager: ClientManager.StartConnection() returned false — server-only mode active.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"OfflineModeManager: ClientManager.StartConnection threw: {e.Message}");
            }
        }

        private static void CompleteOfflineLobbyFlow(object? lobbyManagerInstance)
        {
            if (_offlineLobbyFlowCompleted) return;

            try
            {
                var uiType = Patches.PatchHelpers.SafeTypeByName("UiReferenceController");
                var ui = uiType == null ? null : GetSingletonInstance(uiType);
                var closeMenus = uiType?.GetMethod(
                    "CloseAllOpenMenus",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                closeMenus?.Invoke(ui, new object[] { false });
                if (closeMenus != null)
                    Plugin.Log.LogInfo("OfflineModeManager: closed lobby menus for offline custom server flow.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"OfflineModeManager: failed to close lobby menus: {e.Message}");
            }

            try
            {
                var lobbyType = Patches.PatchHelpers.SafeTypeByName("_Scripts.Managers.LobbyManager");
                var lobby = lobbyManagerInstance ?? (lobbyType == null ? null : GetSingletonInstance(lobbyType));
                var wait = lobby?.GetType().GetMethod(
                    "WaitForConnection",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (wait != null)
                {
                    wait.Invoke(lobby, null);
                    _offlineLobbyFlowCompleted = true;
                    Plugin.Log.LogInfo("OfflineModeManager: invoked LobbyManager.WaitForConnection() for offline custom server flow.");
                }
                else
                {
                    Plugin.Log.LogWarning("OfflineModeManager: LobbyManager.WaitForConnection() not found; custom server is active but vanilla lobby transition could not be bridged.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"OfflineModeManager: offline lobby flow bridge failed: {e.Message}");
            }
        }

        private static bool TryInvokeHostMethod(Type type, string typeName, string methodName, object[]? args = null)
        {
            if (type == null) return false;

            var instance = GetSingletonInstance(type);
            if (instance == null) return false;

            var method = AccessTools.Method(type, methodName);
            if (method == null) return false;

            try
            {
                method.Invoke(instance, args);
                Plugin.Log.LogInfo($"OfflineModeManager: invoked {typeName}.{methodName}().");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"OfflineModeManager: failed to invoke {typeName}.{methodName}: {e.Message}");
                return false;
            }
        }

        private static object? GetSingletonInstance(Type type)
        {
            var instanceProperty = type.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static);
            if (instanceProperty != null)
            {
                var result = instanceProperty.GetValue(null);
                if (result != null) return result;
            }

            var instanceField = type.GetField("Instance", BindingFlags.Public | BindingFlags.Static);
            if (instanceField != null)
            {
                var result = instanceField.GetValue(null);
                if (result != null) return result;
            }

            return FindObjectOfType(type);
        }

        private static object? FindObjectOfType(Type type)
        {
            try
            {
                var findMethod = typeof(UObject).GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "FindObjectOfType" && m.IsGenericMethodDefinition && m.GetParameters().Length == 0);
                if (findMethod == null) return null;

                var generic = findMethod.MakeGenericMethod(type);
                return generic.Invoke(null, null);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"OfflineModeManager: FindObjectOfType({type.Name}) failed: {e.Message}");
                return null;
            }
        }
    }
}
