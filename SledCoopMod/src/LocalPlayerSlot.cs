using UnityEngine;

namespace SledCoopMod
{
    /// <summary>
    /// Immutable-identity data for one local player seat (slot 0–3).
    /// Mutable state (pawn, camera, HUD) is filled in as the game session progresses.
    /// </summary>
    public class LocalPlayerSlot
    {
        // ── Identity ───────────────────────────────────────────────────────────────
        /// <summary>0-based seat index. Player 1 = slot 0.</summary>
        public int SlotIndex { get; }

        /// <summary>
        /// Guest/profile name used by networked child instances. Slot 0 uses the
        /// host game's native profile and normally leaves this blank.
        /// </summary>
        public string ProfileName { get; set; } = "";

        // ── Input ──────────────────────────────────────────────────────────────────
        /// <summary>
        /// Rewired player ID assigned to this slot, or -1 if using Unity Input System only.
        /// Set during device-join flow.
        /// </summary>
        public int RewiredPlayerId { get; set; } = -1;

        /// <summary>
        /// Unity Input System PlayerInput device index, or -1 if using Rewired only.
        /// </summary>
        public int InputSystemDeviceId { get; set; } = -1;

        /// <summary>
        /// Per-slot input provider created when the slot joins.  Null for slot 0
        /// (host uses the game's own Rewired/PlayerLocalInput system).
        /// </summary>
        public LocalInputProvider? LocalInputProvider { get; set; }

        // ── Network / pawn ─────────────────────────────────────────────────────────
        /// <summary>
        /// The spawned player pawn GameObject owned by this slot.
        /// Null until the spawn pipeline runs.
        /// </summary>
        public GameObject? Pawn { get; set; }

        /// <summary>
        /// FishNet network object ID for the pawn, or -1 if not yet spawned / local-only mode.
        /// </summary>
        public int NetworkObjectId { get; set; } = -1;

        // ── Camera ─────────────────────────────────────────────────────────────────
        /// <summary>
        /// Dedicated camera (or Cinemachine brain camera) for this slot.
        /// Null until CameraLayoutManager assigns it.
        /// </summary>
        public Camera? ViewCamera { get; set; }

        /// <summary>
        /// Viewport rect applied to <see cref="ViewCamera"/>.
        /// Updated by CameraLayoutManager whenever player count changes.
        /// </summary>
        public Rect ViewportRect { get; set; } = new Rect(0, 0, 1, 1);

        /// <summary>
        /// Target display index (0 = primary). Used only when multi-display is enabled.
        /// </summary>
        public int TargetDisplay { get; set; } = 0;

        // ── HUD ────────────────────────────────────────────────────────────────────
        /// <summary>Root HUD canvas object for this slot. Null until HudManager assigns it.</summary>
        public GameObject? HudRoot { get; set; }

        // ── Lifecycle ──────────────────────────────────────────────────────────────
        /// <summary>True once the device join flow has completed for this slot.</summary>
        public bool IsJoined { get; set; }

        /// <summary>True while the session is active and this slot has a live pawn.</summary>
        public bool IsActive => IsJoined && Pawn != null;

        public LocalPlayerSlot(int slotIndex)
        {
            SlotIndex = slotIndex;
        }

        public override string ToString() =>
            $"Slot{SlotIndex}[joined={IsJoined} pawn={Pawn?.name ?? "none"} " +
            $"rewired={RewiredPlayerId} display={TargetDisplay} profile={ProfileName}]";
    }
}
