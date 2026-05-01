using System;
using System.Collections.Generic;
using System.Linq;
using Il2CppInterop.Runtime.Attributes;
using UnityEngine;

namespace SledCoopMod
{
    /// <summary>
    /// Central registry of all active <see cref="LocalPlayerSlot"/> instances.
    /// Lives on the persistent ModBootstrap GameObject; all other managers read from here.
    /// </summary>
    public class LocalPlayerManager : MonoBehaviour
    {
        public static LocalPlayerManager? Instance { get; private set; }

        // Slots are indexed 0–3.  Slot 0 always maps to the original single player.
        private readonly LocalPlayerSlot[] _slots = new LocalPlayerSlot[4];

        // Raised whenever the active slot count changes (join or leave).
        public static event Action? OnActivePlayersChanged;

        /// <summary>Fire the <see cref="OnActivePlayersChanged"/> event from outside this class.</summary>
        public static void RaiseActivePlayersChanged()
        {
            OnActivePlayersChanged?.Invoke();
        }

        // ── Unity lifecycle ────────────────────────────────────────────────────────

        private void Awake()
        {
            if (Instance != null && Instance != this)
            {
                Destroy(gameObject);
                return;
            }
            Instance = this;

            for (int i = 0; i < _slots.Length; i++)
                _slots[i] = new LocalPlayerSlot(i);

            // Slot 0 always starts as joined (original single-player flow).
            _slots[0].IsJoined = true;

            Plugin.Log.LogInfo("LocalPlayerManager initialised. Slot 0 pre-joined.");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        // ── Public API ─────────────────────────────────────────────────────────────

        /// <summary>All four slot objects, including inactive ones.</summary>
        [HideFromIl2Cpp]
        public IReadOnlyList<LocalPlayerSlot> AllSlots => _slots;

        /// <summary>Only currently joined/active slots.</summary>
        [HideFromIl2Cpp]
        public IEnumerable<LocalPlayerSlot> ActiveSlots => _slots.Where(s => s.IsJoined);

        /// <summary>Number of currently joined players.</summary>
        public int ActiveCount => _slots.Count(s => s.IsJoined);

        /// <summary>
        /// Attempt to join the next available slot with the given device IDs.
        /// Returns the slot if successful, null if all slots are full.
        /// </summary>
        [HideFromIl2Cpp]
        public LocalPlayerSlot? TryJoin(int rewiredPlayerId = -1, int inputSystemDeviceId = -1)
        {
            if (ActiveCount >= ModConfig.MaxLocalPlayers.Value)
            {
                Plugin.Log.LogWarning("TryJoin: MaxLocalPlayers reached, ignoring join request.");
                return null;
            }

            LocalPlayerSlot? slot = _slots.FirstOrDefault(s => !s.IsJoined);
            if (slot == null)
            {
                Plugin.Log.LogWarning("TryJoin: No free slots available.");
                return null;
            }

            slot.RewiredPlayerId     = rewiredPlayerId;
            slot.InputSystemDeviceId = inputSystemDeviceId;
            slot.IsJoined            = true;
            if (slot.SlotIndex > 0 && string.IsNullOrWhiteSpace(slot.ProfileName))
                slot.ProfileName = NetworkedGuestProfile.GenerateGuestName();

            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo($"Player joined: {slot}");

            OnActivePlayersChanged?.Invoke();
            return slot;
        }

        /// <summary>
        /// Remove a player from their slot, cleaning up references.
        /// Slot 0 cannot be removed (it is the host/original player).
        /// </summary>
        public bool TryLeave(int slotIndex)
        {
            if (slotIndex == 0)
            {
                Plugin.Log.LogWarning("TryLeave: Slot 0 (host player) cannot leave.");
                return false;
            }

            if (slotIndex < 0 || slotIndex >= _slots.Length)
                return false;

            LocalPlayerSlot slot = _slots[slotIndex];
            if (!slot.IsJoined) return false;

            slot.IsJoined            = false;
            slot.RewiredPlayerId     = -1;
            slot.InputSystemDeviceId = -1;
            slot.LocalInputProvider  = null;
            slot.Pawn                = null;
            slot.ViewCamera          = null;
            slot.HudRoot             = null;
            slot.NetworkObjectId     = -1;
            slot.ProfileName         = "";

            if (ModConfig.VerboseLogging.Value)
                Plugin.Log.LogInfo($"Player left slot {slotIndex}.");

            OnActivePlayersChanged?.Invoke();
            return true;
        }

        /// <summary>Find the slot that owns a given pawn GameObject.</summary>
        [HideFromIl2Cpp]
        public LocalPlayerSlot? GetSlotForPawn(GameObject pawn) =>
            Array.Find(_slots, s => s.Pawn == pawn);

        /// <summary>Find the slot that owns a given Rewired player ID.</summary>
        [HideFromIl2Cpp]
        public LocalPlayerSlot? GetSlotForRewiredPlayer(int rewiredId) =>
            Array.Find(_slots, s => s.RewiredPlayerId == rewiredId);

        /// <summary>Get the slot for a specific index (0-3), or null if out of range.</summary>
        [HideFromIl2Cpp]
        public LocalPlayerSlot? GetSlot(int index) =>
            (index >= 0 && index < _slots.Length) ? _slots[index] : null;

        public void SetProfileName(int slotIndex, string? profileName)
        {
            if (slotIndex <= 0 || slotIndex >= _slots.Length) return;
            _slots[slotIndex].ProfileName = NetworkedGuestProfile.Sanitize(profileName);
        }

        /// <summary>
        /// Reset per-session state on all slots (called when returning to menu or
        /// starting a new match).  Preserves slot-0 joined status; clears pawn/camera
        /// references on all slots and unjoinss slots 1-3.
        /// </summary>
        public void ResetForNewSession()
        {
            // Notify scene watcher first so it can clean up cameras before we wipe refs.
            SceneWatcher.NotifyGameplayEnded();

            ClearSessionPawnState();
            OfflineModeManager.ResetOfflineMode();
            Plugin.Log.LogInfo("LocalPlayerManager: session reset.");
            OnActivePlayersChanged?.Invoke();
        }

        public void ClearSessionPawnState()
        {
            for (int i = 0; i < _slots.Length; i++)
            {
                _slots[i].Pawn          = null;
                _slots[i].ViewCamera    = null;
                _slots[i].HudRoot       = null;
                _slots[i].NetworkObjectId = -1;
                _slots[i].ViewportRect  = new UnityEngine.Rect(0, 0, 1, 1);

                if (i > 0)
                {
                    _slots[i].IsJoined            = false;
                    _slots[i].RewiredPlayerId     = -1;
                    _slots[i].InputSystemDeviceId = -1;
                    _slots[i].ProfileName         = "";
                }
            }
        }

        // ── Private helpers ────────────────────────────────────────────────────────

        [HideFromIl2Cpp]
        private LocalPlayerSlot[] GetActiveArray() =>
            _slots.Where(s => s.IsJoined).ToArray();
    }
}
