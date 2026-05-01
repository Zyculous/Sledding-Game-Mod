using System.Reflection;
using UnityEngine;

namespace SledCoopMod
{
    /// <summary>
    /// Which physical device is locked to a local player slot.
    /// </summary>
    public enum AssignedInputDevice
    {
        None           = 0,
        KeyboardWASD   = 1,   // WASD move, Mouse look, Space jump, E interact, Tab inventory
        KeyboardArrows = 2,   // Arrow move, Numpad look, RShift jump, NumEnter interact, Num0 inventory
        Gamepad0       = 3,   // Gamepad index 0 (first connected)
        Gamepad1       = 4,   // Gamepad index 1
        Gamepad2       = 5,   // Gamepad index 2
        Gamepad3       = 6,   // Gamepad index 3
    }

    /// <summary>
    /// Per-slot input wrapper. Uses Unity legacy Input for keyboard (fully IL2CPP-safe)
    /// and UnityEngine.InputSystem for gamepads (try/catch on every call for safety).
    ///
    /// Default bindings:
    ///   WASD    – move WASD, look Mouse, jump Space, interact E, inventory Tab, sprint LShift
    ///   Arrows  – move Arrows, look Numpad4/6/8/2, jump RShift, interact NumpadEnter,
    ///             inventory Numpad0, sprint RAlt
    ///   Gamepad – left stick move, right stick look, A/Cross jump, B/Circle interact,
    ///             L3 sled, Select/Back inventory, LB sprint
    /// </summary>
    public class LocalInputProvider
    {
        public AssignedInputDevice Device { get; set; } = AssignedInputDevice.None;

        // ── Move / Look ────────────────────────────────────────────────────────────

        public Vector2 GetMoveInput()
        {
            switch (Device)
            {
                case AssignedInputDevice.KeyboardWASD:
                    return new Vector2(
                        KeyAxis(KeyCode.A, KeyCode.D),
                        KeyAxis(KeyCode.S, KeyCode.W));

                case AssignedInputDevice.KeyboardArrows:
                    return new Vector2(
                        KeyAxis(KeyCode.LeftArrow, KeyCode.RightArrow),
                        KeyAxis(KeyCode.DownArrow,  KeyCode.UpArrow));

                case AssignedInputDevice.Gamepad0:
                case AssignedInputDevice.Gamepad1:
                case AssignedInputDevice.Gamepad2:
                case AssignedInputDevice.Gamepad3:
                    return GamepadLeftStick((int)Device - (int)AssignedInputDevice.Gamepad0);

                default:
                    return Vector2.zero;
            }
        }

        public Vector2 GetLookInput()
        {
            switch (Device)
            {
                case AssignedInputDevice.KeyboardWASD:
                    return new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));

                case AssignedInputDevice.KeyboardArrows:
                    return new Vector2(
                        KeyAxis(KeyCode.Keypad4, KeyCode.Keypad6),
                        KeyAxis(KeyCode.Keypad2, KeyCode.Keypad8));

                case AssignedInputDevice.Gamepad0:
                case AssignedInputDevice.Gamepad1:
                case AssignedInputDevice.Gamepad2:
                case AssignedInputDevice.Gamepad3:
                    return GamepadRightStick((int)Device - (int)AssignedInputDevice.Gamepad0);

