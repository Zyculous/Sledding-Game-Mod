using System;
using Rewired;
using UnityEngine;

namespace SledCoopMod
{
    public class NetworkedRewiredIsolationManager : MonoBehaviour
    {
        private bool _loggedReady;
        private string _lastSummary = "";
        private string _lastControllerInventory = "";
        private int _nextInventoryLogFrame;
        private bool _autoAssignDisabled;

        private void Update()
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return;

            // Disable Rewired auto-assign on the HOST only. The child has
            // exactly one gamepad and depends on Rewired's auto-assign to
            // load the default action-element maps for it (without that,
            // buttons fire no actions even though stick axes still poll).
            if (!_autoAssignDisabled
                && NetworkedInstanceManager.Instance?.IsChildClient != true)
            {
                _autoAssignDisabled = TryDisableAutoAssign();
            }

            ApplyIsolation();
        }

        private static bool TryDisableAutoAssign()
        {
            try
            {
                if (!ReInput.isReady) return false;

                // Rewired exposes this as a configuration property; setting
                // it to false stops Rewired from auto-claiming any new or
                // re-detected joystick into the first available Player.
                var cfg = ReInput.configuration;
                if (cfg == null) return false;

                var prop = cfg.GetType().GetProperty(
                    "autoAssignJoysticks",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(cfg, false);
                    Plugin.Log.LogInfo("[NetworkedRewiredIsolation] Rewired autoAssignJoysticks disabled — manual binding only.");
                    return true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[NetworkedRewiredIsolation] disable auto-assign failed: {e.Message}");
            }
            return false;
        }

        private void ApplyIsolation()
        {
            try
            {
                if (!ReInput.isReady)
                    return;

                var localPlayer = ReInput.players.GetPlayer(0);
                if (localPlayer == null)
                    return;

                bool isChild = NetworkedInstanceManager.Instance?.IsChildClient == true;
                if (isChild)
                    ApplyChildIsolation(localPlayer);
                else
                    ApplyHostIsolation(localPlayer);

                // Action queries (Player.GetButton / GetButtonDown / GetAxis)
                // only return real values when isPlaying=true. Make sure
                // Player 0 is on for both host and child — without this flag,
                // raw Joystick axis polling still works (so stick movement
                // appears functional) but every button-driven Rewired Action
                // returns false.
                EnsurePlayerIsPlaying(localPlayer);

                // Host: strip non-local Rewired Players so the host's gamepad
                // only feeds Player 0 (prevents one controller driving four
                // characters when multiple Rewired Players auto-claim it).
                //
                // Child: the *opposite* — populate the bound joystick on
                // *every* Rewired Player so the local game-side PlayerControl
                // (which on the child instance reads from a non-zero Rewired
                // Player ID for slot routing) gets full action input. Without
                // this, axis polling works (game uses raw Joystick.GetAxis)
                // but Rewired-action button events fire on a Player that has
                // no map loaded for the gamepad.
                if (isChild)
                    PopulateBoundJoystickOnAllPlayers();
                else
                    StripNonLocalPlayers();

                // Diagnostic snapshot every ~5s so we can see what each
                // Rewired Player ended up with — invaluable when controllers
                // appear "dead" in-game.
                if (Time.frameCount >= _nextInventoryLogFrame)
                {
                    _nextInventoryLogFrame = Time.frameCount + 300;
                    LogControllerInventory();
                }

                // Per-frame button-state probe: when *any* joystick button is
                // currently held, log raw element state and the corresponding
                // Rewired action state. Tells us whether the input reaches
                // Rewired at all (raw element) and whether Rewired actions
                // fire (Player.GetButton). One log per state-change so it's
                // not noisy on idle frames.
                ProbeJoystickButtonState(localPlayer);
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[NetworkedRewiredIsolation] Apply skipped: {e.GetType().Name}: {e.Message}");
            }
        }

        private void ApplyHostIsolation(Player player)
        {
            string slot0Id = ControllerBindings.GetBinding(0);

            // 0) Slot 0 explicitly bound to Keyboard+Mouse — give Player 0
            //    only KB+M, strip every joystick so gamepad buttons can't
            //    leak through to the host character.
            if (ControllerBindings.IsKeyboardMouse(slot0Id))
            {
                SafeAdd(player, ControllerType.Keyboard, 0);
                SafeAdd(player, ControllerType.Mouse, 0);
                for (int i = 0; i <= 3; i++)
                    SafeRemove(player, ControllerType.Joystick, i);

                LogSummary("host: slot 0 = Keyboard+Mouse, all joysticks stripped");
                return;
            }

            // 1) Honor an explicit Slot 0 binding from the in-game bind UI.
            //    If the user said "slot 0 = this gamepad", make sure Player 0
            //    has it and remove every other joystick. This guarantees the
            //    host's controller routes input even when child slots claimed
            //    other gamepads.
            int slot0Bound = ControllerBindings.ResolveBoundJoystickIndex(0);
            if (slot0Bound >= 0)
            {
                // Bound a joystick to slot 0 — strip KB/Mouse too so they
                // don't double-drive the host character.
                SafeRemove(player, ControllerType.Keyboard, 0);
                SafeRemove(player, ControllerType.Mouse, 0);
                for (int i = 0; i <= 3; i++)
                {
                    if (i == slot0Bound)
                        SafeAdd(player, ControllerType.Joystick, i);
                    else
                        SafeRemove(player, ControllerType.Joystick, i);
                }

                LogSummary($"host: gamepad {slot0Bound} bound to slot 0, KB/Mouse stripped");
            }
            else
            {
                // 2) No explicit binding — fall back to "remove gamepads
                //    claimed by child slots" but explicitly add the first
                //    *unclaimed* gamepad to host's Player 0 so the host has
                //    something to drive their character with. Without this
                //    add, Rewired's auto-assign sometimes leaves Player 0
                //    with zero joysticks after we strip the claimed ones.
                int firstUnclaimed = -1;
                for (int i = 0; i <= 3; i++)
                {
                    if (IsGamepadClaimedByChild(i))
                        SafeRemove(player, ControllerType.Joystick, i);
                    else if (firstUnclaimed < 0 && JoystickPresent(i))
                        firstUnclaimed = i;
                }

                if (firstUnclaimed >= 0)
                    SafeAdd(player, ControllerType.Joystick, firstUnclaimed);

                LogSummary($"host: keyboard/mouse kept, child gamepads removed, host gamepad={firstUnclaimed}");
            }
        }

        private static bool JoystickPresent(int index)
        {
            try
            {
                int count = ReInput.controllers.joystickCount;
                if (index < 0 || index >= count) return false;
                return ReInput.controllers.Joysticks[index] != null;
            }
            catch { return false; }
        }

        private void ApplyChildIsolation(Player player)
        {
            int assigned = GetAssignedGamepadIndex(NetworkedInstanceManager.Instance?.AssignedDevice ?? AssignedInputDevice.None);
            if (assigned < 0)
                return;

            SafeRemove(player, ControllerType.Keyboard, 0);
            SafeRemove(player, ControllerType.Mouse, 0);

            for (int i = 0; i <= 3; i++)
            {
                if (i == assigned)
                    SafeAdd(player, ControllerType.Joystick, i);
                else
                    SafeRemove(player, ControllerType.Joystick, i);
            }

            LogSummary($"child slot {NetworkedInstanceManager.Instance?.SlotIndex ?? -1}: gamepad {assigned} kept, keyboard/mouse removed");
        }

        // For the child instance: ensure every Rewired Player has the
        // assigned joystick attached, with default + per-layout maps loaded
        // AND isPlaying=true so action queries (Player.GetButton) actually
        // return values. Without isPlaying=true, axis polling via direct
        // Joystick.GetAxis still works (which is why movement-stick worked)
        // but Rewired Player.GetButton/GetButtonDown fires nothing — the
        // exact symptom that left P2 unable to jump/sled/interact.
        private static void PopulateBoundJoystickOnAllPlayers()
        {
            int assigned = GetAssignedGamepadIndex(NetworkedInstanceManager.Instance?.AssignedDevice ?? AssignedInputDevice.None);
            if (assigned < 0) return;

            int playerCount = SafeGetPlayerCount();
            for (int p = 0; p < playerCount; p++)
            {
                Player? other = null;
                try { other = ReInput.players.GetPlayer(p); }
                catch { continue; }
                if (other == null) continue;

                // Strip KB/M from every Player on the child (the child window
                // shouldn't receive keyboard/mouse — host owns those).
                SafeRemove(other, ControllerType.Keyboard, 0);
                SafeRemove(other, ControllerType.Mouse, 0);

                // Add the assigned gamepad. SafeAdd internally calls
                // LoadDefaultMaps + LoadMap + SetAllMapsEnabled so each
                // Player gets working button bindings.
                SafeAdd(other, ControllerType.Joystick, assigned);

                // The crucial flip: turn the Player on so action queries
                // route through the loaded maps.
                EnsurePlayerIsPlaying(other);
            }

            if (!_populateAllPlayersLogged)
            {
                _populateAllPlayersLogged = true;
                Plugin.Log.LogInfo($"[NetworkedRewiredIsolation] Populated gamepad {assigned} on all {playerCount} Rewired Players (child instance).");
            }
        }

        private static bool _populateAllPlayersLogged;
        private static bool _isPlayingLogged;
        private static bool _isPlayingErrLogged;
        private static string _lastButtonProbeSnapshot = "";
        private static int _lastActionProbeFrame;

        // Reads UnityEngine.InputSystem.Gamepad.current via reflection
        // (Unity 6 has the package; Il2CppInterop may not surface a clean
        // direct-call binding). Returns a short snapshot string showing
        // left-stick, right-stick, and any held button, so the probe
        // output makes the Rewired-vs-InputSystem split visible.
        private static void AppendUnityInputSystemGamepadSnapshot(System.Text.StringBuilder sb)
        {
            try
            {
                var gamepadType = Type.GetType("UnityEngine.InputSystem.Gamepad, Unity.InputSystem");
                if (gamepadType == null) { sb.Append("(no-IS)"); return; }

                var current = gamepadType.GetProperty("current",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static)?.GetValue(null);
                if (current == null) { sb.Append("(noPad)"); return; }

                bool wrote = false;
                var leftStick = current.GetType().GetProperty("leftStick",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(current);
                if (leftStick != null)
                {
                    var read = leftStick.GetType().GetMethod("ReadValue", Type.EmptyTypes);
                    var v = read?.Invoke(leftStick, null);
                    if (v is Vector2 vec && vec.sqrMagnitude > 0.25f)
                    {
                        sb.Append("LS=").Append(vec.x.ToString("0.0")).Append(',').Append(vec.y.ToString("0.0")).Append(' ');
                        wrote = true;
                    }
                }
                var rightStick = current.GetType().GetProperty("rightStick",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(current);
                if (rightStick != null)
                {
                    var read = rightStick.GetType().GetMethod("ReadValue", Type.EmptyTypes);
                    var v = read?.Invoke(rightStick, null);
                    if (v is Vector2 vec && vec.sqrMagnitude > 0.25f)
                    {
                        sb.Append("RS=").Append(vec.x.ToString("0.0")).Append(',').Append(vec.y.ToString("0.0")).Append(' ');
                        wrote = true;
                    }
                }

                // Probe a few common buttons by property name.
                foreach (string name in new[] { "buttonSouth", "buttonEast", "buttonWest", "buttonNorth", "leftShoulder", "rightShoulder", "leftTrigger", "rightTrigger", "startButton", "selectButton", "leftStickButton", "rightStickButton" })
                {
                    var control = current.GetType().GetProperty(name,
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(current);
                    if (control == null) continue;
                    var pressed = control.GetType().GetProperty("isPressed",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance)?.GetValue(control);
                    if (pressed is bool b && b) { sb.Append(name).Append(' '); wrote = true; }
                }

                if (!wrote) sb.Append("(idle)");
            }
            catch (Exception e)
            {
                sb.Append("(err:").Append(e.GetType().Name).Append(')');
            }
        }
        private static readonly string[] _probedActionNames =
        {
            "Jump", "Brake", "Snowball", "PlaceMarker", "Interact",
            "Sled", "GetUp", "Crouch", "Pause", "Boost", "Camera",
            "CameraHorizontal", "CameraVertical", "Horizontal", "Vertical",
            "Hand Action", "HandAction", "Throw", "Punch", "Hit",
            "TeleportMarker", "Build", "Inventory", "Sprint",
            "UISubmit", "UICancel", "Submit", "Cancel",
        };

        // Reads raw joystick buttons + a basket of common Rewired actions
        // and logs only when the on/off pattern changes. The point is to
        // determine, definitively, where button input is breaking:
        //   * raw button line shows pressed elements → Rewired is seeing
        //     the gamepad input → problem is action-mapping or game-side.
        //   * raw line stays empty → the gamepad isn't delivering button
        //     events to Rewired at all (Wine/SDL mapping issue).
        //   * raw line populates AND action line populates → Rewired is
        //     firing actions, so the issue is downstream (game polls a
        //     different Player ID or has ownership/netcode block).
        private static void ProbeJoystickButtonState(Player player)
        {
            try
            {
                if (!ReInput.isReady) return;

                // Find the player's first attached joystick.
                Joystick? joy = null;
                int joyCount = 0;
                try { joyCount = ReInput.controllers.joystickCount; } catch { }
                for (int i = 0; i < joyCount; i++)
                {
                    Joystick? candidate = null;
                    try { candidate = ReInput.controllers.Joysticks[i]; } catch { }
                    if (candidate == null) continue;
                    if (player.controllers.ContainsController(ControllerType.Joystick, candidate.id))
                    {
                        joy = candidate;
                        break;
                    }
                }

                var sb = new System.Text.StringBuilder();

                // Raw button state from the joystick itself — what Rewired
                // is actually receiving from the OS.
                if (joy != null)
                {
                    int btnCount = 0;
                    try { btnCount = joy.buttonCount; } catch { }
                    sb.Append("rawBtns=");
                    bool anyDown = false;
                    for (int b = 0; b < btnCount; b++)
                    {
                        bool down = false;
                        try { down = joy.GetButton(b); } catch { }
                        if (down) { sb.Append(b).Append(','); anyDown = true; }
                    }
                    if (!anyDown) sb.Append("(none)");
                    sb.Append(" rawAxes=");
                    bool anyAxis = false;
                    int axisCount = 0;
                    try { axisCount = joy.axisCount; } catch { }
                    for (int a = 0; a < axisCount; a++)
                    {
                        float v = 0f;
                        try { v = joy.GetAxis(a); } catch { }
                        if (Math.Abs(v) > 0.5f)
                        {
                            sb.Append(a).Append('=').Append(v.ToString("0.0")).Append(',');
                            anyAxis = true;
                        }
                    }
                    if (!anyAxis) sb.Append("(none)");
                }
                else
                {
                    sb.Append("noJoyAttached");
                }

                // Rewired action state — does Player.GetButton fire?
                sb.Append(" actions=");
                bool anyAction = false;
                foreach (var actionName in _probedActionNames)
                {
                    bool down = false;
                    try { down = player.GetButton(actionName); } catch { }
                    if (down)
                    {
                        sb.Append(actionName).Append(',');
                        anyAction = true;
                    }
                }
                if (!anyAction) sb.Append("(none)");

                // Also poll Unity InputSystem's gamepad directly. If the
                // user reports stick movement working but Rewired sees
                // nothing, Unity InputSystem is the parallel path that's
                // delivering the input — proving Rewired's pipeline is
                // dead in the child window.
                sb.Append(" unityIS=");
                AppendUnityInputSystemGamepadSnapshot(sb);

                string snapshot = sb.ToString();
                bool stateChanged = snapshot != _lastButtonProbeSnapshot;

                // Always log every ~3s so we know the probe IS running, plus
                // immediate logs on every state change while throttled to
                // ~10 Hz. Without the heartbeat we can't tell whether
                // "no log" means "no input" or "probe not running".
                bool heartbeatDue = Time.frameCount - _lastActionProbeFrame >= 180;
                if (!stateChanged && !heartbeatDue) return;
                if (Time.frameCount - _lastActionProbeFrame < 6) return;

                _lastButtonProbeSnapshot = snapshot;
                _lastActionProbeFrame = Time.frameCount;
                Plugin.Log.LogInfo($"[NetworkedRewiredIsolation] probe: {snapshot}");
            }
            catch { }
        }

        // Reflectively set Player.isPlaying = true. We use reflection rather
        // than a direct property write because the wrapper-generated property
        // setter can vary across Rewired+Il2CppInterop builds — reflection
        // tolerates both `set_isPlaying(bool)` and a plain field.
        private static void EnsurePlayerIsPlaying(Player player)
        {
            try
            {
                var t = player.GetType();
                var prop = t.GetProperty(
                    "isPlaying",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (prop != null && prop.CanWrite)
                {
                    object? current = null;
                    try { current = prop.GetValue(player); } catch { }
                    if (current is bool b && b) return; // already true

                    prop.SetValue(player, true);
                    if (!_isPlayingLogged)
                    {
                        _isPlayingLogged = true;
                        Plugin.Log.LogInfo("[NetworkedRewiredIsolation] Player.isPlaying = true (action queries will now route through loaded maps).");
                    }
                    return;
                }

                // Fallback: try the field name.
                var f = t.GetField(
                    "isPlaying",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                if (f != null && f.FieldType == typeof(bool))
                {
                    f.SetValue(player, true);
                    if (!_isPlayingLogged)
                    {
                        _isPlayingLogged = true;
                        Plugin.Log.LogInfo("[NetworkedRewiredIsolation] Player.isPlaying field set to true.");
                    }
                }
            }
            catch (Exception e)
            {
                if (!_isPlayingErrLogged)
                {
                    _isPlayingErrLogged = true;
                    Plugin.Log.LogWarning($"[NetworkedRewiredIsolation] EnsurePlayerIsPlaying threw: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        // Remove every controller from every non-local Rewired Player so
        // their action-element-maps can't fire while the user holds a
        // controller intended for the canonical local Player 0.
        private static void StripNonLocalPlayers()
        {
            int playerCount = SafeGetPlayerCount();
            if (playerCount <= 1)
                return;

            for (int id = 1; id < playerCount; id++)
            {
                Player? other = null;
                try { other = ReInput.players.GetPlayer(id); }
                catch { continue; }

                if (other == null) continue;

                StripAllControllers(other);
            }
        }

        private static int SafeGetPlayerCount()
        {
            try { return ReInput.players.playerCount; }
            catch { return 0; }
        }

        private static void StripAllControllers(Player player)
        {
            // Defensive: each controller type's id range is finite. We blast
            // through 0..3 for joysticks and 0 for keyboard/mouse since
            // Sledding Game's UserData never provisions more than that.
            SafeRemove(player, ControllerType.Keyboard, 0);
            SafeRemove(player, ControllerType.Mouse, 0);
            for (int i = 0; i <= 3; i++)
                SafeRemove(player, ControllerType.Joystick, i);
        }

        private static bool IsGamepadClaimedByChild(int index)
        {
            if (LocalPlayerManager.Instance == null)
                return false;

            foreach (var slot in LocalPlayerManager.Instance.ActiveSlots)
            {
                if (slot.SlotIndex <= 0 || slot.LocalInputProvider == null)
                    continue;

                if (GetAssignedGamepadIndex(slot.LocalInputProvider.Device) == index)
                    return true;
            }

            return false;
        }

        private static int GetAssignedGamepadIndex(AssignedInputDevice device)
        {
            if (device < AssignedInputDevice.Gamepad0 || device > AssignedInputDevice.Gamepad3)
                return -1;
            return (int)device - (int)AssignedInputDevice.Gamepad0;
        }

        private static void SafeAdd(Player player, ControllerType type, int id)
        {
            try
            {
                if (!player.controllers.ContainsController(type, id))
                    player.controllers.AddController(type, id, false);
            }
            catch { }

            LoadDefaultMapsRobust(player, type, id);

            // Crucial follow-up: LoadDefaultMaps(ControllerType) on this
            // Rewired build only loads the player-level default. The
            // per-joystick layout map (which contains the button-to-action
            // bindings for this specific controller template) needs an
            // explicit LoadMap with controller id + category 0 + layout 0,
            // and any pre-existing maps need to be enabled in case the
            // host process disabled them.
            LoadPerControllerLayoutMap(player, type, id);
            EnableAllMapsForController(player, type);
        }

        private static void LoadPerControllerLayoutMap(Player player, ControllerType type, int id)
        {
            try
            {
                var maps = player.controllers.maps;
                if (maps == null) return;
                var t = maps.GetType();

                // Void LoadMap(ControllerType, int controllerId, int categoryId, int layoutId)
                var m = t.GetMethod(
                    "LoadMap",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null,
                    new[] { typeof(ControllerType), typeof(int), typeof(int), typeof(int) },
                    null);
                if (m != null)
                {
                    m.Invoke(maps, new object[] { type, id, 0, 0 });
                    if (!_loadMapLogged)
                    {
                        _loadMapLogged = true;
                        Plugin.Log.LogInfo($"[NetworkedRewiredIsolation] LoadMap({type}, {id}, 0, 0) ok.");
                    }
                }
            }
            catch (Exception e)
            {
                if (!_loadMapErrLogged)
                {
                    _loadMapErrLogged = true;
                    Plugin.Log.LogWarning($"[NetworkedRewiredIsolation] LoadMap threw: {e.GetType().Name}: {e.Message}");
                }
            }
        }

        private static void EnableAllMapsForController(Player player, ControllerType type)
        {
            try
            {
                var maps = player.controllers.maps;
                if (maps == null) return;
                var t = maps.GetType();

                // Int32 SetAllMapsEnabled(Boolean state, ControllerType controllerType)
                var m = t.GetMethod(
                    "SetAllMapsEnabled",
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                    null,
                    new[] { typeof(bool), typeof(ControllerType) },
                    null);
                if (m != null)
                {
                    m.Invoke(maps, new object[] { true, type });
                    if (!_enableMapsLogged)
                    {
                        _enableMapsLogged = true;
                        Plugin.Log.LogInfo($"[NetworkedRewiredIsolation] SetAllMapsEnabled(true, {type}) ok.");
                    }
                }
            }
            catch { }
        }

        private static bool _loadMapLogged;
        private static bool _loadMapErrLogged;
        private static bool _enableMapsLogged;

        // Rewired's LoadDefaultMaps overloads vary by version: some take
        // (ControllerType, int), some (ControllerType, int, bool), some
        // (Controller), some no-args. Reflectively try every overload by
        // name and parameter shape until one accepts the call. Critical
        // because without maps loaded the player has the controller for
        // axis polling but every button-driven Rewired Action fires nothing.
        private static void LoadDefaultMapsRobust(Player player, ControllerType type, int id)
        {
            // First time through: dump every method on player.controllers.maps
            // so we know what's actually available in this Rewired version.
            DumpMapsApiOnce(player);

            try
            {
                var maps = player.controllers.maps;
                if (maps == null) return;

                var mapsType = maps.GetType();
                var methods = mapsType.GetMethods(
                    System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);

                foreach (var m in methods)
                {
                    if (m.Name != "LoadDefaultMaps") continue;
                    var ps = m.GetParameters();

                    try
                    {
                        // (ControllerType) — the only overload Rewired
                        // exposes on this build (per API dump). Loads
                        // default maps for every controller of that type
                        // attached to the player.
                        if (ps.Length == 1
                            && ps[0].ParameterType == typeof(ControllerType))
                        {
                            m.Invoke(maps, new object[] { type });
                            if (!_loadDefaultMapsLogged)
                            {
                                _loadDefaultMapsLogged = true;
                                Plugin.Log.LogInfo($"[NetworkedRewiredIsolation] LoadDefaultMaps({type}) ok via 1-arg overload.");
                            }
                            return;
                        }

                        // (ControllerType, int)
                        if (ps.Length == 2
                            && ps[0].ParameterType == typeof(ControllerType)
                            && ps[1].ParameterType == typeof(int))
                        {
                            m.Invoke(maps, new object[] { type, id });
                            if (!_loadDefaultMapsLogged)
                            {
                                _loadDefaultMapsLogged = true;
                                Plugin.Log.LogInfo($"[NetworkedRewiredIsolation] LoadDefaultMaps({type}, {id}) ok via 2-arg overload.");
                            }
                            return;
                        }

                        // (ControllerType, int, bool)
                        if (ps.Length == 3
                            && ps[0].ParameterType == typeof(ControllerType)
                            && ps[1].ParameterType == typeof(int)
                            && ps[2].ParameterType == typeof(bool))
                        {
                            m.Invoke(maps, new object[] { type, id, true });
                            if (!_loadDefaultMapsLogged)
                            {
                                _loadDefaultMapsLogged = true;
                                Plugin.Log.LogInfo($"[NetworkedRewiredIsolation] LoadDefaultMaps({type}, {id}, true) ok via 3-arg overload.");
                            }
                            return;
                        }

                        // (ControllerType, int, int /* categoryId */, bool)
                        if (ps.Length == 4
                            && ps[0].ParameterType == typeof(ControllerType)
                            && ps[1].ParameterType == typeof(int)
                            && ps[2].ParameterType == typeof(int)
                            && ps[3].ParameterType == typeof(bool))
                        {
                            m.Invoke(maps, new object[] { type, id, 0, true });
                            if (!_loadDefaultMapsLogged)
                            {
                                _loadDefaultMapsLogged = true;
                                Plugin.Log.LogInfo($"[NetworkedRewiredIsolation] LoadDefaultMaps({type}, {id}, 0, true) ok via 4-arg overload.");
                            }
                            return;
                        }
                    }
                    catch (Exception e)
                    {
                        if (!_loadDefaultMapsErrLogged)
                        {
                            _loadDefaultMapsErrLogged = true;
                            Plugin.Log.LogWarning($"[NetworkedRewiredIsolation] LoadDefaultMaps overload threw: {e.GetType().Name}: {e.Message}");
                        }
                    }
                }

                // Fallbacks: no-arg / type-only / per-joystick
                foreach (var m in methods)
                {
                    if (m.Name != "LoadDefaultMaps") continue;
                    var ps = m.GetParameters();
                    if (ps.Length == 0)
                    {
                        try { m.Invoke(maps, null); return; } catch { }
                    }
                }

                if (type == ControllerType.Joystick)
                {
                    var byJoy = mapsType.GetMethod("LoadDefaultMapsForJoystick",
                        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance,
                        null, new[] { typeof(int) }, null);
                    if (byJoy != null)
                    {
                        try { byJoy.Invoke(maps, new object[] { id }); return; } catch { }
                    }
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[NetworkedRewiredIsolation] LoadDefaultMapsRobust outer error: {e.Message}");
            }
        }

        private static bool _loadDefaultMapsLogged;
        private static bool _loadDefaultMapsErrLogged;
        private static bool _mapsApiDumped;

        private static void DumpMapsApiOnce(Player player)
        {
            if (_mapsApiDumped) return;
            _mapsApiDumped = true;
            try
            {
                var maps = player.controllers.maps;
                if (maps == null) return;
                var t = maps.GetType();
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[NetworkedRewiredIsolation] ControllerMap API dump ({t.FullName}):");
                foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    string n = m.Name;
                    if (!n.Contains("Map") && !n.Contains("Default") && !n.Contains("Add")) continue;
                    var ps = m.GetParameters();
                    sb.Append($"  {m.ReturnType.Name} {n}(");
                    for (int i = 0; i < ps.Length; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append(ps[i].ParameterType.Name).Append(' ').Append(ps[i].Name);
                    }
                    sb.AppendLine(")");
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch { }
        }

        private static void SafeRemove(Player player, ControllerType type, int id)
        {
            try
            {
                if (player.controllers.ContainsController(type, id))
                    player.controllers.RemoveController(type, id);
            }
            catch { }
        }

        private void LogControllerInventory()
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                int playerCount = SafeGetPlayerCount();
                for (int p = 0; p < playerCount; p++)
                {
                    Player? pl = null;
                    try { pl = ReInput.players.GetPlayer(p); } catch { }
                    if (pl == null) continue;

                    bool playing = false;
                    try
                    {
                        var prop = pl.GetType().GetProperty("isPlaying",
                            System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                        if (prop != null && prop.GetValue(pl) is bool b) playing = b;
                    }
                    catch { }

                    sb.Append($"P{p}{(playing ? "(playing)" : "(STOPPED)")}=[");
                    bool first = true;
                    try
                    {
                        if (pl.controllers.ContainsController(ControllerType.Keyboard, 0))
                        {
                            sb.Append("Keyboard#0");
                            first = false;
                        }
                        if (pl.controllers.ContainsController(ControllerType.Mouse, 0))
                        {
                            if (!first) sb.Append(',');
                            sb.Append("Mouse#0");
                            first = false;
                        }
                        for (int j = 0; j <= 3; j++)
                        {
                            if (pl.controllers.ContainsController(ControllerType.Joystick, j))
                            {
                                if (!first) sb.Append(',');
                                sb.Append($"Joystick#{j}");
                                first = false;
                            }
                        }
                    }
                    catch { }
                    sb.Append("] ");
                }

                string line = sb.ToString().TrimEnd();
                if (line == _lastControllerInventory) return;
                _lastControllerInventory = line;
                Plugin.Log.LogInfo($"[NetworkedRewiredIsolation] inventory: {line}");
            }
            catch { }
        }

        private void LogSummary(string summary)
        {
            if (summary == _lastSummary)
                return;

            _lastSummary = summary;
            Plugin.Log.LogInfo($"[NetworkedRewiredIsolation] {summary}.");
        }
    }
}
