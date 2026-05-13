using System;
using System.Collections.Generic;
using BepInEx.Configuration;
using Rewired;

namespace SledCoopMod
{
    // Per-slot Rewired joystick assignments. Reads/writes the four
    // ModConfig.SlotControllerBindingN ConfigEntries, exposes a
    // capture state machine for the in-game bind UI, and provides
    // joystick enumeration that ModSettingsUi renders + the
    // NetworkedRewiredIsolationManager honors at runtime.
    internal static class ControllerBindings
    {
        public const int MaxSlots = 4;
        public const string KeyboardMouseId = "Keyboard+Mouse";

        public static bool IsKeyboardMouse(string id) =>
            string.Equals(id, KeyboardMouseId, StringComparison.OrdinalIgnoreCase);

        // Renders the bind table with IMGUI from any host (LocalCoopUI's
        // overlay or ModSettingsUi's modal). Returns once. Call from OnGUI.
        public static void DrawBindTableIMGUI(
            UnityEngine.GUIStyle labelStyle,
            UnityEngine.GUIStyle buttonStyle)
        {
            int max = UnityEngine.Mathf.Clamp(ModConfig.MaxLocalPlayers.Value, 1, MaxSlots);
            for (int slot = 0; slot < max; slot++)
            {
                UnityEngine.GUILayout.BeginHorizontal();
                UnityEngine.GUILayout.Label($"Slot {slot}", labelStyle, UnityEngine.GUILayout.Width(70f));

                bool armed = CapturingSlot == slot;
                string status = armed
                    ? "Press any key/button to bind…"
                    : DescribeBinding(slot);
                UnityEngine.GUILayout.Label(status, labelStyle, UnityEngine.GUILayout.Width(220f));

                UnityEngine.GUILayout.FlexibleSpace();

                if (armed)
                {
                    if (UnityEngine.GUILayout.Button("Cancel", buttonStyle, UnityEngine.GUILayout.Width(72f)))
                        CancelCapture();
                }
                else
                {
                    if (UnityEngine.GUILayout.Button("Bind", buttonStyle, UnityEngine.GUILayout.Width(72f)))
                        StartCapture(slot);
                }

                if (!armed)
                {
                    if (UnityEngine.GUILayout.Button("KB+M", buttonStyle, UnityEngine.GUILayout.Width(64f)))
                        SetBinding(slot, KeyboardMouseId);
                    if (!string.IsNullOrEmpty(GetBinding(slot)))
                    {
                        if (UnityEngine.GUILayout.Button("Clear", buttonStyle, UnityEngine.GUILayout.Width(56f)))
                            ClearBinding(slot);
                    }
                }

                UnityEngine.GUILayout.EndHorizontal();
            }
        }

        // The slot currently waiting for the next joystick button-press. -1
        // means no capture in progress. Set by ModSettingsUi when the user
        // clicks "Detect & Bind"; cleared automatically when capture lands or
        // is cancelled.
        public static int CapturingSlot { get; private set; } = -1;
        public static double CaptureExpiresAtRealtime { get; private set; }

        public static bool IsCapturing => CapturingSlot >= 0;

        public static void StartCapture(int slot, float timeoutSeconds = 8f)
        {
            if (slot < 0 || slot >= MaxSlots) return;
            CapturingSlot = slot;
            CaptureExpiresAtRealtime = UnityEngine.Time.realtimeSinceStartupAsDouble + timeoutSeconds;
            Plugin.Log.LogInfo($"[ControllerBindings] Capture armed for slot {slot}; press any button on the controller…");
        }

        public static void CancelCapture()
        {
            if (CapturingSlot < 0) return;
            Plugin.Log.LogInfo($"[ControllerBindings] Capture for slot {CapturingSlot} cancelled.");
            CapturingSlot = -1;
        }

