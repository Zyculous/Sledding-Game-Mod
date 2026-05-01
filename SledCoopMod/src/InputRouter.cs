using UnityEngine;

namespace SledCoopMod
{
    /// <summary>
    /// Manages manual player-slot join/leave triggered by the IMGUI overlay buttons.
    ///
    /// Phase 2 note: device binding (InputSystem / Rewired) cannot be wired here yet
    /// because both InputSystem.devices (ReadOnlyArray value-type) and InputUser
    /// cause IL2CPP trampoline type-load failures. That work is deferred until a
    /// working IL2CPP-safe device enumeration path is confirmed.
    /// </summary>
    public class InputRouter : MonoBehaviour
    {
        public static InputRouter? Instance { get; private set; }

        // ── Unity lifecycle ────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ── Public API (called from LocalCoopUI IMGUI buttons) ────────────────────

        /// <summary>
        /// Join <paramref name="slotIndex"/> (1–3). Slot 0 is always the host.
        /// Returns true on success.
        /// </summary>
        public bool JoinSlot(int slotIndex, string? profileName = null)
        {
            if (slotIndex <= 0 || slotIndex > 3) return false;
            if (LocalPlayerManager.Instance == null) return false;

            LocalPlayerSlot? slot = LocalPlayerManager.Instance.GetSlot(slotIndex);
            if (slot == null || slot.IsJoined) return false;

            slot.IsJoined = true;
            slot.ProfileName = NetworkedGuestProfile.Resolve(profileName);

            // Create an input provider and auto-assign a gamepad so the slot is
            // immediately usable.  The first extra slot gets Gamepad0, second gets
            // Gamepad1, etc.  The user can override via the mod settings overlay.
            if (slot.LocalInputProvider == null)
                slot.LocalInputProvider = new LocalInputProvider();

            if (slot.LocalInputProvider.Device == AssignedInputDevice.None)
            {
                // Map slot 1 → Gamepad0, slot 2 → Gamepad1, slot 3 → Gamepad2.
                // If no gamepad is available, fallback to a second keyboard layout.
                int gpIndex = slotIndex - 1;
                if (gpIndex >= 0 && gpIndex <= 3 && LocalInputProvider.IsGamepadAvailable(gpIndex))
                {
                    slot.LocalInputProvider.Device =
                        (AssignedInputDevice)((int)AssignedInputDevice.Gamepad0 + gpIndex);
                }
                else
                {
                    slot.LocalInputProvider.Device = AssignedInputDevice.KeyboardArrows;
                    Plugin.Log.LogInfo($"[InputRouter] No gamepad found for slot {slotIndex}, falling back to KeyboardArrows.");
                }
            }

            LocalPlayerManager.RaiseActivePlayersChanged();

            if (NetworkedInstanceManager.TryEnsureConfiguredChildForSlot(slotIndex))
            {
            }

            LocalCoopUI.Instance?.FlashJoin(slotIndex);
            Plugin.Log.LogInfo($"[InputRouter] P{slotIndex + 1} joined slot {slotIndex}.");
            return true;
        }

        /// <summary>
        /// Leave <paramref name="slotIndex"/> (1–3).
        /// Returns true on success.
        /// </summary>
        public bool LeaveSlot(int slotIndex)
        {
            if (slotIndex <= 0) return false;
            if (LocalPlayerManager.Instance == null) return false;

            LocalPlayerSlot? slot = LocalPlayerManager.Instance.GetSlot(slotIndex);
            if (NetworkedInstanceManager.TryStopConfiguredChildForSlot(slotIndex))
            {
            }

            bool ok = LocalPlayerManager.Instance.TryLeave(slotIndex);
            if (ok)
                Plugin.Log.LogInfo($"[InputRouter] P{slotIndex + 1} left slot {slotIndex}.");
            return ok;
        }
    }
}
