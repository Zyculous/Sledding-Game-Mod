using System;
using Rewired;
using UnityEngine;

namespace SledCoopMod
{
    public class NetworkedRewiredIsolationManager : MonoBehaviour
    {
        private int _nextApplyFrame;
        private bool _loggedReady;
        private string _lastSummary = "";

        private void Update()
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return;

            if (Time.frameCount < _nextApplyFrame)
                return;

            _nextApplyFrame = Time.frameCount + 60;
            ApplyIsolation();
        }

        private void ApplyIsolation()
        {
            try
            {
                if (!ReInput.isReady)
                    return;

                var player = ReInput.players.GetPlayer(0);
                if (player == null)
                    return;

                if (NetworkedInstanceManager.Instance?.IsChildClient == true)
                    ApplyChildIsolation(player);
                else
                    ApplyHostIsolation(player);

                if (!_loggedReady)
                {
                    _loggedReady = true;
                    Plugin.Log.LogInfo("[NetworkedRewiredIsolation] Rewired player-0 device isolation active.");
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[NetworkedRewiredIsolation] Apply skipped: {e.GetType().Name}: {e.Message}");
            }
        }

        private void ApplyHostIsolation(Player player)
        {
            for (int i = 0; i <= 3; i++)
            {
                if (!IsGamepadClaimedByChild(i))
                    continue;

                SafeRemove(player, ControllerType.Joystick, i);
            }

            LogSummary("host: keyboard/mouse kept, child gamepads removed");
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

        private void LogSummary(string summary)
        {
            if (summary == _lastSummary)
                return;

            _lastSummary = summary;
            Plugin.Log.LogInfo($"[NetworkedRewiredIsolation] {summary}.");
        }
    }
}