                default:
                    return Vector2.zero;
            }
        }

        // ── Buttons ────────────────────────────────────────────────────────────────

        public bool GetButtonDown(string action)
        {
            switch (Device)
            {
                case AssignedInputDevice.KeyboardWASD:
                    return action switch
                    {
                        "Interact"       => Input.GetKeyDown(KeyCode.E),
                        "Inventory"      => Input.GetKeyDown(KeyCode.Tab),
                        "Jump"           => Input.GetKeyDown(KeyCode.Space),
                        "Sled"           => Input.GetKeyDown(KeyCode.F),
                        "Snowball"       => Input.GetKeyDown(KeyCode.G),
                        "Build"          => Input.GetKeyDown(KeyCode.H),
                        "PlaceMarker"    => Input.GetKeyDown(KeyCode.R),
                        "TeleportMarker" => Input.GetKeyDown(KeyCode.T),
                        "Pause"          => Input.GetKeyDown(KeyCode.P),
                        _                => false,
                    };

                case AssignedInputDevice.KeyboardArrows:
                    return action switch
                    {
                        "Interact"       => Input.GetKeyDown(KeyCode.KeypadEnter),
                        "Inventory"      => Input.GetKeyDown(KeyCode.Keypad0),
                        "Jump"           => Input.GetKeyDown(KeyCode.RightShift),
                        "Sled"           => Input.GetKeyDown(KeyCode.Keypad5),
                        "Snowball"       => Input.GetKeyDown(KeyCode.Keypad7),
                        "Build"          => Input.GetKeyDown(KeyCode.Keypad9),
                        "PlaceMarker"    => Input.GetKeyDown(KeyCode.Keypad3),
                        "TeleportMarker" => Input.GetKeyDown(KeyCode.Keypad1),
                        "Pause"          => Input.GetKeyDown(KeyCode.KeypadMultiply),
                        _                => false,
                    };

                case AssignedInputDevice.Gamepad0:
                case AssignedInputDevice.Gamepad1:
                case AssignedInputDevice.Gamepad2:
                case AssignedInputDevice.Gamepad3:
                {
                    int g = (int)Device - (int)AssignedInputDevice.Gamepad0;
                    return action switch
                    {
                        // Unity KeyCode: Joystick(N+1)Button(B) = 350 + N*20 + B
                        "Jump"           => JoystickButtonDown(g, 0),  // A / Cross
                        "Interact"       => JoystickButtonDown(g, 1),  // B / Circle
                        "Snowball"       => JoystickButtonDown(g, 2),  // X / Square
                        "Build"          => JoystickButtonDown(g, 3),  // Y / Triangle
                        "Sled"           => JoystickButtonDown(g, 8),  // L3
                        "Inventory"      => JoystickButtonDown(g, 6),  // Select / Back
                        "PlaceMarker"    => JoystickButtonDown(g, 9),  // R3
                        "TeleportMarker" => JoystickButtonDown(g, 5),  // RB / R1
                        "Pause"          => JoystickButtonDown(g, 7),  // Start / Menu / Options
                        _                => false,
                    };
                }

                default:
                    return false;
            }
        }

        public bool GetButtonUp(string action)
        {
            switch (Device)
            {
                case AssignedInputDevice.KeyboardWASD:
                    return action switch
                    {
                        "Interact"       => Input.GetKeyUp(KeyCode.E),
                        "Jump"           => Input.GetKeyUp(KeyCode.Space),
                        "Sled"           => Input.GetKeyUp(KeyCode.F),
                        "Snowball"       => Input.GetKeyUp(KeyCode.G),
                        "Build"          => Input.GetKeyUp(KeyCode.H),
                        "PlaceMarker"    => Input.GetKeyUp(KeyCode.R),
                        "TeleportMarker" => Input.GetKeyUp(KeyCode.T),
                        _                => false,
                    };

                case AssignedInputDevice.KeyboardArrows:
                    return action switch
                    {
                        "Interact"       => Input.GetKeyUp(KeyCode.KeypadEnter),
                        "Jump"           => Input.GetKeyUp(KeyCode.RightShift),
                        "Sled"           => Input.GetKeyUp(KeyCode.Keypad5),
                        "Snowball"       => Input.GetKeyUp(KeyCode.Keypad7),
                        "Build"          => Input.GetKeyUp(KeyCode.Keypad9),
                        "PlaceMarker"    => Input.GetKeyUp(KeyCode.Keypad3),
                        "TeleportMarker" => Input.GetKeyUp(KeyCode.Keypad1),
                        _                => false,
                    };

                case AssignedInputDevice.Gamepad0:
                case AssignedInputDevice.Gamepad1:
                case AssignedInputDevice.Gamepad2:
                case AssignedInputDevice.Gamepad3:
                {
                    int g = (int)Device - (int)AssignedInputDevice.Gamepad0;
                    return action switch
                    {
                        "Jump"           => JoystickButtonUp(g, 0),
                        "Interact"       => JoystickButtonUp(g, 1),
                        "Snowball"       => JoystickButtonUp(g, 2),
                        "Build"          => JoystickButtonUp(g, 3),
                        "Sled"           => JoystickButtonUp(g, 8),
                        "PlaceMarker"    => JoystickButtonUp(g, 9),
                        "TeleportMarker" => JoystickButtonUp(g, 5),
                        _                => false,
                    };
                }

                default:
                    return false;
            }
        }

        public bool GetButton(string action)
        {
            switch (Device)
            {
                case AssignedInputDevice.KeyboardWASD:
                    return action switch
                    {
                        "Sprint"         => Input.GetKey(KeyCode.LeftShift),
                        "Snowball"       => Input.GetKey(KeyCode.G),
                        "PlaceMarker"    => Input.GetKey(KeyCode.R),
                        "TeleportMarker" => Input.GetKey(KeyCode.T),
                        _                => false,
                    };
                case AssignedInputDevice.KeyboardArrows:
                    return action switch
                    {
                        "Sprint"         => Input.GetKey(KeyCode.RightAlt),
                        "Snowball"       => Input.GetKey(KeyCode.Keypad7),
                        "PlaceMarker"    => Input.GetKey(KeyCode.Keypad3),
                        "TeleportMarker" => Input.GetKey(KeyCode.Keypad1),
                        _                => false,
                    };
                case AssignedInputDevice.Gamepad0:
                case AssignedInputDevice.Gamepad1:
                case AssignedInputDevice.Gamepad2:
                case AssignedInputDevice.Gamepad3:
                {
                    int g = (int)Device - (int)AssignedInputDevice.Gamepad0;
                    return action switch
                    {
                        "Sprint"         => JoystickButton(g, 4),  // LB
                        "Snowball"       => JoystickButton(g, 2),  // X / Square
                        "PlaceMarker"    => JoystickButton(g, 9),  // R3
                        "TeleportMarker" => JoystickButton(g, 5),  // RB / R1
                        _                => false,
                    };
                }
                default:
                    return false;
            }
        }

        // ── Static helpers ─────────────────────────────────────────────────────────

        private static float KeyAxis(KeyCode neg, KeyCode pos) =>
            (Input.GetKey(pos) ? 1f : 0f) - (Input.GetKey(neg) ? 1f : 0f);

        private static Vector2 GamepadLeftStick(int gpIdx)
        {
            try
            {
                var gp = GetGamepadByIndex(gpIdx);
                if (gp != null) return gp.leftStick.ReadValue();
            }
            catch { }
            return Vector2.zero;
        }

        private static Vector2 GamepadRightStick(int gpIdx)
        {
            try
            {
                var gp = GetGamepadByIndex(gpIdx);
                if (gp != null) return gp.rightStick.ReadValue();
            }
            catch { }
            return Vector2.zero;
        }

        public static bool IsGamepadAvailable(int gpIdx)
        {
            try
            {
                return GetGamepadByIndex(gpIdx) != null;
            }
            catch { return false; }
        }

        /// <summary>
        /// Reflection-based accessor for gamepad index > 0 that avoids directly referencing
        /// Gamepad.all (whose ReadOnlyArray&lt;T&gt; return type causes TypeLoadException in IL2CPP).
        /// </summary>
        private static UnityEngine.InputSystem.Gamepad? GetGamepadByIndex(int gpIdx)
        {
            try
            {
                PropertyInfo? allProp = typeof(UnityEngine.InputSystem.Gamepad)
                    .GetProperty("all", BindingFlags.Public | BindingFlags.Static);
                object? list = allProp?.GetValue(null);
                if (list == null) return null;
                PropertyInfo? countProp = list.GetType().GetProperty("Count");
                object? countValue = countProp?.GetValue(list);
                int count = countValue is int countInt ? countInt : 0;
                if (gpIdx >= count) return null;
                MethodInfo? getter = list.GetType().GetMethod("get_Item");
                return getter?.Invoke(list, new object[] { gpIdx }) as UnityEngine.InputSystem.Gamepad;
            }
            catch { return null; }
        }

        /// <summary>
        /// Unity legacy Input maps joystick-specific buttons as:
        ///   Joystick(N+1)Button(B) = KeyCode 350 + N*20 + B
        /// where N is 0-based gamepad index, B is 0-based button number.
        /// </summary>
        private static bool JoystickButtonDown(int gpIdx, int btn)
        {
            try { return Input.GetKeyDown((KeyCode)(350 + gpIdx * 20 + btn)); }
            catch { return false; }
        }

        private static bool JoystickButton(int gpIdx, int btn)
        {
            try { return Input.GetKey((KeyCode)(350 + gpIdx * 20 + btn)); }
            catch { return false; }
        }

        private static bool JoystickButtonUp(int gpIdx, int btn)
        {
            try { return Input.GetKeyUp((KeyCode)(350 + gpIdx * 20 + btn)); }
            catch { return false; }
        }

        // ── Label helpers ──────────────────────────────────────────────────────────

        public static string DeviceLabel(AssignedInputDevice d) => d switch
        {
            AssignedInputDevice.None           => "None",
            AssignedInputDevice.KeyboardWASD   => "KB: WASD+Mouse",
            AssignedInputDevice.KeyboardArrows => "KB: Arrows+Numpad",
            AssignedInputDevice.Gamepad0       => "Gamepad 1",
            AssignedInputDevice.Gamepad1       => "Gamepad 2",
            AssignedInputDevice.Gamepad2       => "Gamepad 3",
            AssignedInputDevice.Gamepad3       => "Gamepad 4",
            _                                  => d.ToString(),
        };
    }
}
