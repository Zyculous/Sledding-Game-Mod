using System;
using System.Collections.Generic;
using System.Reflection;
using BepInEx.Configuration;
using UnityEngine;

namespace SledCoopMod
{
    /// <summary>
    /// In-game settings overlay opened by the configurable hotkey
    /// (default Ctrl+P). Walks <see cref="ModSettingsCatalog"/> and renders one
    /// IMGUI control per <see cref="BepInEx.Configuration.ConfigEntry{T}"/>.
    ///
    /// All rendering helpers that take <see cref="ModSettingsCatalog.Item"/>
    /// live on <see cref="ModSettingsCatalog"/> as static methods, not on this
    /// MonoBehaviour. Putting Item-parameter methods on a MonoBehaviour
    /// triggers Il2CppInterop "unsupported parameter type" warnings during
    /// ClassInjector registration, which on this Unity 6 IL2CPP build can
    /// silently break Update/OnGUI dispatch. Keeping the MonoBehaviour
    /// surface limited to lifecycle methods + primitive-typed callbacks
    /// avoids the issue entirely.
    ///
    /// Pauses gameplay (`Time.timeScale = 0`) while open, releases the cursor,
    /// and gates `LocalCoopUI` + `Patch_UiReferenceController_HandleInput` so
    /// the native game ignores keystrokes meant for the modal.
    /// </summary>
    public class ModSettingsUi : MonoBehaviour
    {
        public static ModSettingsUi? Instance { get; private set; }
        public static bool IsAnyOpen => Instance?.IsOpen ?? false;

        public bool IsOpen { get; private set; }

        // Latched at Awake — if the master toggle is off at boot we don't poll
        // the hotkey at all.
        private bool _hotkeyEnabled;

        // Update-tick heartbeat so we can prove from the log that Update is
        // actually being called. First few ticks logged at info, then once
        // every ~5 s at debug to keep noise down.
        private int _updateTickCount;
        private int _lastHeartbeatFrame;
        private bool _awakeLogged;

        // Cached InputSystem keyboard handle. Reflected once (lazily) so that
        // any IL2CPP-Interop type-load failure on the InputSystem path is
        // contained — falling back to legacy UnityEngine.Input.
        private bool _inputSystemProbed;
        private object? _inputSystemKeyboard;
        private MethodInfo? _inputSystemKeyboardKeyByCode;

        private Vector2 _scroll;
        private GUIStyle? _labelStyle;
        private GUIStyle? _headerStyle;
        private GUIStyle? _sectionStyle;
        private GUIStyle? _noteStyle;
        private GUIStyle? _buttonStyle;
        private GUIStyle? _toggleStyle;
        private GUIStyle? _textFieldStyle;
        private Texture2D? _bgTex;

        // While the modal is open we cache the gameplay state so it can be
        // restored on close.
        private float _savedTimeScale = 1f;
        private CursorLockMode _savedLockState = CursorLockMode.None;
        private bool _savedCursorVisible = true;

        // Restart hint banner — set by render code, read in DrawFooter.
        private bool _restartHintVisible;