        // Polled by ModSettingsUi.Update while capture is armed. Returns true
        // when a joystick button was captured (and the binding written), or
        // when the capture timed out (also clears state).
        public static bool TickCapture()
        {
            if (CapturingSlot < 0) return false;

            if (UnityEngine.Time.realtimeSinceStartupAsDouble > CaptureExpiresAtRealtime)
            {
                Plugin.Log.LogInfo($"[ControllerBindings] Capture for slot {CapturingSlot} timed out.");
                CapturingSlot = -1;
                return true;
            }

            try
            {
                if (!ReInput.isReady) return false;

                // First check if any keyboard or mouse input is active —
                // lets the user bind KB+M by tapping any key / clicking
                // while capture is armed.
                if (AnyKeyboardOrMouseActive())
                {
                    SetBinding(CapturingSlot, KeyboardMouseId);
                    Plugin.Log.LogInfo($"[ControllerBindings] Slot {CapturingSlot} bound to Keyboard+Mouse.");
                    CapturingSlot = -1;
                    return true;
                }

                foreach (var joy in EnumerateJoysticks())
                {
                    if (joy == null) continue;
                    if (!AnyButtonOrAxisActive(joy)) continue;

                    string id = GetJoystickIdentifier(joy);
                    if (string.IsNullOrEmpty(id)) continue;

                    SetBinding(CapturingSlot, id);
                    Plugin.Log.LogInfo($"[ControllerBindings] Slot {CapturingSlot} bound to '{GetJoystickDisplayName(joy)}' (id={id}).");
                    CapturingSlot = -1;
                    return true;
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[ControllerBindings] TickCapture error: {e.GetType().Name}: {e.Message}");
            }

            return false;
        }

        public static IEnumerable<Joystick> EnumerateJoysticks()
        {
            if (!ReInput.isReady) yield break;
            int count = 0;
            try { count = ReInput.controllers.joystickCount; }
            catch { yield break; }

            for (int i = 0; i < count; i++)
            {
                Joystick? j = null;
                try { j = ReInput.controllers.Joysticks[i]; } catch { }
                if (j != null) yield return j;
            }
        }

        public static string GetJoystickIdentifier(Joystick joy)
        {
            try { if (!string.IsNullOrEmpty(joy.hardwareIdentifier)) return joy.hardwareIdentifier; }
            catch { }
            try { return $"id:{joy.id}"; }
            catch { return ""; }
        }

        public static string GetJoystickDisplayName(Joystick joy)
        {
            try { return joy.name ?? $"Joystick {joy.id}"; }
            catch { return "Joystick"; }
        }

        public static string GetBinding(int slot)
        {
            return slot switch
            {
                0 => ModConfig.SlotControllerBinding0.Value,
                1 => ModConfig.SlotControllerBinding1.Value,
                2 => ModConfig.SlotControllerBinding2.Value,
                3 => ModConfig.SlotControllerBinding3.Value,
                _ => "",
            };
        }

        public static void SetBinding(int slot, string identifier)
        {
            switch (slot)
            {
                case 0: ModConfig.SlotControllerBinding0.Value = identifier ?? ""; break;
                case 1: ModConfig.SlotControllerBinding1.Value = identifier ?? ""; break;
                case 2: ModConfig.SlotControllerBinding2.Value = identifier ?? ""; break;
                case 3: ModConfig.SlotControllerBinding3.Value = identifier ?? ""; break;
            }
        }

        public static void ClearBinding(int slot) => SetBinding(slot, "");

        // Resolves the bound joystick for a slot back to a Rewired joystick
        // index (0..3) so NetworkedRewiredIsolationManager can call
        // ContainsController(Joystick, index). Returns -1 if no binding or
        // the bound device is no longer present.
        public static int ResolveBoundJoystickIndex(int slot)
        {
            string id = GetBinding(slot);
            if (string.IsNullOrEmpty(id)) return -1;

            int idx = 0;
            foreach (var joy in EnumerateJoysticks())
            {
                if (string.Equals(GetJoystickIdentifier(joy), id, StringComparison.Ordinal))
                    return idx;
                idx++;
            }
            return -1;
        }

        public static string DescribeBinding(int slot)
        {
            string id = GetBinding(slot);
            if (string.IsNullOrEmpty(id)) return "(unbound)";
            if (IsKeyboardMouse(id)) return "Keyboard + Mouse";

            foreach (var joy in EnumerateJoysticks())
            {
                if (string.Equals(GetJoystickIdentifier(joy), id, StringComparison.Ordinal))
                    return $"{GetJoystickDisplayName(joy)}";
            }
            return $"{id} (not connected)";
        }

        private static bool AnyKeyboardOrMouseActive()
        {
            try
            {
                if (UnityEngine.Input.anyKeyDown) return true;
                if (UnityEngine.Input.GetMouseButton(0)) return true;
                if (UnityEngine.Input.GetMouseButton(1)) return true;
            }
            catch { }
            return false;
        }

        private static bool AnyButtonOrAxisActive(Joystick joy)
        {
            try
            {
                int btnCount = joy.buttonCount;
                for (int b = 0; b < btnCount; b++)
                {
                    if (joy.GetButton(b)) return true;
                }
            }
            catch { }

            try
            {
                int axisCount = joy.axisCount;
                for (int a = 0; a < axisCount; a++)
                {
                    if (Math.Abs(joy.GetAxis(a)) > 0.6f) return true;
                }
            }
            catch { }

            return false;
        }
    }
}
