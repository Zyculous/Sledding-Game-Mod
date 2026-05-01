using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using FishNet.Managing;
using FishNet.Transporting;
using FishNet.Transporting.Tugboat;
using Microsoft.Win32;
using UnityEngine;
using UObject = UnityEngine.Object;

namespace SledCoopMod
{
    public class NetworkedInstanceManager : MonoBehaviour
    {
        public static NetworkedInstanceManager? Instance { get; private set; }

        private readonly Dictionary<int, Process> _children = new();
        private NetworkedInstanceArgs _args = null!;
        private bool _startRequested;
        private bool _hostStarted;
        private bool _clientStarted;
        private bool _missingNetworkManagerLogged;
        private bool _startLoopErrorLogged;
        private bool _lobbyBridgePending;
        private int _nextLobbyBridgeFrame;
        private bool _windowLayoutPending;
        private bool _windowLayoutAllowHostMove;
        private int _windowLayoutPlayerCount = 1;
        private int _nextWindowLayoutFrame;
        private int _windowLayoutDeadlineFrame;
        private LocalInputProvider? _assignedInputProvider;
        private bool _quitRequested;
        private int _quitFallbackFrame;
        private bool _quitFallbackLogged;
        private static volatile bool _quitInProgress;
        private static int _quitKillFallbackStarted;

        public static bool IsNetworkedModeConfigured =>
            ModConfig.NetworkedInstancesEnabled.Value;

        public bool IsNetworkedMode =>
            IsNetworkedModeConfigured;

        public bool IsChildClient => _args?.IsClient == true;

        public static bool ShouldHideNetworkedOverlay =>
            IsNetworkedModeConfigured
            && (Instance?.IsChildClient == true
                || Instance?._startRequested == true
                || Instance?._hostStarted == true
                || Instance?._clientStarted == true
                || SceneWatcher.IsInGameplayScene);

        public static bool IsQuitInProgress => _quitInProgress;

        public int SlotIndex => _args?.Slot ?? 0;

        public int ConfiguredPlayerCount => _args?.PlayerCount ?? 1;

        public string ProfileName => _args?.Profile ?? "Host";

        public AssignedInputDevice AssignedDevice => _args?.Device ?? AssignedInputDevice.None;

        public bool TryGetConfiguredProfileName(int slotIndex, out string profile)
        {
            profile = "";
            return _args?.TryGetProfileForSlot(slotIndex, out profile) == true;
        }

        public bool TryGetAssignedMoveInput(out Vector2 result)
        {
            result = Vector2.zero;
            if (!IsNetworkedMode || !IsChildClient || _assignedInputProvider == null) return false;
            result = _assignedInputProvider.GetMoveInput();
            return true;
        }

        public bool TryGetAssignedLookInput(out Vector2 result)
        {
            result = Vector2.zero;
            if (!IsNetworkedMode || !IsChildClient || _assignedInputProvider == null) return false;
            result = _assignedInputProvider.GetLookInput();
            return true;
        }

        public bool TryGetAssignedButtonDown(string action, out bool result)
        {
            result = false;
            if (!IsNetworkedMode || !IsChildClient || _assignedInputProvider == null) return false;
            result = _assignedInputProvider.GetButtonDown(action);
            return true;
        }

        public string Status
        {
            get
            {
                if (!IsNetworkedMode) return "Networked instances disabled.";
                if (IsChildClient) return _clientStarted ? "Child client connected/requested." : "Child client waiting for server.";
                if (_hostStarted) return $"Networked host active. Children={_children.Count}.";
                if (_startRequested) return "Networked host start requested.";
                return "Ready for networked multi-instance start.";
            }
        }

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
            _args = NetworkedInstanceArgs.Parse();
            if (_args.IsClient && _args.Device != AssignedInputDevice.None)
                _assignedInputProvider = new LocalInputProvider { Device = _args.Device };
            if (_args.IsClient)
                QueueWindowLayout(Math.Max(_args.PlayerCount, _args.Slot + 1), allowHostMove: true);

