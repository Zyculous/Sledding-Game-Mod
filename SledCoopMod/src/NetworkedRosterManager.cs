using System;
using System.Collections.Generic;
using System.Reflection;
using SledCoopMod.Patches;
using UnityEngine;

namespace SledCoopMod
{
    public class NetworkedRosterManager : MonoBehaviour
    {
        public static NetworkedRosterManager? Instance { get; private set; }

        private readonly List<PlayerControl> _knownPlayerControls = new List<PlayerControl>();
        private readonly HashSet<string> _logged = new HashSet<string>(StringComparer.Ordinal);
        private int _lastLogFrame;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(gameObject); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
        }

        private void Update()
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return;

            if (Time.frameCount - _lastLogFrame < 600)
                return;

            _lastLogFrame = Time.frameCount;
            LogRosterOnce();
        }

        internal List<NetworkedPlayerDisplayEntry> GetEntries()
        {
            var entries = new List<NetworkedPlayerDisplayEntry>();
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return entries;

            int localSlot = Math.Max(0, NetworkedInstanceManager.Instance?.SlotIndex ?? 0);
            int count = GetExpectedPlayerCount();
            for (int i = 0; i < count; i++)
            {
                entries.Add(new NetworkedPlayerDisplayEntry(
                    i,
                    GetName(i),
                    i == localSlot,
                    i == 0));
            }

            return entries;
        }

        internal string GetName(int slot)
        {
            slot = Math.Max(0, Math.Min(3, slot));
            if (slot == 0)
            {
                string steamName = GetSteamPersonaName();
                return string.IsNullOrWhiteSpace(steamName) ? "Host" : steamName;
            }

            try
            {
                var localSlot = LocalPlayerManager.Instance?.GetSlot(slot);
                if (!string.IsNullOrWhiteSpace(localSlot?.ProfileName))
                    return localSlot.ProfileName;
            }
            catch { }

            try
            {
                var manager = NetworkedInstanceManager.Instance;
                if (manager != null
                    && manager.SlotIndex == slot
                    && !string.IsNullOrWhiteSpace(manager.ProfileName))
                    return manager.ProfileName;

                if (manager != null
                    && manager.TryGetConfiguredProfileName(slot, out string configured)
                    && !string.IsNullOrWhiteSpace(configured))
                    return configured;
            }
            catch { }

            return $"guest{slot:00}";
        }

        internal bool TryResolveReference(int id, out PlayerReference playerReference)
        {
            playerReference = default;
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured || id < 0)
                return false;

            PlayerControl[] players = GetAllPlayerControls();
            int expected = GetExpectedPlayerCount();
            PlayerControl? player = FindPlayerForId(players, id, expected);
            if (player == null)
                return false;

            int slot = ResolveSlotForPlayer(player, players, expected, id);
            if (slot < 0)
                return false;

            playerReference = CreatePlayerReference(slot, player);
            return true;
        }

        internal bool TryResolveReferenceByString(string? id, out PlayerReference playerReference)
        {
            playerReference = default;
            if (string.IsNullOrWhiteSpace(id))
                return false;

            foreach (string prefix in new[] { "sledcoop-product-", "sledcoop-voice-" })
            {
                if (id.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(id.Substring(prefix.Length), out int slot))
                    return TryResolveReference(slot, out playerReference);
            }

            return false;
        }

        internal bool TryResolvePlayerControl(int id, out PlayerControl? playerControl)
        {
            playerControl = null;
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured || id < 0)
                return false;

            PlayerControl[] players = GetAllPlayerControls();
            playerControl = FindPlayerForId(players, id, GetExpectedPlayerCount());
            return playerControl != null;
        }

        internal void RegisterPlayerControl(PlayerControl? playerControl)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured || playerControl == null)
                return;

            try
            {
                int instanceId = playerControl.GetInstanceID();
                foreach (var known in _knownPlayerControls)
                {
                    if (known == null)
                        continue;

                    if (known == playerControl || known.GetInstanceID() == instanceId)
                        return;
                }

                _knownPlayerControls.Add(playerControl);
            }
            catch { }

            LogRosterOnce();
        }

        internal PlayerReference CreatePlayerReference(int slot, PlayerControl playerControl)
        {
            slot = Math.Max(0, Math.Min(3, slot));
            return new PlayerReference(
                $"sledcoop-product-{slot}",
                9_000_000L + slot,
                slot,
                GetName(slot),
                $"sledcoop-voice-{slot}",
                AuthPlatform.Steam,
                playerControl);
        }

        private int GetExpectedPlayerCount()
        {
            int count = 1;

            try { count = Math.Max(count, NetworkedInstanceManager.Instance?.ConfiguredPlayerCount ?? 1); }
            catch { }

            try { count = Math.Max(count, LocalPlayerManager.Instance?.ActiveCount ?? 1); }
            catch { }

            foreach (var player in GetAllPlayerControls())
            {
                int slot = ResolveSlotForPlayer(player, Array.Empty<PlayerControl>(), 4, -1);
                if (slot >= 0 && slot < 4)
                    count = Math.Max(count, slot + 1);
            }

            return Math.Max(1, Math.Min(4, count));
        }

        private PlayerControl? FindPlayerForId(PlayerControl[] players, int id, int expected)
        {
            foreach (var player in players)
            {
                if (player == null)
                    continue;

                if (GetPlayerOwnerId(player) == id || GetPlayerObjectId(player) == id)
                    return player;
            }

            if (id >= 0 && id < expected)
                return FindPlayerForSlot(players, id);

            return null;
        }

        private PlayerControl? FindPlayerForSlot(PlayerControl[] players, int slot)
        {
            foreach (var player in players)
            {
                if (player == null)
                    continue;

                if (GetPlayerOwnerId(player) == slot)
                    return player;
            }

            var ordered = new List<PlayerControl>();
            foreach (var player in players)
            {
                if (player != null)
                    ordered.Add(player);
            }

            ordered.Sort((a, b) =>
            {
                int ownerCompare = GetPlayerOwnerId(a).CompareTo(GetPlayerOwnerId(b));
                if (ownerCompare != 0)
                    return ownerCompare;

                return GetPlayerObjectId(a).CompareTo(GetPlayerObjectId(b));
            });

            return slot >= 0 && slot < ordered.Count ? ordered[slot] : null;
        }

        private int ResolveSlotForPlayer(PlayerControl player, PlayerControl[] players, int expected, int fallback)
        {
            int ownerId = GetPlayerOwnerId(player);
            if (ownerId >= 0 && ownerId < expected)
                return ownerId;

            if (fallback >= 0 && fallback < expected)
                return fallback;

            if (players.Length == 0)
                players = GetAllPlayerControls();

            for (int i = 0; i < expected; i++)
            {
                var bySlot = FindPlayerForSlot(players, i);
                if (SamePlayer(bySlot, player))
                    return i;
            }

            return -1;
        }

        private PlayerControl[] GetAllPlayerControls()
        {
            var players = new List<PlayerControl>();
            var seen = new HashSet<int>();

            try
            {
                var manager = PlayerReferenceManager.Instance;
                AddPlayerControlsFromReferenceList(players, seen, manager?.GetPlayerReferences());
            }
            catch { }

            try
            {
                for (int i = _knownPlayerControls.Count - 1; i >= 0; i--)
                {
                    var known = _knownPlayerControls[i];
                    if (known == null)
                    {
                        _knownPlayerControls.RemoveAt(i);
                        continue;
                    }

                    AddPlayerControl(players, seen, known);
                }
            }
            catch { }

            foreach (var obj in FindUnityObjectsOfType(typeof(PlayerControl)))
            {
                if (obj is PlayerControl player)
                    AddPlayerControl(players, seen, player);
            }

            return players.ToArray();
        }

        private static void AddPlayerControlsFromReferenceList(List<PlayerControl> players, HashSet<int> seen, object? references)
        {
            if (references == null)
                return;

            try
            {
                int count = GetIntProperty(references, "Count");
                if (count <= 0)
                    return;

                var indexer = references.GetType().GetProperty(
                    "Item",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (indexer == null)
                    return;

                for (int i = 0; i < count; i++)
                {
                    object? value = null;
                    try { value = indexer.GetValue(references, new object[] { i }); }
                    catch { }

                    if (value is PlayerReference reference)
                        AddPlayerControl(players, seen, reference.PlayerControl);
                }
            }
            catch { }
        }

        private static void AddPlayerControl(List<PlayerControl> players, HashSet<int> seen, PlayerControl? player)
        {
            if (player == null)
                return;

            try
            {
                int instanceId = player.GetInstanceID();
                if (instanceId != 0 && !seen.Add(instanceId))
                    return;
            }
            catch { }

            players.Add(player);
        }

        private static UnityEngine.Object[] FindUnityObjectsOfType(Type type)
        {
            try
            {
                var method = typeof(UnityEngine.Object).GetMethod(
                    "FindObjectsOfType",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { typeof(Type) },
                    null);

                return method?.Invoke(null, new object[] { type }) as UnityEngine.Object[]
                    ?? Array.Empty<UnityEngine.Object>();
            }
            catch { return Array.Empty<UnityEngine.Object>(); }
        }

        private static string GetSteamPersonaName()
        {
            try
            {
                foreach (string typeName in new[] { "Steamworks.SteamFriends", "SteamFriends" })
                {
                    Type? type = PatchHelpers.SafeTypeByName(typeName);
                    var method = type?.GetMethod(
                        "GetPersonaName",
                        BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                    if (method?.Invoke(null, null) is string name && !string.IsNullOrWhiteSpace(name))
                        return name;
                }
            }
            catch { }

            return "";
        }

        private static bool SamePlayer(object? a, object? b)
        {
            if (a == null || b == null)
                return false;

            try
            {
                var aUnity = a as UnityEngine.Object;
                var bUnity = b as UnityEngine.Object;
                if (aUnity != null && bUnity != null)
                    return aUnity == bUnity;
            }
            catch { }

            return ReferenceEquals(a, b);
        }

        private static int GetPlayerObjectId(object? playerControl)
        {
            int direct = GetIntProperty(playerControl, "ObjectId");
            if (direct > 0)
                return direct;

            object? networkObject = ReflectionHelper.GetProperty(playerControl, "NetworkObject");
            return GetIntProperty(networkObject, "ObjectId");
        }

        private static int GetPlayerOwnerId(object? playerControl)
        {
            int direct = GetIntProperty(playerControl, "OwnerId");
            if (direct >= 0)
                return direct;

            object? networkObject = ReflectionHelper.GetProperty(playerControl, "NetworkObject");
            return GetIntProperty(networkObject, "OwnerId");
        }

        private static int GetIntProperty(object? instance, string propertyName)
        {
            if (instance == null)
                return -1;

            try
            {
                var prop = instance.GetType().GetProperty(
                    propertyName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                object? value = prop?.GetValue(instance);
                return value is int i ? i : -1;
            }
            catch { return -1; }
        }

        private void LogRosterOnce()
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return;

            var entries = GetEntries();
            string key = string.Join("|", entries.ConvertAll(e => $"{e.ConnectionId}:{e.Name}"));
            if (!_logged.Add(key))
                return;

            Plugin.Log.LogInfo($"[NetworkedRosterManager] Roster entries: {key}");
        }
    }
}
