using UnityEngine;

namespace SledCoopMod
{
    /// <summary>
    /// Decides whether P1's joystick/gamepad input should be suppressed because
    /// a mod-managed extra player slot has claimed that gamepad.
    /// </summary>
    internal static class InputBlockHelper
    {
        private static readonly KeyCode[] HostHandActionKeys =
        {
            KeyCode.G,
            KeyCode.E,
            KeyCode.Q,
            KeyCode.R,
            KeyCode.T,
            KeyCode.H,
            KeyCode.F,
            KeyCode.C,
            KeyCode.V,
            KeyCode.B,
            KeyCode.Tab,
            KeyCode.Space,
            KeyCode.LeftControl,
            KeyCode.RightControl,
            KeyCode.LeftShift,
            KeyCode.RightShift,
        };

        /// <summary>
        /// Returns true when any extra slot has a gamepad device assigned.
        /// Callers should substitute keyboard-only input rather than zeroing P1's input entirely.
        /// </summary>
        public static bool ShouldBlockJoystickForHost()
        {
            if (NetworkedInstanceManager.Instance?.IsChildClient == true)
                return false;

            if (LocalPlayerManager.Instance == null) return false;
            foreach (var slot in LocalPlayerManager.Instance.ActiveSlots)
            {
                if (slot.SlotIndex > 0 && slot.LocalInputProvider != null)
                {
                    var dev = slot.LocalInputProvider.Device;
                    if (dev >= AssignedInputDevice.Gamepad0 && dev <= AssignedInputDevice.Gamepad3)
                        return true;
                }
            }
            return false;
        }

        public static bool TryGetNetworkedChildMoveInput(out Vector2 result)
        {
            result = Vector2.zero;
            return NetworkedInstanceManager.Instance?.TryGetAssignedMoveInput(out result) == true;
        }

        public static bool TryGetNetworkedChildLookInput(out Vector2 result)
        {
            result = Vector2.zero;
            return NetworkedInstanceManager.Instance?.TryGetAssignedLookInput(out result) == true;
        }

        public static bool TryGetNetworkedChildButtonDown(string action, out bool result)
        {
            result = false;
            return NetworkedInstanceManager.Instance?.TryGetAssignedButtonDown(action, out result) == true;
        }

        /// <summary>
        /// WASD keyboard movement for P1 — substituted into PlayerLocalInput.GetMoveInput
        /// when a gamepad is claimed by an extra slot, so P1's keyboard still works.
        /// </summary>
        public static Vector2 GetHostKeyboardMoveInput() =>
            new Vector2(KeyAxis(KeyCode.A, KeyCode.D), KeyAxis(KeyCode.S, KeyCode.W));

        /// <summary>
        /// Mouse-look for P1 — substituted into PlayerLocalInput.GetLookInput
        /// when a gamepad is claimed by an extra slot.
        /// </summary>
        public static Vector2 GetHostKeyboardLookInput() =>
            new Vector2(Input.GetAxis("Mouse X"), Input.GetAxis("Mouse Y"));

        public static bool ShouldSuppressHostHandAction()
        {
            if (!ShouldBlockJoystickForHost()) return false;
            return !IsHostMouseKeyboardHandActionActive();
        }

        private static bool IsHostMouseKeyboardHandActionActive()
        {
            return Input.GetMouseButton(0)
                || Input.GetMouseButtonDown(0)
                || Input.GetMouseButtonUp(0)
                || Input.GetMouseButton(1)
                || Input.GetMouseButtonDown(1)
                || Input.GetMouseButtonUp(1)
                || IsAnyHostHandActionKeyActive();
        }

        private static bool IsAnyHostHandActionKeyActive()
        {
            foreach (var key in HostHandActionKeys)
            {
                if (Input.GetKey(key) || Input.GetKeyDown(key) || Input.GetKeyUp(key))
                    return true;
            }

            return false;
        }

        private static float KeyAxis(KeyCode neg, KeyCode pos) =>
            (Input.GetKey(pos) ? 1f : 0f) - (Input.GetKey(neg) ? 1f : 0f);
    }
}
