using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SledCoopMod.Patches
{
    // Bridges Unity InputSystem gamepad state into Rewired action queries on
    // child (P2+) instances.
    //
    // Why: Wine routes DirectInput/XInput to the foreground window only, so
    // Rewired's native joystick polling is dead in the child window even
    // though AddController/LoadDefaultMaps/LoadMap/SetAllMapsEnabled and
    // isPlaying=true all return success. Unity InputSystem (SDL2 backed)
    // *does* receive the gamepad on the child — confirmed by probe lines
    // showing rawBtns=(none) rawAxes=(none) while unityIS=buttonSouth /
    // LS=… fire at the same instant.
    //
    // Strategy: Harmony-postfix every Rewired.Player action query
    // (GetButton/GetButtonDown/GetButtonUp/GetAxis/GetAxisRaw — both
    // string- and int-keyed). When the original returns false/0 on the
    // child instance, we resolve the action's bound element *name*
    // ("A", "Right Trigger", "Left Stick X", …) via Rewired's own
    // ActionElementMap.elementIdentifierName, then map the name to a
    // Unity InputSystem control on Gamepad.current.
    //
    // Mapping by name (not numeric id) is critical: Rewired's
    // elementIdentifierId is a stable per-template integer that's NOT a
    // 0-based array index — it can be 1000+ depending on the controller
    // template. Earlier versions of this file mapped by id and ended up
    // associating "Interact" with stick-axis movement.
    internal static class InputSystemBridge
    {
        public static bool IsActiveOnThisInstance =>
            NetworkedInstanceManager.Instance?.IsChildClient == true;

        // ── Element-name → InputSystem control lookup ──────────────────────────
        // Covers Rewired's standard Xbox/Generic-Gamepad/PlayStation template
        // identifier names. PS names included so DualShock-style controllers
        // map to the right Unity InputSystem controls too.
        private static readonly Dictionary<string, string> _buttonNameToInputSystem =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                { "A", "buttonSouth" },
                { "Cross", "buttonSouth" },
                { "B", "buttonEast" },
                { "Circle", "buttonEast" },
                { "X", "buttonWest" },
                { "Square", "buttonWest" },
                { "Y", "buttonNorth" },
                { "Triangle", "buttonNorth" },
                { "Left Bumper", "leftShoulder" },
                { "Left Shoulder", "leftShoulder" },
                { "L1", "leftShoulder" },
                { "Right Bumper", "rightShoulder" },
                { "Right Shoulder", "rightShoulder" },
                { "R1", "rightShoulder" },
                { "Back", "selectButton" },
                { "Select", "selectButton" },
                { "Share", "selectButton" },
                { "View", "selectButton" },
                { "Start", "startButton" },
                { "Options", "startButton" },
                { "Menu", "startButton" },
                { "Left Stick Button", "leftStickButton" },
                { "Left Stick Click", "leftStickButton" },
                { "L3", "leftStickButton" },
                { "Right Stick Button", "rightStickButton" },
                { "Right Stick Click", "rightStickButton" },
                { "R3", "rightStickButton" },
                // Triggers also live in the axis dictionary above for stick-style
                // reads, but the InputSystem ButtonControl backing them exposes
                // isPressed (true above pressPoint), so we can route them here
                // for actions whose Rewired binding is button-shaped (e.g.
                // "Hand Action" → Right Trigger).
                { "Left Trigger", "leftTrigger" },
                { "L2", "leftTrigger" },
                { "Right Trigger", "rightTrigger" },
                { "R2", "rightTrigger" },
            };

        // Element-name → (stickName, isYAxis) for axes, or trigger control name.
        // The game's HUD prompts and Rewired's standard templates use these
        // exact strings.
        private static readonly Dictionary<string, AxisRoute> _axisNameToInputSystem =
            new Dictionary<string, AxisRoute>(StringComparer.OrdinalIgnoreCase)
            {
                { "Left Stick X",  new AxisRoute(AxisKind.StickX, "leftStick") },
                { "Left Stick Y",  new AxisRoute(AxisKind.StickY, "leftStick") },
                { "Right Stick X", new AxisRoute(AxisKind.StickX, "rightStick") },
                { "Right Stick Y", new AxisRoute(AxisKind.StickY, "rightStick") },
                { "Left Trigger",  new AxisRoute(AxisKind.Trigger, "leftTrigger") },
                { "L2",            new AxisRoute(AxisKind.Trigger, "leftTrigger") },
                { "Right Trigger", new AxisRoute(AxisKind.Trigger, "rightTrigger") },
                { "R2",            new AxisRoute(AxisKind.Trigger, "rightTrigger") },
            };

        private enum AxisKind { StickX, StickY, Trigger }
        private readonly struct AxisRoute
        {
            public readonly AxisKind Kind;
            public readonly string Member;
            public AxisRoute(AxisKind k, string m) { Kind = k; Member = m; }
        }

        // ── DPad name handling ─────────────────────────────────────────────────
        // DPad is Rewired-modeled as both buttons (DPad Up/Down/Left/Right)
        // and axes (DPad X/Y, with PositiveAxis/NegativeAxis polarity).
        // Returns the InputSystem dpad sub-control name when applicable.
        // Rewired surfaces these element names as either "DPad Up" or
        // "D-Pad Up" depending on controller template — strip the hyphen so
        // both spellings hit the same route.
        private static string? DPadButtonRouteForName(string name)
        {
            string n = name.Trim().Replace("-", "");
            if (n.Equals("DPad Up", StringComparison.OrdinalIgnoreCase)) return "up";
            if (n.Equals("DPad Right", StringComparison.OrdinalIgnoreCase)) return "right";
            if (n.Equals("DPad Down", StringComparison.OrdinalIgnoreCase)) return "down";
            if (n.Equals("DPad Left", StringComparison.OrdinalIgnoreCase)) return "left";
            return null;
        }

        // ── Action-name → element-name fallback ────────────────────────────────
        // The default Rewired profile in Sledding Game has *no* joystick binding
        // for several gameplay/UI actions (Snowball, PlaceMarker, Build,
        // UISubmit/UICancel etc.) — they're keyboard-only. On the host that's
        // fine because Rewired's keyboard pipeline still works, but on the
        // child instance the bridge has no joystick binding to translate, so
        // the action never fires regardless of which gamepad button is pressed.
        //
        // This table supplies a synthetic gamepad element name for known
        // actions. Used when Rewired returns no joystick binding for the
        // action OR when the binding it returns isn't gamepad-mappable.
        // Multiple actions can intentionally map to the same element (e.g.
        // Snowball + Interact both fire on X — the game switches the prompt
        // contextually based on what's in front of the player).
        private static readonly Dictionary<string, string> _actionNameToElementName =
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                // Gameplay: see SledCoopMod/src/LocalInputProvider.cs for the
                // canonical mapping the local-coop slots use. Keep these in
                // sync so child-instance gamepad layout matches the in-process
                // P2/P3 layout.
                { "Snowball",       "X" },                   // X / Square
                { "PlaceMarker",    "Right Stick Button" },  // R3
                { "TeleportMarker", "Right Shoulder" },      // RB / R1
                { "Build",          "Y" },                   // Y / Triangle
                { "Inventory",      "Select" },              // Back / Select / Share
                { "Sled",           "Left Stick Button" },   // L3
                { "Sprint",         "Left Shoulder" },       // LB
                { "Crouch",         "B" },
                { "GetUp",          "A" },
                // "Hand Action" is the keyboard's left-mouse-button binding
                // — used to throw snowballs and punch/push other players.
                // Without an override the bridge resolves it to "Left Mouse
                // Button" which has no gamepad route, so on the child the
                // player can pick a snowball up but cannot throw it.
                { "Hand Action",        "Right Trigger" },
                { "HandAction",         "Right Trigger" },
                { "Hand Action Charge", "Right Trigger" },
                { "Throw",              "Right Trigger" },
                { "Punch",              "Right Trigger" },
                { "Hit",                "Right Trigger" },
                // UI navigation — RewiredStandaloneInputModule polls these
                // names by default. Without them, gamepad navigation in
                // settings/inventory/pause menus does nothing on the child.
                { "UISubmit",       "A" },
                { "UICancel",       "B" },
                { "Submit",         "A" },
                { "Cancel",         "B" },
            };

        // Cache action-id → action-name lookups so we can apply name-based
        // overrides for the int-keyed GetButton calls (most game-side polls
        // use ids, not strings). Reflection into ReInput is otherwise too
        // expensive to do per-call.
        private static readonly Dictionary<int, string?> _actionIdToName =
            new Dictionary<int, string?>();

        private static string? ResolveActionName(object actionKey)
        {
            if (actionKey is string s) return s;
            if (actionKey is int id)
            {
                if (_actionIdToName.TryGetValue(id, out var cached)) return cached;
                string? name = null;
                try
                {
                    var reInputType = PatchHelpers.SafeTypeByName("Rewired.ReInput");
                    var mapping = reInputType?.GetProperty("mapping",
                        BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                    if (mapping != null)
                    {
                        var getAction = mapping.GetType().GetMethod("GetAction",
                            BindingFlags.Public | BindingFlags.Instance, null,
                            new[] { typeof(int) }, null);
                        var action = getAction?.Invoke(mapping, new object[] { id });
                        name = action?.GetType().GetProperty("name",
                            BindingFlags.Public | BindingFlags.Instance)?.GetValue(action) as string;
                    }
                }
                catch { }
                _actionIdToName[id] = name;
                return name;
            }
            return null;
        }

        private static bool IsButtonNameGamepadMappable(string? name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            string trimmed = name!.Trim();
            return _buttonNameToInputSystem.ContainsKey(trimmed)
                || DPadButtonRouteForName(trimmed) != null;
        }

        // ── Public API ─────────────────────────────────────────────────────────
        public static bool TryReadButtonByName(string elementName, out bool pressed)
        {
            pressed = false;
            object? pad = GetGamepadCurrent();
            if (pad == null) return false;

            string? dpadDir = DPadButtonRouteForName(elementName);
            if (dpadDir != null)
            {
                var dpad = GetMember(pad, "dpad");
                if (dpad == null) return false;
                pressed = ReadIsPressed(GetMember(dpad, dpadDir));
                return true;
            }

            if (!_buttonNameToInputSystem.TryGetValue(elementName.Trim(), out var controlName))
                return false;

            pressed = ReadIsPressed(GetMember(pad, controlName));
            return true;
        }

        public static bool TryReadAxisByName(string elementName, int axisRange, out float value)
        {
            value = 0f;
            object? pad = GetGamepadCurrent();
            if (pad == null) return false;

            if (!_axisNameToInputSystem.TryGetValue(elementName.Trim(), out var route))
                return false;

            switch (route.Kind)
            {
                case AxisKind.StickX:
                {
                    var v = ReadStickVector(pad, route.Member);
                    value = v.x;
                    break;
                }
                case AxisKind.StickY:
                {
                    var v = ReadStickVector(pad, route.Member);
                    // Rewired stick Y typically inverted vs InputSystem (Rewired
                    // up = +1, InputSystem up = +1) — we keep raw here.
                    value = v.y;
                    break;
                }
                case AxisKind.Trigger:
                    value = ReadFloatControl(pad, route.Member);
                    break;
            }

            // Rewired axisRange: 0 = Full, 1 = Positive, 2 = Negative.
            // When the action expects a half-axis, clip accordingly.
            if (axisRange == 1 && value < 0f) value = 0f;
            else if (axisRange == 2)
            {
                if (value > 0f) value = 0f;
                else value = -value;
            }

            return true;
        }

        // ── Action → element-name resolution ───────────────────────────────────
        // Uses Rewired's GetFirstButtonMapWithAction / GetFirstAxisMapWithAction.
        // The returned ActionElementMap exposes elementIdentifierName as a string
        // — that's the stable name we map below.
        // Per-action diagnostic — log once per unique (action, resolved-name)
        // pair so the next log shows exactly what element name each action
        // resolved to and whether our dictionary mapped it to an InputSystem
        // control. If we see "→ '???' (unmapped)" we know the dict needs an
        // entry for that name string.
        private static readonly System.Collections.Generic.HashSet<string> _diagLogged =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        public static string? ResolveButtonElementName(object player, object actionKey)
        {
            string? rewiredName = null;
            try
            {
                var aem = InvokeGetFirstMapWithAction(player, "GetFirstButtonMapWithAction", actionKey);
                if (aem != null) rewiredName = GetElementIdentifierName(aem);
            }
            catch { }

            string actionTag = actionKey is string s ? s : $"id:{actionKey}";

            // Prefer Rewired's own joystick binding when it's gamepad-mappable.
            if (IsButtonNameGamepadMappable(rewiredName))
            {
                string diagKey = "btn:" + actionTag + ":" + rewiredName;
                if (_diagLogged.Add(diagKey))
                    Plugin.Log.LogInfo($"[InputSystemBridge] btn-action '{actionTag}' → element '{rewiredName}' (mapped: yes).");
                return rewiredName;
            }

            // Fallback: action-name override for actions Rewired has no
            // joystick binding for (Snowball, PlaceMarker, UISubmit, ...).
            string? actionName = ResolveActionName(actionKey);
            if (actionName != null
                && _actionNameToElementName.TryGetValue(actionName, out var overrideName))
            {
                string diagKey = "btn:" + actionTag + ":" + overrideName + ":override";
                if (_diagLogged.Add(diagKey))
                    Plugin.Log.LogInfo($"[InputSystemBridge] btn-action '{actionTag}' (name='{actionName}') → override element '{overrideName}' (Rewired returned '{rewiredName ?? "<null>"}').");
                return overrideName;
            }

            // No mapping anywhere — log once so we know which actions need
            // a new override entry, then return whatever Rewired had (likely
            // a keyboard key like "G" that won't match any gamepad control).
            if (rewiredName != null)
            {
                string diagKey = "btn:" + actionTag + ":" + rewiredName + ":unmapped";
                if (_diagLogged.Add(diagKey))
                    Plugin.Log.LogInfo($"[InputSystemBridge] btn-action '{actionTag}' (name='{actionName ?? "<null>"}') → element '{rewiredName}' (mapped: NO).");
            }
            return rewiredName;
        }

        public static (string? name, int axisRange) ResolveAxisElementInfo(object player, object actionKey)
        {
            try
            {
                var aem = InvokeGetFirstMapWithAction(player, "GetFirstAxisMapWithAction", actionKey);
                if (aem == null) return (null, 0);
                string? name = GetElementIdentifierName(aem);
                int range = 0;
                try
                {
                    var p = aem.GetType().GetProperty("axisRange",
                        BindingFlags.Public | BindingFlags.Instance);
                    var v = p?.GetValue(aem);
                    if (v != null) range = Convert.ToInt32(v);
                }
                catch { }

                string actionTag = actionKey is string s ? s : $"id:{actionKey}";
                string diagKey = "axis:" + actionTag + ":" + (name ?? "<null>");
                if (_diagLogged.Add(diagKey))
                {
                    bool mapped = name != null && _axisNameToInputSystem.ContainsKey(name);
                    Plugin.Log.LogInfo($"[InputSystemBridge] axis-action '{actionTag}' → element '{name ?? "<null>"}' (range={range}, mapped: {(mapped ? "yes" : "NO")}).");
                }
                return (name, range);
            }
            catch { return (null, 0); }
        }

        private static object? InvokeGetFirstMapWithAction(object player, string methodName, object actionKey)
        {
            var maps = GetMapHelper(player);
            if (maps == null) return null;

            var t = maps.GetType();
            Type keyType = actionKey is string ? typeof(string) : typeof(int);

            // Prefer the controller-type-filtered overload so we get the
            // joystick-specific binding, not whatever map (often keyboard)
            // happens to be registered first. This is the key fix for
            // actions like Snowball/PlaceMarker that have *both* keyboard
            // and joystick bindings — Rewired's plain GetFirstXxxWithAction
            // returns the first match regardless of controller type, which
            // ends up being the keyboard map and gives us a name like "G"
            // that doesn't map to any gamepad control.
            var ctrlTypeEnum = PatchHelpers.SafeTypeByName("Rewired.ControllerType");
            if (ctrlTypeEnum != null)
            {
                var mFiltered = t.GetMethod(methodName,
                    BindingFlags.Public | BindingFlags.Instance,
                    null,
                    new[] { ctrlTypeEnum, keyType, typeof(bool) },
                    null);
                if (mFiltered != null)
                {
                    object joystickEnumValue;
                    try { joystickEnumValue = Enum.Parse(ctrlTypeEnum, "Joystick"); }
                    catch { joystickEnumValue = 2; /* Rewired's Joystick = 2 in the standard enum */ }

                    var aem = mFiltered.Invoke(maps, new object[] { joystickEnumValue, actionKey, true });
                    if (aem != null) return aem;
                    // If the joystick filter returned nothing, fall through to
                    // the unfiltered call so axis-only actions etc. still work.
                }
            }

            // (TKey, bool skipDisabled) — unfiltered fallback.
            var m = t.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { keyType, typeof(bool) },
                null);
            if (m != null)
                return m.Invoke(maps, new object[] { actionKey, true });

            // Single-arg fallback.
            m = t.GetMethod(methodName,
                BindingFlags.Public | BindingFlags.Instance,
                null,
                new[] { keyType },
                null);
            return m?.Invoke(maps, new object[] { actionKey });
        }

        private static string? GetElementIdentifierName(object actionElementMap)
        {
            try
            {
                // Property direct read.
                var p = actionElementMap.GetType().GetProperty("elementIdentifierName",
                    BindingFlags.Public | BindingFlags.Instance);
                if (p?.GetValue(actionElementMap) is string s && !string.IsNullOrEmpty(s))
                    return s;
            }
            catch { }
            return null;
        }

        // ── Internals ──────────────────────────────────────────────────────────
        private static object? GetMapHelper(object player)
        {
            try
            {
                var controllers = player.GetType().GetProperty("controllers",
                    BindingFlags.Public | BindingFlags.Instance)?.GetValue(player);
                if (controllers == null) return null;
                return controllers.GetType().GetProperty("maps",
                    BindingFlags.Public | BindingFlags.Instance)?.GetValue(controllers);
            }
            catch { return null; }
        }

        private static object? GetGamepadCurrent()
        {
            try
            {
                var t = Type.GetType("UnityEngine.InputSystem.Gamepad, Unity.InputSystem");
                if (t == null) return null;
                return t.GetProperty("current",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            }
            catch { return null; }
        }

        private static object? GetMember(object obj, string name)
        {
            try
            {
                return obj.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
            }
            catch { return null; }
        }

        private static bool ReadIsPressed(object? control)
        {
            if (control == null) return false;
            try
            {
                var p = control.GetType().GetProperty("isPressed",
                    BindingFlags.Public | BindingFlags.Instance);
                return p?.GetValue(control) is bool b && b;
            }
            catch { return false; }
        }

        private static Vector2 ReadStickVector(object pad, string stickName)
        {
            try
            {
                var stick = GetMember(pad, stickName);
                if (stick == null) return Vector2.zero;
                var read = stick.GetType().GetMethod("ReadValue", Type.EmptyTypes);
                return read?.Invoke(stick, null) is Vector2 v ? v : Vector2.zero;
            }
            catch { return Vector2.zero; }
        }

        private static float ReadFloatControl(object pad, string controlName)
        {
            try
            {
                var ctrl = GetMember(pad, controlName);
                if (ctrl == null) return 0f;
                var read = ctrl.GetType().GetMethod("ReadValue", Type.EmptyTypes);
                return read?.Invoke(ctrl, null) is float f ? f : 0f;
            }
            catch { return 0f; }
        }
    }

    // ── Per-action edge-state caches for synthesized GetButtonDown/Up ──────────
    //
    // Stores the frame number of the most-recent down-edge and up-edge per
    // action key. Multiple GetButtonDown(...) calls within the same frame
    // ALL return true on the down-edge frame, instead of only the first.
    // This fixes ragdoll-recovery and similar consumers that poll the action
    // multiple times per frame from different systems.
    internal static class BridgeEdgeState
    {
        // Down-edge frame numbers + the button state observed when the
        // edge tracker was last updated (so we know when to flip up→down).
        public sealed class Edge
        {
            public bool LastPressed;
            public int DownFrame = -1;
            public int UpFrame = -1;
            public int LastSampleFrame = -2;
        }

        public static readonly Dictionary<string, Edge> ByName =
            new Dictionary<string, Edge>(StringComparer.Ordinal);
        public static readonly Dictionary<int, Edge> ById = new Dictionary<int, Edge>();

        // Updates the edge tracker exactly once per frame for a given action,
        // so multiple polls within the same frame see the same edge result.
        public static void SampleOncePerFrame(Edge e, bool pressed)
        {
            int f = UnityEngine.Time.frameCount;
            if (e.LastSampleFrame == f) return;
            e.LastSampleFrame = f;
            if (pressed && !e.LastPressed) e.DownFrame = f;
            if (!pressed && e.LastPressed) e.UpFrame = f;
            e.LastPressed = pressed;
        }

        public static bool IsDownThisFrame(Edge e) =>
            e.DownFrame == UnityEngine.Time.frameCount;

        public static bool IsUpThisFrame(Edge e) =>
            e.UpFrame == UnityEngine.Time.frameCount;
    }

    // ─── String-keyed action queries ───────────────────────────────────────────
    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_GetButton_String
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            if (t == null) return null;
            return t.GetMethod("GetButton", new[] { typeof(string) });
        }

        [HarmonyPostfix]
        static void Postfix(ref bool __result, string actionName, object __instance)
        {
            if (__result) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            string? elName = InputSystemBridge.ResolveButtonElementName(__instance, actionName);
            if (elName == null) return;
            if (InputSystemBridge.TryReadButtonByName(elName, out bool pressed) && pressed)
                __result = true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_GetButtonDown_String
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            if (t == null) return null;
            return t.GetMethod("GetButtonDown", new[] { typeof(string) });
        }

        [HarmonyPostfix]
        static void Postfix(ref bool __result, string actionName, object __instance)
        {
            if (__result) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            string? elName = InputSystemBridge.ResolveButtonElementName(__instance, actionName);
            if (elName == null) return;
            if (!InputSystemBridge.TryReadButtonByName(elName, out bool pressed)) return;
            if (!BridgeEdgeState.ByName.TryGetValue(actionName, out var e))
                BridgeEdgeState.ByName[actionName] = e = new BridgeEdgeState.Edge();
            BridgeEdgeState.SampleOncePerFrame(e, pressed);
            if (BridgeEdgeState.IsDownThisFrame(e)) __result = true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_GetButtonUp_String
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            if (t == null) return null;
            return t.GetMethod("GetButtonUp", new[] { typeof(string) });
        }

        [HarmonyPostfix]
        static void Postfix(ref bool __result, string actionName, object __instance)
        {
            if (__result) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            string? elName = InputSystemBridge.ResolveButtonElementName(__instance, actionName);
            if (elName == null) return;
            if (!InputSystemBridge.TryReadButtonByName(elName, out bool pressed)) return;
            if (!BridgeEdgeState.ByName.TryGetValue(actionName, out var e))
                BridgeEdgeState.ByName[actionName] = e = new BridgeEdgeState.Edge();
            BridgeEdgeState.SampleOncePerFrame(e, pressed);
            if (BridgeEdgeState.IsUpThisFrame(e)) __result = true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_GetAxis_String
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            if (t == null) return null;
            return t.GetMethod("GetAxis", new[] { typeof(string) });
        }

        [HarmonyPostfix]
        static void Postfix(ref float __result, string actionName, object __instance)
        {
            if (Math.Abs(__result) > 0.001f) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            var info = InputSystemBridge.ResolveAxisElementInfo(__instance, actionName);
            if (info.name == null) return;
            if (InputSystemBridge.TryReadAxisByName(info.name, info.axisRange, out float v)
                && Math.Abs(v) > 0.001f)
                __result = v;
        }
    }

    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_GetAxisRaw_String
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            if (t == null) return null;
            return t.GetMethod("GetAxisRaw", new[] { typeof(string) });
        }

        [HarmonyPostfix]
        static void Postfix(ref float __result, string actionName, object __instance)
        {
            if (Math.Abs(__result) > 0.001f) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            var info = InputSystemBridge.ResolveAxisElementInfo(__instance, actionName);
            if (info.name == null) return;
            if (InputSystemBridge.TryReadAxisByName(info.name, info.axisRange, out float v)
                && Math.Abs(v) > 0.001f)
                __result = v;
        }
    }

    // ─── Int-keyed action queries (game character controllers commonly cache
    //     action IDs and call by int) ───────────────────────────────────────────
    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_GetButton_Int
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            if (t == null) return null;
            return t.GetMethod("GetButton", new[] { typeof(int) });
        }

        [HarmonyPostfix]
        static void Postfix(ref bool __result, int actionId, object __instance)
        {
            if (__result) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            string? elName = InputSystemBridge.ResolveButtonElementName(__instance, actionId);
            if (elName == null) return;
            if (InputSystemBridge.TryReadButtonByName(elName, out bool pressed) && pressed)
                __result = true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_GetButtonDown_Int
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            if (t == null) return null;
            return t.GetMethod("GetButtonDown", new[] { typeof(int) });
        }

        [HarmonyPostfix]
        static void Postfix(ref bool __result, int actionId, object __instance)
        {
            if (__result) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            string? elName = InputSystemBridge.ResolveButtonElementName(__instance, actionId);
            if (elName == null) return;
            if (!InputSystemBridge.TryReadButtonByName(elName, out bool pressed)) return;
            if (!BridgeEdgeState.ById.TryGetValue(actionId, out var e))
                BridgeEdgeState.ById[actionId] = e = new BridgeEdgeState.Edge();
            BridgeEdgeState.SampleOncePerFrame(e, pressed);
            if (BridgeEdgeState.IsDownThisFrame(e)) __result = true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_GetButtonUp_Int
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            if (t == null) return null;
            return t.GetMethod("GetButtonUp", new[] { typeof(int) });
        }

        [HarmonyPostfix]
        static void Postfix(ref bool __result, int actionId, object __instance)
        {
            if (__result) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            string? elName = InputSystemBridge.ResolveButtonElementName(__instance, actionId);
            if (elName == null) return;
            if (!InputSystemBridge.TryReadButtonByName(elName, out bool pressed)) return;
            if (!BridgeEdgeState.ById.TryGetValue(actionId, out var e))
                BridgeEdgeState.ById[actionId] = e = new BridgeEdgeState.Edge();
            BridgeEdgeState.SampleOncePerFrame(e, pressed);
            if (BridgeEdgeState.IsUpThisFrame(e)) __result = true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_GetAxis_Int
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            if (t == null) return null;
            return t.GetMethod("GetAxis", new[] { typeof(int) });
        }

        [HarmonyPostfix]
        static void Postfix(ref float __result, int actionId, object __instance)
        {
            if (Math.Abs(__result) > 0.001f) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            var info = InputSystemBridge.ResolveAxisElementInfo(__instance, actionId);
            if (info.name == null) return;
            if (InputSystemBridge.TryReadAxisByName(info.name, info.axisRange, out float v)
                && Math.Abs(v) > 0.001f)
                __result = v;
        }
    }

    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_GetAxisRaw_Int
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            if (t == null) return null;
            return t.GetMethod("GetAxisRaw", new[] { typeof(int) });
        }

        [HarmonyPostfix]
        static void Postfix(ref float __result, int actionId, object __instance)
        {
            if (Math.Abs(__result) > 0.001f) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            var info = InputSystemBridge.ResolveAxisElementInfo(__instance, actionId);
            if (info.name == null) return;
            if (InputSystemBridge.TryReadAxisByName(info.name, info.axisRange, out float v)
                && Math.Abs(v) > 0.001f)
                __result = v;
        }
    }

    // ─── "Any button" queries — used by ragdoll-recovery / "press any button"
    //     prompts. Rewired's joystick is dead in the child, so the native
    //     query never returns true. Bridge: check every standard gamepad
    //     button on Unity InputSystem's Gamepad.current and return true on
    //     any press. ─────────────────────────────────────────────────────────
    internal static class AnyButtonBridge
    {
        public static bool IsAnyInputSystemButtonPressed()
        {
            object? pad = GetGamepadCurrent();
            if (pad == null) return false;
            foreach (string control in new[]
            {
                "buttonSouth", "buttonEast", "buttonWest", "buttonNorth",
                "leftShoulder", "rightShoulder",
                "selectButton", "startButton",
                "leftStickButton", "rightStickButton",
            })
            {
                if (IsPressed(GetMember(pad, control))) return true;
            }

            // Triggers: treat as pressed when value crosses ~0.5 — analog
            // triggers don't have isPressed in the same way as buttons.
            var lt = GetMember(pad, "leftTrigger");
            if (ReadFloat(lt) > 0.5f) return true;
            var rt = GetMember(pad, "rightTrigger");
            if (ReadFloat(rt) > 0.5f) return true;

            // DPad sub-controls.
            var dpad = GetMember(pad, "dpad");
            if (dpad != null)
            {
                if (IsPressed(GetMember(dpad, "up"))) return true;
                if (IsPressed(GetMember(dpad, "right"))) return true;
                if (IsPressed(GetMember(dpad, "down"))) return true;
                if (IsPressed(GetMember(dpad, "left"))) return true;
            }
            return false;
        }

        private static object? GetGamepadCurrent()
        {
            try
            {
                var t = Type.GetType("UnityEngine.InputSystem.Gamepad, Unity.InputSystem");
                return t?.GetProperty("current",
                    BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
            }
            catch { return null; }
        }

        private static object? GetMember(object obj, string name)
        {
            try
            {
                return obj.GetType().GetProperty(name,
                    BindingFlags.Public | BindingFlags.Instance)?.GetValue(obj);
            }
            catch { return null; }
        }

        private static bool IsPressed(object? control)
        {
            if (control == null) return false;
            try
            {
                var p = control.GetType().GetProperty("isPressed",
                    BindingFlags.Public | BindingFlags.Instance);
                return p?.GetValue(control) is bool b && b;
            }
            catch { return false; }
        }

        private static float ReadFloat(object? control)
        {
            if (control == null) return 0f;
            try
            {
                var read = control.GetType().GetMethod("ReadValue", Type.EmptyTypes);
                return read?.Invoke(control, null) is float f ? f : 0f;
            }
            catch { return 0f; }
        }
    }

    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_GetAnyButton
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            return t?.GetMethod("GetAnyButton", Type.EmptyTypes);
        }

        [HarmonyPostfix]
        static void Postfix(ref bool __result)
        {
            if (__result) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            if (AnyButtonBridge.IsAnyInputSystemButtonPressed())
                __result = true;
        }
    }

    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_GetAnyButtonDown
    {
        private static readonly BridgeEdgeState.Edge _edge = new BridgeEdgeState.Edge();

        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            return t?.GetMethod("GetAnyButtonDown", Type.EmptyTypes);
        }

        [HarmonyPostfix]
        static void Postfix(ref bool __result)
        {
            if (__result) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            bool now = AnyButtonBridge.IsAnyInputSystemButtonPressed();
            BridgeEdgeState.SampleOncePerFrame(_edge, now);
            if (BridgeEdgeState.IsDownThisFrame(_edge)) __result = true;
        }
    }

    // ─── One-time dump of every Rewired action so we can see exactly which
    //     action names exist (e.g. "Recover", "GetUp", "Stand"). Triggered
    //     once on the child instance the first time a Player.GetButton call
    //     is patched. ────────────────────────────────────────────────────────
    [HarmonyPatch]
    internal static class Patch_RewiredPlayer_DumpActionsOnce
    {
        private static bool _dumped;

        static MethodBase? TargetMethod()
        {
            // Piggyback on GetButton(string) — guaranteed to be hit early.
            var t = PatchHelpers.SafeTypeByName("Rewired.Player");
            return t?.GetMethod("GetButton", new[] { typeof(string) });
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            if (_dumped) return;
            if (!InputSystemBridge.IsActiveOnThisInstance) return;
            _dumped = true;
            try
            {
                var reInputType = PatchHelpers.SafeTypeByName("Rewired.ReInput");
                var mappingProp = reInputType?.GetProperty("mapping",
                    BindingFlags.Public | BindingFlags.Static);
                var mapping = mappingProp?.GetValue(null);
                if (mapping == null) return;

                var actionsProp = mapping.GetType().GetProperty("Actions",
                    BindingFlags.Public | BindingFlags.Instance);
                var actions = actionsProp?.GetValue(mapping);
                if (actions == null) return;

                // Il2Cpp lists don't implement BCL IEnumerable. Iterate by
                // Count + indexer (get_Item) reflectively.
                var listType = actions.GetType();
                var countProp = listType.GetProperty("Count",
                    BindingFlags.Public | BindingFlags.Instance);
                if (countProp?.GetValue(actions) is not int count) return;

                var indexer = listType.GetMethod("get_Item",
                    BindingFlags.Public | BindingFlags.Instance,
                    null, new[] { typeof(int) }, null);
                if (indexer == null) return;

                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"[InputSystemBridge] Rewired action list ({count} actions, child instance):");
                for (int i = 0; i < count; i++)
                {
                    object? item = null;
                    try { item = indexer.Invoke(actions, new object[] { i }); }
                    catch { continue; }
                    if (item == null) continue;

                    string? name = null;
                    int id = 0;
                    string? type = null;
                    try
                    {
                        name = item.GetType().GetProperty("name",
                            BindingFlags.Public | BindingFlags.Instance)?.GetValue(item) as string;
                        var idVal = item.GetType().GetProperty("id",
                            BindingFlags.Public | BindingFlags.Instance)?.GetValue(item);
                        if (idVal != null) id = Convert.ToInt32(idVal);
                        type = item.GetType().GetProperty("type",
                            BindingFlags.Public | BindingFlags.Instance)?.GetValue(item)?.ToString();
                    }
                    catch { }
                    sb.AppendLine($"  [{id}] {name} ({type})");
                }
                Plugin.Log.LogInfo(sb.ToString());
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[InputSystemBridge] Action-dump failed: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