            Plugin.Log.LogInfo(
                $"[NetworkedInstanceManager] role={_args.Role} host={_args.HostAddress} port={_args.Port} " +
                $"slot={_args.Slot} profile='{_args.Profile}' device={_args.Device}");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            StopChildren();
        }

        private void Update()
        {
            ProcessPendingQuitFallback();

            if (!IsNetworkedMode) return;

            ReapChildren();

            try
            {
                if (_args.IsClient)
                {
                    TryStartChildClient();
                    ProcessPendingLobbyBridge();
                    ProcessPendingWindowLayout();
                    return;
                }

                if (_startRequested && !_hostStarted)
                    TryStartHost();

                ProcessPendingLobbyBridge();
                ProcessPendingWindowLayout();
            }
            catch (Exception e)
            {
                if (_startLoopErrorLogged) return;
                _startLoopErrorLogged = true;
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] Start loop failed: {e.GetType().Name}: {e.Message}");
            }
        }

        public void StartNetworkedLocalGame()
        {
            if (!IsNetworkedModeConfigured)
            {
                OfflineModeManager.StartOfflineLocalGame();
                return;
            }

            if (_startRequested || _hostStarted)
            {
                int currentPlayerCount = GetActivePlayerCountForLayout();
                if (_hostStarted)
                    QueueHostSplitLayoutIfReady(currentPlayerCount);
                return;
            }

            _startRequested = true;
            ModConfig.LocalCoopEnabled.Value = true;
            int playerCount = GetActivePlayerCountForLayout();
            Plugin.Log.LogInfo($"[NetworkedInstanceManager] Networked local game requested for {LocalPlayerManager.Instance?.ActiveCount ?? 1} player(s).");
        }

        public static bool TryStartConfiguredNetworkedGame()
        {
            if (!IsNetworkedModeConfigured) return false;
            if (Instance == null)
            {
                Plugin.Log.LogWarning("[NetworkedInstanceManager] Networked mode is configured but manager instance is unavailable.");
                return true;
            }

            if (Instance.IsChildClient)
            {
                Plugin.Log.LogInfo("[NetworkedInstanceManager] Ignoring host-start request in child client instance.");
                return true;
            }

            Instance.StartNetworkedLocalGame();
            return true;
        }

        public static bool TryEnsureConfiguredChildForSlot(int slotIndex)
        {
            if (!IsNetworkedModeConfigured) return false;
            if (Instance == null)
            {
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] Cannot launch child slot {slotIndex}; manager instance is unavailable.");
                return true;
            }

            Instance.EnsureChildForSlot(slotIndex);
            return true;
        }

        public static bool TryStopConfiguredChildForSlot(int slotIndex)
        {
            if (!IsNetworkedModeConfigured) return false;
            Instance?.StopChildForSlot(slotIndex);
            return true;
        }

        public static bool TryHandleConfiguredQuitToDesktop()
        {
            if (!IsNetworkedModeConfigured) return false;

            if (Instance == null)
            {
                _quitInProgress = true;
                StartQuitKillFallback();
                try { Application.Quit(); } catch { }
                return true;
            }

            Instance.RequestQuitToDesktop("quit button");
            return true;
        }

        public static bool TryHandleConfiguredLeaveLobby()
        {
            if (!IsNetworkedModeConfigured) return false;

            if (Instance == null)
                return true;

            Instance.LeaveNetworkedLobby();
            return true;
        }

        public void EnsureChildForSlot(int slotIndex)
        {
            if (!IsNetworkedMode || _args.IsClient) return;
            if (!_hostStarted)
            {
                Plugin.Log.LogInfo($"[NetworkedInstanceManager] Queued child slot {slotIndex}; host has not started yet.");
                return;
            }
            if (LaunchChild(slotIndex))
                QueueHostSplitLayoutIfReady(GetActivePlayerCountForLayout());
        }

        public void StopChildForSlot(int slotIndex)
        {
            if (_children.TryGetValue(slotIndex, out var proc))
            {
                int pid = 0;
                try { pid = proc.Id; }
                catch { }

                bool stopped = TryKillProcess(proc);
                if (!stopped && pid > 0)
                {
                    try
                    {
                        using var byId = Process.GetProcessById(pid);
                        stopped = TryKillProcess(byId);
                    }
                    catch (Exception e)
                    {
                        Plugin.Log.LogWarning($"[NetworkedInstanceManager] Failed to stop child slot {slotIndex} pid {pid}: {e.Message}");
                    }
                }

                if (!stopped)
                    Plugin.Log.LogWarning($"[NetworkedInstanceManager] Child slot {slotIndex} stop was requested, but process termination could not be confirmed.");

                _children.Remove(slotIndex);
            }
        }

        public void StopChildren()
        {
            foreach (int slot in new List<int>(_children.Keys))
                StopChildForSlot(slot);
        }

        private void RequestQuitToDesktop(string source)
        {
            if (_quitRequested)
                return;

            _quitRequested = true;
            _quitFallbackFrame = UnityEngine.Time.frameCount + 180;
            _quitInProgress = true;
            StartQuitKillFallback();

            Plugin.Log.LogInfo($"[NetworkedInstanceManager] Networked {source} requested; terminating local instances before quitting.");

            try
            {
                if (!IsChildClient)
                    StopChildren();
            }
            catch { }

            try { Application.Quit(); }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] Application.Quit failed: {e.Message}");
            }
        }

        private void LeaveNetworkedLobby()
        {
            if (_quitInProgress)
                return;

            if (IsChildClient)
            {
                RequestQuitToDesktop("child leave lobby");
                return;
            }

            Plugin.Log.LogInfo("[NetworkedInstanceManager] Leaving networked local lobby.");

            StopChildren();
            StopFishNetConnections(sendDisconnectMessage: true);

            _startRequested = false;
            _hostStarted = false;
            _clientStarted = false;
            _lobbyBridgePending = false;

            SceneWatcher.NotifyGameplayEnded();
            NetworkedUiState.ReturnToMainMenuAfterNetworkLeave();
        }

        private void ProcessPendingQuitFallback()
        {
            if (!_quitRequested || UnityEngine.Time.frameCount < _quitFallbackFrame)
                return;

            if (!_quitFallbackLogged)
            {
                _quitFallbackLogged = true;
                Plugin.Log.LogWarning("[NetworkedInstanceManager] Unity quit did not complete; terminating current process.");
            }

            try { Process.GetCurrentProcess().Kill(entireProcessTree: false); }
            catch { }
        }

        private static void StartQuitKillFallback()
        {
            if (Interlocked.Exchange(ref _quitKillFallbackStarted, 1) == 1)
                return;

            try
            {
                var thread = new Thread(() =>
                {
                    try { Thread.Sleep(5000); }
                    catch { }

                    try { Process.GetCurrentProcess().Kill(entireProcessTree: false); }
                    catch { }
                })
                {
                    IsBackground = true,
                    Name = "SledCoopQuitFallback",
                };
                thread.Start();
            }
            catch { }
        }

        private static void StopFishNetConnections(bool sendDisconnectMessage)
        {
            var nm = GetNetworkManager();
            if (nm == null)
                return;

            try
            {
                if (nm.IsClientStarted)
                    nm.ClientManager.StopConnection();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] Client stop failed: {e.Message}");
            }

            try
            {
                if (nm.IsServerStarted)
                    nm.ServerManager.StopConnection(sendDisconnectMessage);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] Server stop failed: {e.Message}");
            }
        }

        private void TryStartHost()
        {
            var nm = GetNetworkManager();
            if (nm == null)
            {
                if (!_missingNetworkManagerLogged)
                {
                    _missingNetworkManagerLogged = true;
                    Plugin.Log.LogWarning("[NetworkedInstanceManager] Waiting for FishNet NetworkManager before host start.");
                }
                return;
            }

            if (!ConfigureTugboat(nm, "0.0.0.0", "127.0.0.1", ModConfig.NetworkedInstancePort.Value))
                return;

            try
            {
                if (!nm.IsServerStarted && !nm.ServerManager.StartConnection((ushort)ModConfig.NetworkedInstancePort.Value))
                {
                    Plugin.Log.LogWarning("[NetworkedInstanceManager] ServerManager.StartConnection returned false.");
                    return;
                }

                if (!nm.IsClientStarted && !nm.ClientManager.StartConnection("127.0.0.1", (ushort)ModConfig.NetworkedInstancePort.Value))
                    Plugin.Log.LogWarning("[NetworkedInstanceManager] Host ClientManager.StartConnection returned false.");

                _hostStarted = true;
                _missingNetworkManagerLogged = false;
                Plugin.Log.LogInfo("[NetworkedInstanceManager] Tugboat host/server started.");

                try { QueueLobbyTransition(); }
                catch (Exception e) { Plugin.Log.LogWarning($"[NetworkedInstanceManager] Host lobby transition queue failed: {e.Message}"); }

                LaunchJoinedChildren();
                QueueHostSplitLayoutIfReady(GetActivePlayerCountForLayout());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] Host start failed: {e.Message}");
            }
        }

        private void TryStartChildClient()
        {
            if (_clientStarted) return;

            var nm = GetNetworkManager();
            if (nm == null)
            {
                if (!_missingNetworkManagerLogged)
                {
                    _missingNetworkManagerLogged = true;
                    Plugin.Log.LogWarning("[NetworkedInstanceManager] Child waiting for FishNet NetworkManager.");
                }
                return;
            }

            if (!ConfigureTugboat(nm, "0.0.0.0", _args.HostAddress, _args.Port))
                return;

            try
            {
                if (!nm.IsClientStarted)
                    _clientStarted = nm.ClientManager.StartConnection(_args.HostAddress, _args.Port);
                else
                    _clientStarted = true;

                Plugin.Log.LogInfo(_clientStarted
                    ? $"[NetworkedInstanceManager] Child slot {_args.Slot} connecting to {_args.HostAddress}:{_args.Port}."
                    : "[NetworkedInstanceManager] Child ClientManager.StartConnection returned false.");

                if (_clientStarted)
                {
                    QueueWindowLayout(Math.Max(_args.PlayerCount, _args.Slot + 1), allowHostMove: true);
                    QueueLobbyTransition();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] Child client start failed: {e.Message}");
            }
        }

        private void LaunchJoinedChildren()
        {
            if (!ModConfig.LaunchChildProcesses.Value) return;
            if (LocalPlayerManager.Instance == null) return;

            foreach (var slot in LocalPlayerManager.Instance.ActiveSlots)
            {
                if (slot.SlotIndex == 0) continue;
                LaunchChild(slot.SlotIndex);
            }
        }

        private bool LaunchChild(int slotIndex)
        {
            if (!ModConfig.LaunchChildProcesses.Value) return false;
            if (_children.TryGetValue(slotIndex, out var existing) && IsProcessAlive(existing)) return true;

            string exe = ResolveGameExecutablePath();
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] Cannot launch child slot {slotIndex}; game exe not found at '{exe}'.");
                return false;
            }

            var device = ResolveDeviceForSlot(slotIndex);
            int playerCount = GetActivePlayerCountForLayout();
            string profile = ResolveProfileForSlot(slotIndex);
            string profiles = BuildProfileMapForLaunch(playerCount);
            string launchToken = "";
            try
            {
                launchToken = NetworkedInstanceLaunchFile.Create(slotIndex, (ushort)ModConfig.NetworkedInstancePort.Value, playerCount, profile, device, profiles);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] Could not create child launch lease for slot {slotIndex}: {e.Message}");
            }

            var psi = new ProcessStartInfo(exe)
            {
                UseShellExecute = false,
                WorkingDirectory = Path.GetDirectoryName(exe) ?? Environment.CurrentDirectory
            };
            psi.ArgumentList.Add("--sledcoop-role=client");
            psi.ArgumentList.Add("--sledcoop-host=127.0.0.1");
            psi.ArgumentList.Add($"--sledcoop-port={ModConfig.NetworkedInstancePort.Value}");
            psi.ArgumentList.Add($"--sledcoop-slot={slotIndex}");
            psi.ArgumentList.Add($"--sledcoop-player-count={playerCount}");
            psi.ArgumentList.Add($"--sledcoop-profile={profile}");
            psi.ArgumentList.Add($"--sledcoop-device={device}");
            if (!string.IsNullOrWhiteSpace(profiles))
                psi.ArgumentList.Add($"--sledcoop-profiles={profiles}");
            psi.ArgumentList.Add("-screen-fullscreen");
            psi.ArgumentList.Add("0");
            NetworkedWindowLayout.TryGetLaunchSize(slotIndex, playerCount, out int launchWidth, out int launchHeight);
            psi.ArgumentList.Add("-screen-width");
            psi.ArgumentList.Add(launchWidth.ToString());
            psi.ArgumentList.Add("-screen-height");
            psi.ArgumentList.Add(launchHeight.ToString());
            psi.ArgumentList.Add("-popupwindow");
            if (!string.IsNullOrWhiteSpace(launchToken))
                psi.ArgumentList.Add($"--sledcoop-launch-token={launchToken}");
            psi.Environment["SLEDCOOP_ROLE"] = "client";
            psi.Environment["SLEDCOOP_HOST"] = "127.0.0.1";
            psi.Environment["SLEDCOOP_PORT"] = ModConfig.NetworkedInstancePort.Value.ToString();
            psi.Environment["SLEDCOOP_SLOT"] = slotIndex.ToString();
            psi.Environment["SLEDCOOP_PLAYER_COUNT"] = playerCount.ToString();
            psi.Environment["SLEDCOOP_PROFILE"] = profile;
            psi.Environment["SLEDCOOP_DEVICE"] = device.ToString();
            if (!string.IsNullOrWhiteSpace(profiles))
                psi.Environment["SLEDCOOP_PROFILES"] = profiles;
            EnsureBepInExDoorstopEnvironment(psi);
            string registryOverride = EnsureWineWinhttpRegistryOverride();
            if (!string.IsNullOrWhiteSpace(launchToken))
                psi.Environment["SLEDCOOP_LAUNCH_TOKEN"] = launchToken;

            try
            {
                var proc = Process.Start(psi);
                if (proc != null)
                {
                    _children[slotIndex] = proc;
                    Plugin.Log.LogInfo(
                        $"[NetworkedInstanceManager] Launched child slot {slotIndex}: pid={proc.Id}, profile={profile}, device={device}, " +
                        $"exe='{exe}', cwd='{psi.WorkingDirectory}', token={launchToken}, " +
                        $"WINEDLLOVERRIDES='{GetEnvForLog(psi, "WINEDLLOVERRIDES")}', " +
                        $"DOORSTOP_DISABLE='{GetEnvForLog(psi, "DOORSTOP_DISABLE")}', " +
                        $"DOORSTOP_INVOKE_DLL_PATH='{GetEnvForLog(psi, "DOORSTOP_INVOKE_DLL_PATH")}', " +
                        $"WineDllOverride.winhttp='{registryOverride}'.");
                    return true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] Failed to launch child slot {slotIndex}: {e.Message}");
            }

            return false;
        }

        private static string ResolveProfileForSlot(int slotIndex)
        {
            var slot = LocalPlayerManager.Instance?.GetSlot(slotIndex);
            string profile = NetworkedGuestProfile.Resolve(slot?.ProfileName);
            if (slot != null)
                slot.ProfileName = profile;
            return profile;
        }

        private static string BuildProfileMapForLaunch(int playerCount)
        {
            var profiles = new List<KeyValuePair<int, string>>();
            for (int i = 1; i < Math.Max(1, Math.Min(4, playerCount)); i++)
            {
                string profile = ResolveProfileForSlot(i);
                if (!string.IsNullOrWhiteSpace(profile))
                    profiles.Add(new KeyValuePair<int, string>(i, profile));
            }

            return NetworkedInstanceArgs.EncodeProfiles(profiles);
        }

        private static void EnsureBepInExDoorstopEnvironment(ProcessStartInfo psi)
        {
            const string key = "WINEDLLOVERRIDES";
            const string required = "winhttp=n,b";

            try
            {
                if (!psi.Environment.TryGetValue(key, out var current) || string.IsNullOrWhiteSpace(current))
                {
                    psi.Environment[key] = required;
                }
                else if (current.IndexOf("winhttp", StringComparison.OrdinalIgnoreCase) < 0)
                {
                    psi.Environment[key] = current.TrimEnd(';') + ";" + required;
                }

                // Doorstop sets this in-process after it has injected BepInEx so child
                // processes do not recursively inject. Our child game instance is
                // intentionally another modded Unity process, so never inherit it.
                psi.Environment.Remove("DOORSTOP_DISABLE");

                string cwd = psi.WorkingDirectory;
                if (!string.IsNullOrWhiteSpace(cwd))
                {
                    string invoke = Path.Combine(cwd, "BepInEx", "core", "BepInEx.Unity.IL2CPP.dll");
                    string managed = Path.Combine(cwd, "Sledding Game_Data", "Managed");
                    string processPath = Path.Combine(cwd, Path.GetFileName(psi.FileName));

                    psi.Environment["DOORSTOP_INVOKE_DLL_PATH"] = invoke;
                    psi.Environment["DOORSTOP_PROCESS_PATH"] = processPath;
                    psi.Environment["DOORSTOP_MANAGED_FOLDER_DIR"] = managed;
                }
            }
            catch { }
        }

        private static string EnsureWineWinhttpRegistryOverride()
        {
            const string keyPath = @"Software\Wine\DllOverrides";
            const string valueName = "winhttp";
            const string required = "native,builtin";

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(keyPath);
                if (key == null) return "unavailable";

                string current = key.GetValue(valueName) as string ?? "";
                if (IsNativeFirstDllOverride(current))
                    return current;

                key.SetValue(valueName, required, RegistryValueKind.String);
                return required;
            }
            catch (Exception e)
            {
                return $"failed:{e.GetType().Name}";
            }
        }

        private static bool IsNativeFirstDllOverride(string value)
        {
            if (string.IsNullOrWhiteSpace(value)) return false;
            string first = value.Split(',')[0].Trim();
            return first.Equals("native", StringComparison.OrdinalIgnoreCase)
                || first.Equals("n", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetEnvForLog(ProcessStartInfo psi, string key)
        {
            try
            {
                return psi.Environment.TryGetValue(key, out var value) ? value ?? "" : "";
            }
            catch { return ""; }
        }

        private void ReapChildren()
        {
            foreach (var pair in new List<KeyValuePair<int, Process>>(_children))
            {
                try
                {
                    if (IsProcessAlive(pair.Value)) continue;
                    Plugin.Log.LogInfo($"[NetworkedInstanceManager] Child slot {pair.Key} exited.");
                    _children.Remove(pair.Key);
                }
                catch
                {
                    _children.Remove(pair.Key);
                }
            }
        }

        private void QueueWindowLayout(int playerCount, bool allowHostMove)
        {
            if (!IsNetworkedModeConfigured) return;
            if (!_args.IsClient && !_hostStarted && playerCount > 1)
            {
                Plugin.Log.LogInfo($"[NetworkedWindowLayout] Host layout deferred until networked start; requested players={playerCount}.");
                return;
            }

            _windowLayoutPending = true;
            _windowLayoutAllowHostMove = allowHostMove;
            _windowLayoutPlayerCount = Math.Max(1, Math.Min(4, playerCount));
            _nextWindowLayoutFrame = 0;
            _windowLayoutDeadlineFrame = UnityEngine.Time.frameCount + 600;
        }

        private void QueueHostSplitLayoutIfReady(int playerCount)
        {
            if (_args.IsClient || !_hostStarted)
                return;

            playerCount = Math.Max(1, Math.Min(4, playerCount));
            if (playerCount <= 1)
                return;

            if (_children.Count <= 0)
            {
                Plugin.Log.LogInfo($"[NetworkedWindowLayout] Host split layout waiting for child process; players={playerCount}.");
                return;
            }

            try
            {
                QueueWindowLayout(playerCount, allowHostMove: !HasDedicatedMonitorsForAllPlayers(playerCount));
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] Host window layout queue failed: {e.Message}");
            }
        }

        private void ProcessPendingWindowLayout()
        {
            if (!_windowLayoutPending) return;
            if (UnityEngine.Time.frameCount < _nextWindowLayoutFrame) return;

            int slot = _args?.Slot ?? 0;
            bool applied = NetworkedWindowLayout.TryApplyToCurrentProcess(slot, _windowLayoutPlayerCount, _windowLayoutAllowHostMove);
            if (UnityEngine.Time.frameCount >= _windowLayoutDeadlineFrame)
            {
                _windowLayoutPending = false;
                return;
            }

            if (applied && _windowLayoutPlayerCount <= 1)
            {
                _windowLayoutPending = false;
                return;
            }

            // Unity can restore the Steam-launched host window after an early
            // resolution change. Keep reasserting the bounds briefly for all
            // networked multi-instance slots, especially slot 0.
            _nextWindowLayoutFrame = UnityEngine.Time.frameCount + (applied ? 60 : 30);
        }

        private static int GetActivePlayerCountForLayout()
        {
            return Math.Max(1, Math.Min(4, LocalPlayerManager.Instance?.ActiveCount ?? 1));
        }

        private static bool HasDedicatedMonitorsForAllPlayers(int playerCount)
        {
            if (!ModConfig.MultiDisplayEnabled.Value) return false;
            try { return NetworkedWindowLayout.GetAvailableMonitorCount() >= Math.Max(1, playerCount); }
            catch { return false; }
        }

        private static bool IsProcessAlive(Process process)
        {
            try
            {
                return !process.HasExited;
            }
            catch { return true; }
        }

        private static bool TryKillProcess(Process process)
        {
            try
            {
                if (!IsProcessAlive(process))
                    return true;

                process.Kill(entireProcessTree: true);
                return true;
            }
            catch
            {
                try
                {
                    if (!IsProcessAlive(process))
                        return true;

                    process.Kill();
                    return true;
                }
                catch { return false; }
            }
        }

        private static NetworkManager? GetNetworkManager()
        {
            return NetworkManagerFinder.Find();
        }

        private static bool ConfigureTugboat(NetworkManager nm, string bindAddress, string clientAddress, int port)
        {
            try
            {
                var tm = nm.TransportManager;
                if (tm == null) return false;

                var tugboat = tm.Transport as Tugboat ?? nm.gameObject.GetComponent<Tugboat>();
                if (tugboat == null)
                    tugboat = nm.gameObject.AddComponent<Tugboat>();

                tugboat.enabled = true;
                tugboat.SetServerBindAddress(bindAddress, IPAddressType.IPv4);
                tugboat.SetClientAddress(clientAddress);
                tugboat.SetPort((ushort)Math.Max(1, Math.Min(ushort.MaxValue, port)));
                tugboat.SetMaximumClients(Math.Max(1, ModConfig.MaxLocalPlayers.Value));

                if (tm.Transport != tugboat)
                {
                    var old = tm.Transport;
                    SetFishNetTransportSubscriptions(nm, false);
                    tm.Transport = tugboat;
                    ReinitializeTransportManager(nm);
                    SetFishNetTransportSubscriptions(nm, true);
                    if (old != null && old != tugboat)
                        old.enabled = false;
                    Plugin.Log.LogInfo($"[NetworkedInstanceManager] Transport set to Tugboat (was {old?.GetType().FullName ?? "null"}).");
                }

                tugboat.Initialize(nm, 0);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] ConfigureTugboat failed: {e.Message}");
                return false;
            }
        }

        private static void ReinitializeTransportManager(NetworkManager nm)
        {
            var tm = nm.TransportManager;
            if (tm == null) return;
            var method = tm.GetType().GetMethod("InitializeOnce_Internal", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            method?.Invoke(tm, new object[] { nm });
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
                var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                method?.Invoke(instance, new object[] { value });
            }
            catch { }
        }

        private void QueueLobbyTransition()
        {
            _lobbyBridgePending = true;
            _nextLobbyBridgeFrame = 0;
        }

        private void ProcessPendingLobbyBridge()
        {
            if (!_lobbyBridgePending) return;
            if (UnityEngine.Time.frameCount < _nextLobbyBridgeFrame) return;

            if (CompleteLobbyTransition())
            {
                _lobbyBridgePending = false;
                return;
            }

            _nextLobbyBridgeFrame = UnityEngine.Time.frameCount + 120;
        }

        private static bool CompleteLobbyTransition()
        {
            try
            {
                var lobbyType = Patches.PatchHelpers.SafeTypeByName("_Scripts.Managers.LobbyManager");
                if (lobbyType == null) return false;
                var instance = lobbyType?.GetProperty("Instance", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (instance == null) return false;
                var wait = lobbyType?.GetMethod("WaitForConnection", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (wait == null) return false;
                wait.Invoke(instance, null);
                Plugin.Log.LogInfo("[NetworkedInstanceManager] Invoked LobbyManager.WaitForConnection().");
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedInstanceManager] Lobby transition bridge failed: {e.Message}");
                return false;
            }
        }

        private static AssignedInputDevice ResolveDeviceForSlot(int slotIndex)
        {
            var slot = LocalPlayerManager.Instance?.GetSlot(slotIndex);
            return slot?.LocalInputProvider?.Device ?? (AssignedInputDevice)((int)AssignedInputDevice.Gamepad0 + Math.Max(0, slotIndex - 1));
        }

        private static string ResolveGameExecutablePath()
        {
            foreach (string candidate in EnumerateExecutableCandidates())
            {
                try
                {
                    if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                        return candidate;
                }
                catch { }
            }

            return EnumerateExecutableCandidates().FirstOrDefault(p => !string.IsNullOrWhiteSpace(p)) ?? "";
        }

        private static IEnumerable<string> EnumerateExecutableCandidates()
        {
            var candidates = new List<string>();
            string exeName = "Sledding Game Demo.exe";

            string? mainModule = null;
            try { mainModule = Process.GetCurrentProcess().MainModule?.FileName; } catch { }
            if (!string.IsNullOrWhiteSpace(mainModule))
                candidates.Add(mainModule);

            try
            {
                var dir = Directory.GetParent(Application.dataPath);
                if (dir != null)
                    candidates.Add(Path.Combine(dir.FullName, exeName));
            }
            catch { }

            try
            {
                candidates.Add(Path.Combine(Environment.CurrentDirectory, exeName));
            }
            catch { }

            string? home = null;
            try { home = Environment.GetEnvironmentVariable("HOME"); } catch { }
            if (!string.IsNullOrWhiteSpace(home))
            {
                string[] linuxCandidates =
                {
                    Path.Combine(home, ".steam", "debian-installation", "steamapps", "common", "Sledding Game Demo", exeName),
                    Path.Combine(home, ".steam", "steam", "steamapps", "common", "Sledding Game Demo", exeName),
                    Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Sledding Game Demo", exeName),
                    Path.Combine(home, ".steam", "debian-installation", "steamapps", "common", "Sledding Game", exeName),
                    Path.Combine(home, ".steam", "steam", "steamapps", "common", "Sledding Game", exeName),
                    Path.Combine(home, ".local", "share", "Steam", "steamapps", "common", "Sledding Game", exeName),
                };

                foreach (string candidate in linuxCandidates)
                {
                    candidates.Add(candidate);
                    candidates.Add(ToWineZPath(candidate));
                }
            }

            return candidates;
        }

        private static string ToWineZPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return path;
            string normalized = path.Replace('/', '\\');
            if (normalized.StartsWith("\\"))
                return "Z:" + normalized;
            return normalized;
        }
    }
}