        // Hotkey rebind capture — when the user clicks "Press a key…" the next
        // KeyDown writes through to the linked ConfigEntry and clears this.
        // We expose a method-based callback instead of a typed reference so
        // ModSettingsCatalog.RenderItem can pass back a primitive lookup key.
        private string? _hotkeyRebindLabel;

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(this);
                return;
            }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            _hotkeyEnabled = ModConfig.Enabled.Value;
            _awakeLogged = true;
            Plugin.Log.LogInfo(
                $"[ModSettingsUi] Awake: enabled={_hotkeyEnabled} hotkey={ModConfig.SettingsHotkey.Value} modifier={ModConfig.SettingsHotkeyModifier.Value}");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_bgTex != null) Destroy(_bgTex);
        }

        private void Update()
        {
            _updateTickCount++;

            // Confirm in the log that Update is actually called. After 30
            // ticks we drop to a 5 s heartbeat to keep noise down.
            if (_updateTickCount <= 5 || (_updateTickCount % 300 == 0))
            {
                Plugin.Log.LogInfo(
                    $"[ModSettingsUi] Update tick #{_updateTickCount} hotkeyEnabled={_hotkeyEnabled} isOpen={IsOpen}");
            }

            // Joystick-binding capture is allowed regardless of hotkey gate
            // (the user must already have the panel open to arm capture).
            if (IsOpen && ControllerBindings.IsCapturing)
            {
                ControllerBindings.TickCapture();
            }

            if (!_hotkeyEnabled) return;

            // Capture-rebind path — first KeyDown after the user clicks
            // "Press a key…" writes through to the linked ConfigEntry.
            if (_hotkeyRebindLabel != null && IsOpen)
            {
                if (TryReadFirstKeyDown(out KeyCode key))
                {
                    var entry = FindCatalogEntryByLabel(_hotkeyRebindLabel);
                    entry?.Set(key.ToString());
                    _hotkeyRebindLabel = null;
                }
                return;
            }

            if (!TryGetHotkey(out KeyCode bound, out KeyCode? requiredModifier))
                return;

            bool modifierHeld = requiredModifier == null
                || IsKeyHeld(requiredModifier.Value)
                || (requiredModifier == KeyCode.LeftControl && IsKeyHeld(KeyCode.RightControl))
                || (requiredModifier == KeyCode.LeftShift   && IsKeyHeld(KeyCode.RightShift))
                || (requiredModifier == KeyCode.LeftAlt     && IsKeyHeld(KeyCode.RightAlt));

            bool keyDown = IsKeyDownThisFrame(bound);
            if (modifierHeld && keyDown)
            {
                Plugin.Log.LogInfo($"[ModSettingsUi] Hotkey fired: modifier={requiredModifier} key={bound} → toggle");
                Toggle();
            }
        }

        public void Toggle()
        {
            if (IsOpen) Close();
            else Open();
        }

        public void Open()
        {
            if (IsOpen) return;

            try
            {
                _savedTimeScale = Time.timeScale;
                _savedLockState = Cursor.lockState;
                _savedCursorVisible = Cursor.visible;

                Time.timeScale = 0f;
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            catch { }

            // Belt-and-braces: ensure the Win32 cursor clip from
            // NetworkedFocusManager is released too. Without this the cursor
            // stays trapped in the host's gameplay window even though we just
            // set lockState=None.
            NetworkedFocusManager.ForceReleaseCursorForMenu();

            _restartHintVisible = false;
            IsOpen = true;
            Plugin.Log.LogInfo("[ModSettingsUi] Settings overlay opened.");
        }

        public void Close()
        {
            if (!IsOpen) return;

            try
            {
                Plugin.Instance.Config.Save();
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[ModSettingsUi] Config save failed: {e.GetType().Name}: {e.Message}");
            }

            IsOpen = false;
            _hotkeyRebindLabel = null;

            try
            {
                Time.timeScale = _savedTimeScale;
                Cursor.lockState = _savedLockState;
                Cursor.visible = _savedCursorVisible;
            }
            catch { }

            Plugin.Log.LogInfo("[ModSettingsUi] Settings overlay closed.");
        }

        private void OnGUI()
        {
            if (!IsOpen) return;

            EnsureStyles();

            float w = Mathf.Min(560f, Screen.width  - 40f);
            float h = Mathf.Min(620f, Screen.height - 40f);
            var rect = new Rect(
                (Screen.width  - w) * 0.5f,
                (Screen.height - h) * 0.5f,
                w, h);

            DrawBackground(rect, new Color(0f, 0f, 0f, 0.88f));

            GUILayout.BeginArea(new Rect(rect.x + 16f, rect.y + 12f, rect.width - 32f, rect.height - 24f));

            GUILayout.Label("SledCoopMod settings", _headerStyle);
            GUILayout.Label("Edits live and persist on close. * = restart required.", _noteStyle);
            GUILayout.Space(6f);

            _scroll = GUILayout.BeginScrollView(_scroll, GUILayout.ExpandWidth(true));
            ModSettingsCatalog.RenderAll(this);

            GUILayout.Space(10f);
            GUILayout.Label("Controller bindings", _sectionStyle);
            GUILayout.Label(
                "Bind a controller (or Keyboard+Mouse) to each local player slot. " +
                "Click Bind, then press any input on the device. KB+M is the default for slot 0.",
                _noteStyle);
            ControllerBindings.DrawBindTableIMGUI(_labelStyle ?? GUI.skin.label, _buttonStyle ?? GUI.skin.button);

            GUILayout.EndScrollView();

            GUILayout.Space(8f);
            if (_restartHintVisible)
                GUILayout.Label("Some changes apply on next launch.", _noteStyle);

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Save & close", _buttonStyle, GUILayout.Width(160f), GUILayout.Height(28f)))
                Close();
            GUILayout.FlexibleSpace();
            GUILayout.Label(
                $"Hotkey: {ModConfig.SettingsHotkeyModifier.Value}+{ModConfig.SettingsHotkey.Value}",
                _noteStyle);
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        // ── Renderer callbacks invoked by ModSettingsCatalog.RenderAll ─────────────

        internal GUIStyle Label  => _labelStyle  ?? GUI.skin.label;
        internal GUIStyle Header => _headerStyle ?? GUI.skin.label;
        internal GUIStyle Section => _sectionStyle ?? GUI.skin.label;
        internal GUIStyle Note    => _noteStyle   ?? GUI.skin.label;
        internal GUIStyle Button  => _buttonStyle ?? GUI.skin.button;
        internal GUIStyle Toggle_ => _toggleStyle ?? GUI.skin.button;
        internal GUIStyle TextField_ => _textFieldStyle ?? GUI.skin.textField;

        internal void NoteRestartRequired() => _restartHintVisible = true;

        internal bool IsRebindArmed(string label) =>
            _hotkeyRebindLabel == label;

        internal void ArmRebind(string label) => _hotkeyRebindLabel = label;

        // ── Hotkey resolution ──────────────────────────────────────────────────────

        private static bool TryGetHotkey(out KeyCode key, out KeyCode? modifier)
        {
            key = KeyCode.P;
            modifier = KeyCode.LeftControl;

            try
            {
                if (Enum.TryParse(ModConfig.SettingsHotkey.Value, ignoreCase: true, out KeyCode parsed))
                    key = parsed;
            }
            catch { }

            modifier = ModConfig.SettingsHotkeyModifier.Value switch
            {
                "None"    => null,
                "Shift"   => KeyCode.LeftShift,
                "Alt"     => KeyCode.LeftAlt,
                _         => KeyCode.LeftControl,
            };

            return true;
        }

        // Polymorphic key check: tries legacy Input first (cheapest, works
        // when Active Input Handling = Both), falls back to InputSystem
        // Keyboard.current via reflection if legacy returned nothing — some
        // Unity 6 builds disable legacy when Active Input Handling = New only.
        private bool IsKeyHeld(KeyCode k)
        {
            try { if (UnityEngine.Input.GetKey(k)) return true; }
            catch { }
            return InputSystemKeyHeld(k);
        }

        private bool IsKeyDownThisFrame(KeyCode k)
        {
            try { if (UnityEngine.Input.GetKeyDown(k)) return true; }
            catch { }
            return InputSystemKeyDownThisFrame(k);
        }

        private bool InputSystemKeyHeld(KeyCode k) =>
            QueryInputSystemKey(k, "isPressed");

        private bool InputSystemKeyDownThisFrame(KeyCode k) =>
            QueryInputSystemKey(k, "wasPressedThisFrame");

        // Reflective probe of UnityEngine.InputSystem.Keyboard.current[KeyCode].
        // The Keyboard class exposes a `KeyControl this[Key key]` indexer,
        // where Key is a Unity InputSystem enum — KeyCode.P maps to Key.P,
        // KeyCode.LeftControl → Key.LeftCtrl, etc.
        private bool QueryInputSystemKey(KeyCode k, string boolPropertyName)
        {
            EnsureInputSystemProbed();
            if (_inputSystemKeyboard == null || _inputSystemKeyboardKeyByCode == null)
                return false;

            try
            {
                int? inputSystemKey = MapKeyCodeToInputSystemKey(k);
                if (inputSystemKey == null) return false;

                object? control = _inputSystemKeyboardKeyByCode.Invoke(_inputSystemKeyboard, new object[] { inputSystemKey.Value });
                if (control == null) return false;

                var prop = control.GetType().GetProperty(boolPropertyName, BindingFlags.Instance | BindingFlags.Public);
                if (prop == null) return false;

                object? val = prop.GetValue(control);
                return val is bool b && b;
            }
            catch { return false; }
        }

        private void EnsureInputSystemProbed()
        {
            if (_inputSystemProbed) return;
            _inputSystemProbed = true;

            try
            {
                Type? keyboardType = Type.GetType("UnityEngine.InputSystem.Keyboard, Unity.InputSystem")
                    ?? PatchHelpersTryFind("UnityEngine.InputSystem.Keyboard");
                if (keyboardType == null) return;

                var currentProp = keyboardType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                _inputSystemKeyboard = currentProp?.GetValue(null);

                Type? keyEnum = Type.GetType("UnityEngine.InputSystem.Key, Unity.InputSystem")
                    ?? PatchHelpersTryFind("UnityEngine.InputSystem.Key");

                if (keyEnum != null)
                {
                    _inputSystemKeyboardKeyByCode = keyboardType.GetMethod(
                        "get_Item",
                        BindingFlags.Instance | BindingFlags.Public,
                        binder: null,
                        types: new[] { keyEnum },
                        modifiers: null);
                }

                Plugin.Log.LogInfo(
                    $"[ModSettingsUi] InputSystem probe: keyboard={(_inputSystemKeyboard != null ? "found" : "null")} indexer={(_inputSystemKeyboardKeyByCode != null ? "found" : "null")}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[ModSettingsUi] InputSystem probe failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private static Type? PatchHelpersTryFind(string typeName)
        {
            try { return SledCoopMod.Patches.PatchHelpers.SafeTypeByName(typeName); }
            catch { return null; }
        }

        // Map common Unity legacy KeyCodes to InputSystem Key enum integer
        // values. The InputSystem Key enum has stable integer values across
        // versions: Space=14, A=22, P=37, etc. We hardcode the subset we
        // expect users to bind to. Anything else returns null and we just
        // rely on legacy Input for that key.
        //
        // Source: UnityEngine.InputSystem.Key.cs in com.unity.inputsystem
        private static int? MapKeyCodeToInputSystemKey(KeyCode k) => k switch
        {
            KeyCode.Space        => 14,
            KeyCode.Return       => 13,
            KeyCode.Escape       => 12,
            KeyCode.LeftShift    => 5,
            KeyCode.RightShift   => 6,
            KeyCode.LeftAlt      => 7,
            KeyCode.RightAlt     => 8,
            KeyCode.LeftControl  => 9,
            KeyCode.RightControl => 10,
            KeyCode.A => 22, KeyCode.B => 23, KeyCode.C => 24, KeyCode.D => 25,
            KeyCode.E => 26, KeyCode.F => 27, KeyCode.G => 28, KeyCode.H => 29,
            KeyCode.I => 30, KeyCode.J => 31, KeyCode.K => 32, KeyCode.L => 33,
            KeyCode.M => 34, KeyCode.N => 35, KeyCode.O => 36, KeyCode.P => 37,
            KeyCode.Q => 38, KeyCode.R => 39, KeyCode.S => 40, KeyCode.T => 41,
            KeyCode.U => 42, KeyCode.V => 43, KeyCode.W => 44, KeyCode.X => 45,
            KeyCode.Y => 46, KeyCode.Z => 47,
            KeyCode.Alpha0 => 48, KeyCode.Alpha1 => 49, KeyCode.Alpha2 => 50,
            KeyCode.Alpha3 => 51, KeyCode.Alpha4 => 52, KeyCode.Alpha5 => 53,
            KeyCode.Alpha6 => 54, KeyCode.Alpha7 => 55, KeyCode.Alpha8 => 56,
            KeyCode.Alpha9 => 57,
            KeyCode.F1  => 80, KeyCode.F2  => 81, KeyCode.F3  => 82, KeyCode.F4  => 83,
            KeyCode.F5  => 84, KeyCode.F6  => 85, KeyCode.F7  => 86, KeyCode.F8  => 87,
            KeyCode.F9  => 88, KeyCode.F10 => 89, KeyCode.F11 => 90, KeyCode.F12 => 91,
            _ => null,
        };

        private bool TryReadFirstKeyDown(out KeyCode key)
        {
            for (int kc = (int)KeyCode.Backspace; kc <= (int)KeyCode.F12; kc++)
            {
                var candidate = (KeyCode)kc;
                if (IsModifierKey(candidate)) continue;

                if (IsKeyDownThisFrame(candidate))
                {
                    key = candidate;
                    return true;
                }
            }

            key = KeyCode.None;
            return false;
        }

        private static bool IsModifierKey(KeyCode k) =>
            k == KeyCode.LeftControl || k == KeyCode.RightControl
            || k == KeyCode.LeftShift || k == KeyCode.RightShift
            || k == KeyCode.LeftAlt   || k == KeyCode.RightAlt
            || k == KeyCode.LeftCommand || k == KeyCode.RightCommand;

        private static ModSettingsCatalog.IConfigEntryWrapper? FindCatalogEntryByLabel(string label)
        {
            foreach (var item in ModSettingsCatalog.Build())
            {
                if (string.Equals(item.Label, label, StringComparison.Ordinal))
                    return item.Entry;
            }
            return null;
        }

        // ── Style scaffolding ──────────────────────────────────────────────────────

        private void EnsureStyles()
        {
            if (_labelStyle != null) return;

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = false,
                normal = { textColor = new Color(0.92f, 0.96f, 1f, 1f) }
            };
            _headerStyle = new GUIStyle(_labelStyle)
            {
                fontSize = 18,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.85f, 0.25f, 1f) }
            };
            _sectionStyle = new GUIStyle(_labelStyle)
            {
                fontSize = 14,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(0.7f, 0.85f, 1f, 1f) }
            };
            _noteStyle = new GUIStyle(_labelStyle)
            {
                fontSize = 11,
                fontStyle = FontStyle.Italic,
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f, 1f) }
            };
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 12 };
            _toggleStyle = new GUIStyle(GUI.skin.button) { fontSize = 12 };
            _textFieldStyle = new GUIStyle(GUI.skin.textField) { fontSize = 12 };
        }

        private void DrawBackground(Rect rect, Color color)
        {
            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, Color.white);
                _bgTex.Apply();
            }

            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _bgTex);
            GUI.color = old;
        }
    }
}
