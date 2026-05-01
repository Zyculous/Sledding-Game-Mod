using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SledCoopMod.Patches
{
    internal readonly struct NetworkedPlayerDisplayEntry
    {
        public readonly int ConnectionId;
        public readonly string Name;
        public readonly bool IsLocal;
        public readonly bool IsHost;

        public NetworkedPlayerDisplayEntry(int connectionId, string name, bool isLocal, bool isHost)
        {
            ConnectionId = connectionId;
            Name = name;
            IsLocal = isLocal;
            IsHost = isHost;
        }
    }

    internal static class NetworkedPlayerIdentity
    {
        private static readonly System.Collections.Generic.HashSet<string> _logged =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private static readonly System.Collections.Generic.List<PlayerControl> _knownPlayerControls =
            new System.Collections.Generic.List<PlayerControl>();
        private static bool _sanitizeLogged;
        private static int _lastReferenceEnsureFrame = -1;
        private static bool _referenceEnsureLogged;

        public static bool Enabled => NetworkedInstanceManager.IsNetworkedModeConfigured;

        public static void Sanitize(
            ref string productUserId,
            ref long platformId,
            int connectionId,
            ref string fallbackUsername,
            ref string voiceId,
            ref AuthPlatform platform)
        {
            if (!Enabled) return;

            int slot = Math.Max(0, NetworkedInstanceManager.Instance?.SlotIndex ?? 0);
            int stableId = connectionId >= 0 ? connectionId : slot;

            if (string.IsNullOrWhiteSpace(productUserId))
                productUserId = $"sledcoop-product-{stableId}";

            if (platformId == 0)
                platformId = 9_000_000L + stableId;

            if (string.IsNullOrWhiteSpace(fallbackUsername))
                fallbackUsername = GetDisplayNameForConnectionId(stableId);

            if (string.IsNullOrWhiteSpace(voiceId))
                voiceId = $"sledcoop-voice-{stableId}";

            // Tugboat loopback players do not have unique EOS/Steam identities.
            // Keep the field populated with the game's default platform enum so
            // native UI/reference code can compare records without null IDs.
            platform = AuthPlatform.Steam;

            if (!_sanitizeLogged)
            {
                _sanitizeLogged = true;
                Plugin.Log.LogInfo($"[NetworkedPlayerIdentity] Supplying loopback player identity product='{productUserId}', platformId={platformId}, user='{fallbackUsername}', voice='{voiceId}'.");
            }
        }

        public static bool IsPlayerIdNullArgument(Exception e)
        {
            if (e is ArgumentNullException ane)
                return string.Equals(ane.ParamName, "playerId", StringComparison.Ordinal);

            if (e.InnerException != null && IsPlayerIdNullArgument(e.InnerException))
                return true;

            try
            {
                string text = e.ToString();
                return text.IndexOf("playerId", StringComparison.OrdinalIgnoreCase) >= 0
                    && (text.IndexOf("Value cannot be null", StringComparison.OrdinalIgnoreCase) >= 0
                        || text.IndexOf("ArgumentNullException", StringComparison.OrdinalIgnoreCase) >= 0);
            }
            catch { }

            return false;
        }

        public static void LogOnce(string key, string message)
        {
            if (_logged.Add(key))
                Plugin.Log.LogWarning(message);
        }

        public static void NormalizeInflictingPlayerObjectId(
            ref int playerInflictingObjectId,
            object? sourcePlayer,
            object? hitPlayer,
            string site)
        {
            try
            {
                NormalizeInflictingPlayerObjectIdCore(ref playerInflictingObjectId, sourcePlayer, hitPlayer, site);
            }
            catch (Exception e)
            {
                LogOnce(
                    $"normalize-inflictor-error-{site}",
                    $"[NetworkedPlayerIdentity] Failed to normalize {site} inflicting player id: {e.GetType().Name}: {e.Message}");
            }
        }

        private static void NormalizeInflictingPlayerObjectIdCore(
            ref int playerInflictingObjectId,
            object? sourcePlayer,
            object? hitPlayer,
            string site)
        {
            if (!Enabled || playerInflictingObjectId < 0)
                return;

            if (TryFindPlayerByObjectId(playerInflictingObjectId, out _))
                return;

            int original = playerInflictingObjectId;
            int resolved = ResolveInflictingPlayerObjectId(original, sourcePlayer, hitPlayer);
            if (resolved <= 0 || resolved == original)
                return;

            playerInflictingObjectId = resolved;
            LogOnce(
                $"normalize-inflictor-{site}-{original}-{resolved}",
                $"[NetworkedPlayerIdentity] Rewrote {site} inflicting player id {original} to NetworkObject id {resolved}.");
        }

        private static int ResolveInflictingPlayerObjectId(int candidate, object? sourcePlayer, object? hitPlayer)
        {
            int sourceObjectId = GetPlayerObjectId(sourcePlayer);
            if (sourceObjectId > 0 && !SamePlayer(sourcePlayer, hitPlayer))
                return sourceObjectId;

            if (TryGetPlayerControlFromReferenceManager(candidate, out var referenced))
            {
                int referencedObjectId = GetPlayerObjectId(referenced);
                if (referencedObjectId > 0 && !SamePlayer(referenced, hitPlayer))
                    return referencedObjectId;
            }

            foreach (var player in GetAllPlayerControls())
            {
                if (player == null || SamePlayer(player, hitPlayer))
                    continue;

                if (GetPlayerOwnerId(player) == candidate)
                {
                    int objectId = GetPlayerObjectId(player);
                    if (objectId > 0)
                        return objectId;
                }
            }

            foreach (var player in GetAllPlayerControls())
            {
                if (player == null || SamePlayer(player, hitPlayer))
                    continue;

                int objectId = GetPlayerObjectId(player);
                if (objectId > 0)
                    return objectId;
            }

            return 0;
        }

        private static bool TryGetPlayerControlFromReferenceManager(int connectionId, out PlayerControl? playerControl)
        {
            playerControl = null;
            try
            {
                var manager = PlayerReferenceManager.Instance;
                if (manager != null && manager.TryGetPlayerControl(connectionId, out PlayerControl resolved) && resolved != null)
                {
                    playerControl = resolved;
                    return true;
                }
            }
            catch { }

            return false;
        }

        public static bool TryResolvePlayerControlByNetworkId(int id, out PlayerControl? playerControl)
        {
            playerControl = null;

            if (!Enabled || id < 0)
                return false;

            if (NetworkedRosterManager.Instance?.TryResolvePlayerControl(id, out playerControl) == true)
                return true;

            foreach (var player in GetAllPlayerControls())
            {
                if (player == null)
                    continue;

                int ownerId = GetPlayerOwnerId(player);
                int objectId = GetPlayerObjectId(player);
                if (ownerId == id || objectId == id)
                {
                    playerControl = player;
                    return true;
                }
            }

            return false;
        }

        public static void RememberPlayerControl(PlayerControl? playerControl)
        {
            if (!Enabled || playerControl == null)
                return;

            try { NetworkedRosterManager.Instance?.RegisterPlayerControl(playerControl); }
            catch { }

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

            EnsureLoopbackPlayerReferences();
        }

        public static void EnsureLoopbackPlayerReferences()
        {
            if (!Enabled)
                return;

            int frame = 0;
            try { frame = Time.frameCount; }
            catch { }

            if (frame != 0 && _lastReferenceEnsureFrame == frame)
                return;

            _lastReferenceEnsureFrame = frame;

            try
            {
                var manager = PlayerReferenceManager.Instance;
                if (manager == null)
                    return;

                var byConnectionId = GetOrCreateReferenceDictionary<int>(
                    manager,
                    "_playerConnectionIdToPlayerReference");
                var byProductId = GetOrCreateReferenceDictionary<string>(
                    manager,
                    "_playerPlatformIdToPlayerReference");

                if (byConnectionId == null || byProductId == null)
                    return;

                var players = GetAllPlayerControls();
                int expectedCount = GetExpectedPlayerCount();
                int added = 0;

                for (int connectionId = 0; connectionId < expectedCount; connectionId++)
                {
                    PlayerControl? player = FindPlayerForConnectionId(players, connectionId);
                    if (player == null)
                        continue;

                    var reference = CreateLoopbackPlayerReference(connectionId, player);
                    byConnectionId[connectionId] = reference;

                    int objectId = GetPlayerObjectId(player);
                    if (objectId > 0)
                        byConnectionId[objectId] = reference;

                    if (!string.IsNullOrWhiteSpace(reference.ProductUserId))
                        byProductId[reference.ProductUserId] = reference;
                    if (!string.IsNullOrWhiteSpace(reference.VoiceId))
                        byProductId[reference.VoiceId] = reference;

                    added++;
                }

                if (added > 0 && !_referenceEnsureLogged)
                {
                    _referenceEnsureLogged = true;
                    Plugin.Log.LogInfo($"[NetworkedPlayerIdentity] Populated loopback PlayerReferenceManager dictionaries with {added} player reference(s).");
                }
            }
            catch (Exception e)
            {
                LogOnce(
                    "ensure-loopback-player-references-error",
                    $"[NetworkedPlayerIdentity] Failed to populate loopback player references: {e.GetType().Name}: {e.Message}");
            }
        }

        public static int GetConnectionIdForPlayer(PlayerControl? playerControl)
        {
            if (playerControl == null)
                return -1;

            int ownerId = GetPlayerOwnerId(playerControl);
            if (ownerId >= 0 && ownerId < 32)
                return ownerId;

            int objectId = GetPlayerObjectId(playerControl);
            if (objectId > 0)
                return objectId;

            return ownerId;
        }

        public static Il2CppSystem.Collections.Generic.List<int> GetConnectionIdsNearPosition(Vector3 position, float radius)
        {
            var result = new Il2CppSystem.Collections.Generic.List<int>();
            var seen = new System.Collections.Generic.HashSet<int>();
            int expectedCount = GetExpectedPlayerCount();
            float effectiveRadius = radius > 0.1f ? radius : 40f;
            float radiusSqr = effectiveRadius * effectiveRadius;

            foreach (var player in GetAllPlayerControls())
            {
                int connectionId = GetRaceConnectionIdForPlayer(player, expectedCount);
                if (connectionId < 0 || !seen.Add(connectionId))
                    continue;

                if (IsPlayerNear(player, position, radiusSqr))
                    result.Add(connectionId);
            }

            if (result.Count == 0)
            {
                foreach (var player in GetAllPlayerControls())
                {
                    int connectionId = GetRaceConnectionIdForPlayer(player, expectedCount);
                    if (connectionId >= 0 && seen.Add(connectionId))
                        result.Add(connectionId);
                }
            }

            if (result.Count < expectedCount)
            {
                for (int i = 0; i < expectedCount; i++)
                {
                    if (seen.Add(i))
                        result.Add(i);
                }
            }

            return result;
        }

        public static System.Collections.Generic.List<NetworkedPlayerDisplayEntry> GetDisplayEntries()
        {
            try
            {
                var rosterEntries = NetworkedRosterManager.Instance?.GetEntries();
                if (rosterEntries != null && rosterEntries.Count > 0)
                    return rosterEntries;
            }
            catch { }

            var entries = new System.Collections.Generic.List<NetworkedPlayerDisplayEntry>();
            if (!Enabled)
                return entries;

            int localSlot = Math.Max(0, NetworkedInstanceManager.Instance?.SlotIndex ?? 0);
            int count = GetExpectedPlayerCount();
            for (int i = 0; i < count; i++)
            {
                entries.Add(new NetworkedPlayerDisplayEntry(
                    i,
                    GetDisplayNameForConnectionId(i),
                    i == localSlot,
                    i == 0));
            }

            return entries;
        }

        public static string GetDisplayNameForConnectionId(int connectionId)
        {
            connectionId = Math.Max(0, connectionId);

            try
            {
                var roster = NetworkedRosterManager.Instance;
                if (roster != null)
                    return roster.GetName(connectionId);
            }
            catch { }

            if (connectionId == 0)
            {
                string steamName = GetSteamPersonaName();
                return string.IsNullOrWhiteSpace(steamName) ? "Host" : steamName;
            }

            try
            {
                var slot = LocalPlayerManager.Instance?.GetSlot(connectionId);
                if (!string.IsNullOrWhiteSpace(slot?.ProfileName))
                    return slot.ProfileName;
            }
            catch { }

            try
            {
                var manager = NetworkedInstanceManager.Instance;
                if (manager != null
                    && manager.SlotIndex == connectionId
                    && !string.IsNullOrWhiteSpace(manager.ProfileName))
                    return manager.ProfileName;

                if (manager != null
                    && manager.TryGetConfiguredProfileName(connectionId, out string configuredProfile)
                    && !string.IsNullOrWhiteSpace(configuredProfile))
                    return configuredProfile;
            }
            catch { }

            return $"guest{connectionId:00}";
        }

        public static bool TryResolvePlayerReference(int lookupId, out PlayerReference playerReference)
        {
            playerReference = default;
            if (!Enabled || lookupId < 0)
                return false;

            try
            {
                if (NetworkedRosterManager.Instance?.TryResolveReference(lookupId, out playerReference) == true)
                    return true;
            }
            catch { }

            try
            {
                PlayerControl[] players = GetAllPlayerControls();
                int expectedCount = GetExpectedPlayerCount();
                PlayerControl? player = null;
                int connectionId = lookupId;

                if (lookupId >= 0 && lookupId < expectedCount)
                    player = FindPlayerForConnectionId(players, lookupId);

                if (player == null)
                {
                    foreach (var candidate in players)
                    {
                        if (candidate == null)
                            continue;

                        if (GetPlayerOwnerId(candidate) == lookupId || GetPlayerObjectId(candidate) == lookupId)
                        {
                            player = candidate;
                            break;
                        }
                    }
                }

                if (player == null)
                    return false;

                connectionId = ResolveConnectionIdForPlayer(player, players, expectedCount, lookupId);
                if (connectionId < 0)
                    return false;

                playerReference = CreateLoopbackPlayerReference(connectionId, player);
                return true;
            }
            catch (Exception e)
            {
                LogOnce(
                    $"resolve-reference-error-{lookupId}",
                    $"[NetworkedPlayerIdentity] Failed to resolve loopback PlayerReference for id {lookupId}: {e.GetType().Name}: {e.Message}");
                return false;
            }
        }

        public static bool TryResolvePlayerReferenceByString(string? lookup, out PlayerReference playerReference)
        {
            playerReference = default;
            if (!Enabled || string.IsNullOrWhiteSpace(lookup))
                return false;

            try
            {
                if (NetworkedRosterManager.Instance?.TryResolveReferenceByString(lookup, out playerReference) == true)
                    return true;
            }
            catch { }

            foreach (string prefix in new[] { "sledcoop-product-", "sledcoop-voice-" })
            {
                if (lookup.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                    && int.TryParse(lookup.Substring(prefix.Length), out int connectionId))
                    return TryResolvePlayerReference(connectionId, out playerReference);
            }

            return false;
        }

        private static int GetExpectedPlayerCount()
        {
            int count = 1;

            try { count = Math.Max(count, NetworkedInstanceManager.Instance?.ConfiguredPlayerCount ?? 1); }
            catch { }

            try { count = Math.Max(count, LocalPlayerManager.Instance?.ActiveCount ?? 1); }
            catch { }

            try
            {
                foreach (var player in GetAllPlayerControls())
                {
                    int connectionId = GetConnectionIdForPlayer(player);
                    if (connectionId >= 0 && connectionId < 4)
                        count = Math.Max(count, connectionId + 1);
                }
            }
            catch { }

            return Math.Max(1, Math.Min(4, count));
        }

        private static int GetRaceConnectionIdForPlayer(PlayerControl? playerControl, int expectedCount)
        {
            if (playerControl == null)
                return -1;

            int ownerId = GetPlayerOwnerId(playerControl);
            if (ownerId >= 0 && ownerId < expectedCount)
                return ownerId;

            int resolved = GetConnectionIdForPlayer(playerControl);
            if (resolved >= 0 && resolved < expectedCount)
                return resolved;

            return -1;
        }

        private static PlayerControl? FindPlayerForConnectionId(PlayerControl[] players, int connectionId)
        {
            foreach (var player in players)
            {
                if (player == null)
                    continue;

                if (GetPlayerOwnerId(player) == connectionId)
                    return player;
            }

            var ordered = new System.Collections.Generic.List<PlayerControl>();
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

            return connectionId >= 0 && connectionId < ordered.Count
                ? ordered[connectionId]
                : null;
        }

        private static int ResolveConnectionIdForPlayer(
            PlayerControl player,
            PlayerControl[] players,
            int expectedCount,
            int fallback)
        {
            int ownerId = GetPlayerOwnerId(player);
            if (ownerId >= 0 && ownerId < expectedCount)
                return ownerId;

            if (fallback >= 0 && fallback < expectedCount)
                return fallback;

            var ordered = new System.Collections.Generic.List<PlayerControl>();
            foreach (var candidate in players)
            {
                if (candidate != null)
                    ordered.Add(candidate);
            }

            ordered.Sort((a, b) =>
            {
                int ownerCompare = GetPlayerOwnerId(a).CompareTo(GetPlayerOwnerId(b));
                if (ownerCompare != 0)
                    return ownerCompare;

                return GetPlayerObjectId(a).CompareTo(GetPlayerObjectId(b));
            });

            for (int i = 0; i < ordered.Count && i < expectedCount; i++)
            {
                if (SamePlayer(ordered[i], player))
                    return i;
            }

            return -1;
        }

        private static System.Collections.Generic.Dictionary<TKey, PlayerReference>? GetOrCreateReferenceDictionary<TKey>(
            object manager,
            string fieldName)
            where TKey : notnull
        {
            try
            {
                var field = manager.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field == null)
                    return null;

                if (field.GetValue(manager) is System.Collections.Generic.Dictionary<TKey, PlayerReference> existing)
                    return existing;

                var created = new System.Collections.Generic.Dictionary<TKey, PlayerReference>();
                field.SetValue(manager, created);
                return created;
            }
            catch { return null; }
        }

        private static string GetSteamPersonaName()
        {
            try
            {
                foreach (string typeName in new[]
                {
                    "Steamworks.SteamFriends",
                    "SteamFriends",
                })
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

        private static bool IsPlayerNear(PlayerControl? player, Vector3 position, float radiusSqr)
        {
            if (player == null)
                return false;

            try
            {
                Vector3 delta = player.transform.position - position;
                return delta.sqrMagnitude <= radiusSqr;
            }
            catch { return true; }
        }

        public static PlayerReference CreateLoopbackPlayerReference(int connectionId, PlayerControl playerControl)
        {
            try
            {
                var roster = NetworkedRosterManager.Instance;
                if (roster != null)
                    return roster.CreatePlayerReference(connectionId, playerControl);
            }
            catch { }

            string username = GetDisplayNameForConnectionId(connectionId);

            return new PlayerReference(
                $"sledcoop-product-{connectionId}",
                9_000_000L + connectionId,
                connectionId,
                username,
                $"sledcoop-voice-{connectionId}",
                AuthPlatform.Steam,
                playerControl);
        }

        private static bool TryFindPlayerByObjectId(int objectId, out PlayerControl? playerControl)
        {
            playerControl = null;
            if (objectId <= 0)
                return false;

            foreach (var player in GetAllPlayerControls())
            {
                if (player == null)
                    continue;

                if (GetPlayerObjectId(player) == objectId)
                {
                    playerControl = player;
                    return true;
                }
            }

            return false;
        }

        private static PlayerControl[] GetAllPlayerControls()
        {
            var players = new System.Collections.Generic.List<PlayerControl>();
            var seen = new System.Collections.Generic.HashSet<int>();

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

        private static void AddPlayerControlsFromReferenceList(
            System.Collections.Generic.List<PlayerControl> players,
            System.Collections.Generic.HashSet<int> seen,
            object? references)
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

        private static void AddPlayerControl(
            System.Collections.Generic.List<PlayerControl> players,
            System.Collections.Generic.HashSet<int> seen,
            PlayerControl? player)
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
    }

    [HarmonyPatch]
    internal static class Patch_PlayerReference_Ctor_NetworkedIdentity
    {
        static ConstructorInfo? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerReference");
            if (t == null) return null;
            return t.GetConstructor(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance, null,
                new[]
                {
                    typeof(string),
                    typeof(long),
                    typeof(int),
                    typeof(string),
                    typeof(string),
                    typeof(AuthPlatform),
                    typeof(PlayerControl),
                },
                null);
        }

        [HarmonyPrefix]
        static void Prefix(
            ref string productUserId,
            ref long platformUserId,
            int connectionID,
            ref string fallbackUsername,
            ref string voiceId,
            ref AuthPlatform authPlatform)
        {
            NetworkedPlayerIdentity.Sanitize(
                ref productUserId,
                ref platformUserId,
                connectionID,
                ref fallbackUsername,
                ref voiceId,
                ref authPlatform);
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerControl_CmdAddPlayerReference_NetworkedIdentity
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerControl");
            return t == null ? null : PatchHelpers.FindMethod(t, "Cmd_AddPlayerReference");
        }

        [HarmonyPrefix]
        static void Prefix(
            ref string productUserId,
            ref long platformId,
            int connectionId,
            ref string fallbackUsername,
            ref string voiceId,
            ref AuthPlatform platform)
        {
            NetworkedPlayerIdentity.Sanitize(
                ref productUserId,
                ref platformId,
                connectionId,
                ref fallbackUsername,
                ref voiceId,
                ref platform);
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerControl_RegisterKnown_NetworkedIdentity
    {
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerControl");
            if (t == null) yield break;

            foreach (string name in new[]
            {
                "Awake",
                "OnStartClient",
                "OnStartServer",
            })
            {
                var method = PatchHelpers.FindMethod(t, name);
                if (method != null)
                    yield return method;
            }
        }

        [HarmonyPostfix]
        static void Postfix(object __instance)
        {
            NetworkedPlayerIdentity.RememberPlayerControl(__instance as PlayerControl);
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerControl_RpcLogicAddPlayerReference_NetworkedIdentity
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerControl");
            return t == null ? null : PatchHelpers.FindMethod(t, "RpcLogic___Cmd_AddPlayerReference___7813325");
        }

        [HarmonyPrefix]
        static void Prefix(
            ref string __0,
            ref long __1,
            int __2,
            ref string __3,
            ref string __4,
            ref AuthPlatform __5)
        {
            NetworkedPlayerIdentity.Sanitize(
                ref __0,
                ref __1,
                __2,
                ref __3,
                ref __4,
                ref __5);
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerReferenceManager_ServerAddPlayerReference_NetworkedIdentity
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerReferenceManager");
            return t == null ? null : PatchHelpers.FindMethod(t, "Server_AddPlayerReference");
        }

        [HarmonyPrefix]
        static void Prefix(
            ref string productUserId,
            ref long platformId,
            int connectionId,
            ref string fallbackUsername,
            ref string voiceId,
            ref AuthPlatform platform)
        {
            NetworkedPlayerIdentity.Sanitize(
                ref productUserId,
                ref platformId,
                connectionId,
                ref fallbackUsername,
                ref voiceId,
                ref platform);
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerReferenceManager_TryGetPlayerString_NullGuard
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerReferenceManager");
            if (t == null) return null;
            return AccessTools.Method(t, "TryGetPlayer", new[] { typeof(string), typeof(PlayerReference).MakeByRefType() });
        }

        [HarmonyPrefix]
        static bool Prefix(string playerProductId, ref PlayerReference playerReference, ref bool __result)
        {
            if (!NetworkedPlayerIdentity.Enabled)
                return true;

            if (NetworkedPlayerIdentity.TryResolvePlayerReferenceByString(playerProductId, out var resolved))
            {
                playerReference = resolved;
                __result = true;
                return false;
            }

            playerReference = default;
            __result = false;
            if (string.IsNullOrWhiteSpace(playerProductId))
            {
                NetworkedPlayerIdentity.LogOnce(
                    "TryGetPlayer-null-product",
                    "[NetworkedPlayerIdentity] Ignored null product-user lookup in networked local mode.");
            }
            return false;
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, ref PlayerReference playerReference, ref bool __result)
        {
            if (__exception == null) return null;
            if (!NetworkedPlayerIdentity.Enabled) return __exception;

            playerReference = default;
            __result = false;
            NetworkedPlayerIdentity.LogOnce(
                "TryGetPlayer-product-finalizer",
                $"[NetworkedPlayerIdentity] Suppressed product-user lookup failure in networked local mode: {__exception.GetType().Name}.");
            return null;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerReferenceManager_TryGetPlayerInt_NetworkedFallback
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerReferenceManager");
            if (t == null) return null;
            return AccessTools.Method(t, "TryGetPlayer", new[] { typeof(int), typeof(PlayerReference).MakeByRefType() });
        }

        [HarmonyPrefix]
        static bool Prefix(int connectionId, ref PlayerReference playerReference, ref bool __result)
        {
            if (!NetworkedPlayerIdentity.Enabled)
                return true;

            NetworkedPlayerIdentity.EnsureLoopbackPlayerReferences();
            if (NetworkedPlayerIdentity.TryResolvePlayerReference(connectionId, out var resolved))
            {
                playerReference = resolved;
                __result = true;
            }
            else
            {
                playerReference = default;
                __result = false;
                NetworkedPlayerIdentity.LogOnce(
                    $"TryGetPlayer-int-miss-{connectionId}",
                    $"[NetworkedPlayerIdentity] No loopback PlayerReference found for connection/object id {connectionId}.");
            }

            return false;
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, int connectionId, ref PlayerReference playerReference, ref bool __result)
        {
            if (__exception == null) return null;
            if (!NetworkedPlayerIdentity.Enabled) return __exception;

            if (NetworkedPlayerIdentity.TryResolvePlayerReference(connectionId, out var resolved))
            {
                playerReference = resolved;
                __result = true;
            }
            else
            {
                playerReference = default;
                __result = false;
            }

            NetworkedPlayerIdentity.LogOnce(
                $"TryGetPlayer-int-finalizer-{connectionId}",
                $"[NetworkedPlayerIdentity] Suppressed integer player lookup failure in networked local mode for id {connectionId}: {__exception.GetType().Name}.");
            return null;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerReferenceManager_GetAllConnectionIdsNearPosition_NetworkedFallback
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerReferenceManager");
            return t == null ? null : PatchHelpers.FindMethod(t, "GetAllConnectionIdsNearPosition");
        }

        [HarmonyPrefix]
        static bool Prefix(Vector3 position, float radius, ref Il2CppSystem.Collections.Generic.List<int> __result)
        {
            if (!NetworkedPlayerIdentity.Enabled)
                return true;

            __result = NetworkedPlayerIdentity.GetConnectionIdsNearPosition(position, radius);
            NetworkedPlayerIdentity.LogOnce(
                "race-nearby-connection-ids",
                $"[NetworkedPlayerIdentity] Supplying loopback race participant ids near start; count={__result.Count}.");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_RaceManager_AddRace_NetworkedParticipants
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("_Scripts.Managers.RaceManager");
            return t == null ? null : PatchHelpers.FindMethod(t, "AddRace");
        }

        [HarmonyPrefix]
        static void Prefix(
            PlaceableRaceInteractable start,
            ref Il2CppSystem.Collections.Generic.List<int> connectionIds)
        {
            if (!NetworkedPlayerIdentity.Enabled)
                return;

            bool empty = true;
            try { empty = connectionIds == null || connectionIds.Count == 0; }
            catch { }

            if (!empty)
                return;

            float radius = 40f;
            Vector3 position = Vector3.zero;
            try
            {
                if (start != null)
                {
                    radius = start.radius > 0.1f ? start.radius : radius;
                    position = start.transform.position;
                }
            }
            catch { }

            connectionIds = NetworkedPlayerIdentity.GetConnectionIdsNearPosition(position, radius);
            NetworkedPlayerIdentity.LogOnce(
                "race-add-participants",
                $"[NetworkedPlayerIdentity] Filled empty race participant list for loopback race; count={connectionIds.Count}.");
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerReferenceManager_Update_NetworkedFinalizer
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerReferenceManager");
            return t == null ? null : PatchHelpers.FindMethod(t, "Update");
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (!NetworkedPlayerIdentity.Enabled)
                return true;

            NetworkedPlayerIdentity.EnsureLoopbackPlayerReferences();
            return false;
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception)
        {
            if (__exception == null) return null;
            if (!NetworkedPlayerIdentity.Enabled) return __exception;

            NetworkedPlayerIdentity.LogOnce(
                "PlayerReferenceManager.Update",
                $"[NetworkedPlayerIdentity] Suppressed PlayerReferenceManager.Update failure in loopback mode: {__exception.GetType().Name}.");
            return null;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerReferenceManager_TryGetPlayerControl_NetworkedFallback
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerReferenceManager");
            if (t == null) return null;
            return AccessTools.Method(t, "TryGetPlayerControl", new[] { typeof(int), typeof(PlayerControl).MakeByRefType() });
        }

        [HarmonyPrefix]
        static bool Prefix(int connectionId, ref PlayerControl playerControl, ref bool __result)
        {
            if (!NetworkedPlayerIdentity.Enabled)
                return true;

            NetworkedPlayerIdentity.EnsureLoopbackPlayerReferences();

            if (!NetworkedPlayerIdentity.TryResolvePlayerControlByNetworkId(connectionId, out var resolved) || resolved == null)
            {
                playerControl = null;
                __result = false;
                return false;
            }

            playerControl = resolved;
            __result = true;
            NetworkedPlayerIdentity.LogOnce(
                $"TryGetPlayerControl-fallback-{connectionId}",
                $"[NetworkedPlayerIdentity] Resolved missing loopback PlayerControl for connection/object id {connectionId}.");
            return false;
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, int connectionId, ref PlayerControl playerControl, ref bool __result)
        {
            if (__exception == null) return null;
            if (!NetworkedPlayerIdentity.Enabled) return __exception;

            if (NetworkedPlayerIdentity.TryResolvePlayerControlByNetworkId(connectionId, out var resolved) && resolved != null)
            {
                playerControl = resolved;
                __result = true;
            }
            else
            {
                playerControl = null;
                __result = false;
            }

            NetworkedPlayerIdentity.LogOnce(
                $"TryGetPlayerControl-finalizer-{connectionId}",
                $"[NetworkedPlayerIdentity] Suppressed PlayerControl lookup failure in networked local mode for id {connectionId}: {__exception.GetType().Name}.");
            return null;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerReferenceManager_Lifecycle_NetworkedReferences
    {
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerReferenceManager");
            if (t == null) yield break;

            foreach (string name in new[]
            {
                "Awake",
                "OnStartServer",
                "OnStartClient",
                "OnPlayerReferenceAdded",
                "OnPlayerReferenceRemoved",
                "OnPlayerReferenceCleared",
            })
            {
                var method = PatchHelpers.FindMethod(t, name);
                if (method != null)
                    yield return method;
            }
        }

        [HarmonyPostfix]
        static void Postfix()
        {
            NetworkedPlayerIdentity.EnsureLoopbackPlayerReferences();
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerReferenceManager_RecentlyPlayed_NetworkedSkip
    {
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerReferenceManager");
            if (t == null) yield break;

            foreach (string name in new[]
            {
                "UpdateRecentlyPlayedWith",
                "OnPlayerReferenceCleared",
                "OnStopClient",
            })
            {
                var method = PatchHelpers.FindMethod(t, name);
                if (method != null)
                    yield return method;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod)
        {
            if (!NetworkedPlayerIdentity.Enabled)
                return true;

            if (!string.Equals(__originalMethod.Name, "UpdateRecentlyPlayedWith", StringComparison.Ordinal))
                return true;

            NetworkedPlayerIdentity.LogOnce(
                $"recently-played-skip-{__originalMethod.Name}",
                $"[NetworkedPlayerIdentity] Skipped {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} platform recent-player bookkeeping in networked local mode.");
            return false;
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            if (!NetworkedPlayerIdentity.Enabled) return __exception;

            NetworkedPlayerIdentity.LogOnce(
                $"recently-played-finalizer-{__originalMethod.Name}",
                $"[NetworkedPlayerIdentity] Suppressed {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name} failure in networked local mode: {__exception.GetType().Name}.");
            return null;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerReferenceManager_TryGetPlayerByVoiceId_NullGuard
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerReferenceManager");
            if (t == null) return null;
            return AccessTools.Method(t, "TryGetPlayerByVoiceId", new[] { typeof(string), typeof(PlayerReference).MakeByRefType() });
        }

        [HarmonyPrefix]
        static bool Prefix(string dissonanceVoiceId, ref PlayerReference playerReference, ref bool __result)
        {
            if (!NetworkedPlayerIdentity.Enabled)
                return true;

            if (NetworkedPlayerIdentity.TryResolvePlayerReferenceByString(dissonanceVoiceId, out var resolved))
            {
                playerReference = resolved;
                __result = true;
                return false;
            }

            playerReference = default;
            __result = false;
            if (string.IsNullOrWhiteSpace(dissonanceVoiceId))
            {
                NetworkedPlayerIdentity.LogOnce(
                    "TryGetPlayer-null-voice",
                    "[NetworkedPlayerIdentity] Ignored null voice-id lookup in networked local mode.");
            }
            return false;
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, ref PlayerReference playerReference, ref bool __result)
        {
            if (__exception == null) return null;
            if (!NetworkedPlayerIdentity.Enabled) return __exception;

            playerReference = default;
            __result = false;
            NetworkedPlayerIdentity.LogOnce(
                "TryGetPlayer-voice-finalizer",
                $"[NetworkedPlayerIdentity] Suppressed voice-id lookup failure in networked local mode: {__exception.GetType().Name}.");
            return null;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerReference_GetIsPlayerBlocked_NetworkedIdentity
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerReference");
            return t == null ? null : PatchHelpers.FindMethod(t, "GetIsPlayerBlocked");
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, ref bool __result)
        {
            if (__exception == null) return null;
            if (!NetworkedPlayerIdentity.Enabled) return __exception;

            __result = false;
            NetworkedPlayerIdentity.LogOnce(
                "PlayerReference.GetIsPlayerBlocked",
                $"[NetworkedPlayerIdentity] Suppressed platform block-list lookup failure in networked local mode: {__exception.GetType().Name}.");
            return null;
        }
    }

    [HarmonyPatch]
    internal static class Patch_Snowball_PlayerIdFinalizers
    {
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var snowball = PatchHelpers.SafeTypeByName("Snowball");
            if (snowball != null)
            {
                foreach (string name in new[]
                {
                    "LocalPlayerHitOtherPlayer",
                    "Target_NotifyPlayerThatTheyHitAPlayer",
                    "Target_NotifyPlayerThatGotHitBySnowball",
                    "Rpc_Hit",
                    "Rpc_Hit_Impl",
                    "RpcLogic___Rpc_Hit___3488310474",
                    "RpcLogic___Target_NotifyPlayerThatTheyHitAPlayer___530160725",
                    "RpcLogic___Target_NotifyPlayerThatGotHitBySnowball___530160725",
                })
                {
                    var method = PatchHelpers.FindMethod(snowball, name);
                    if (method != null)
                        yield return method;
                }
            }

            var throwable = PatchHelpers.SafeTypeByName("Throwable");
            if (throwable != null)
            {
                foreach (string name in new[]
                {
                    "LocalPlayerHitOtherPlayer",
                    "Rpc_Hit",
                    "RpcLogic___Rpc_Hit___3488310474",
                })
                {
                    var method = PatchHelpers.FindMethod(throwable, name);
                    if (method != null)
                        yield return method;
                }
            }
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            if (!NetworkedPlayerIdentity.Enabled) return __exception;
            if (!NetworkedPlayerIdentity.IsPlayerIdNullArgument(__exception)) return __exception;

            NetworkedPlayerIdentity.LogOnce(
                $"snowball-{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}",
                $"[NetworkedPlayerIdentity] Suppressed null platform playerId in {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}.");
            return null;
        }
    }

    [HarmonyPatch]
    internal static class Patch_Snowball_TargetNotify_ServerOnlyGuard
    {
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var snowball = PatchHelpers.SafeTypeByName("Snowball");
            if (snowball == null) yield break;

            foreach (string name in new[]
            {
                "Target_NotifyPlayerThatTheyHitAPlayer",
                "Target_NotifyPlayerThatGotHitBySnowball",
                "RpcWriter___Target_NotifyPlayerThatTheyHitAPlayer___530160725",
                "RpcWriter___Target_NotifyPlayerThatGotHitBySnowball___530160725",
            })
            {
                var method = PatchHelpers.FindMethod(snowball, name);
                if (method != null)
                    yield return method;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(MethodBase __originalMethod)
        {
            if (!NetworkedPlayerIdentity.Enabled)
                return true;

            if (NetworkedInstanceManager.Instance?.IsChildClient != true)
                return true;

            NetworkedPlayerIdentity.LogOnce(
                $"snowball-target-notify-client-{__originalMethod.Name}",
                $"[NetworkedPlayerIdentity] Skipped client-side snowball target notification writer {__originalMethod.Name}; server will own hit notifications.");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerControl_ServerGetHitBySomething_NormalizeInflictor
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerControl");
            return t == null ? null : PatchHelpers.FindMethod(t, "Server_GetHitBySomething");
        }

        [HarmonyPrefix]
        static void Prefix(object __instance, PlayerControl hitPlayer, ref int playerInflictingObjectId)
        {
            NetworkedPlayerIdentity.NormalizeInflictingPlayerObjectId(
                ref playerInflictingObjectId,
                __instance,
                hitPlayer,
                "Server_GetHitBySomething");
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerControl_TargetRpcFalldown_NormalizeInflictor
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerControl");
            return t == null ? null : PatchHelpers.FindMethod(t, "TargetRpc_Falldown");
        }

        [HarmonyPrefix]
        static void Prefix(object __instance, ref int __3)
        {
            NetworkedPlayerIdentity.NormalizeInflictingPlayerObjectId(
                ref __3,
                null,
                __instance,
                "TargetRpc_Falldown");
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerControl_RpcLogicFalldown_NormalizeInflictor
    {
        static MethodBase? TargetMethod()
        {
            var t = PatchHelpers.SafeTypeByName("PlayerControl");
            return t == null ? null : PatchHelpers.FindMethod(t, "RpcLogic___TargetRpc_Falldown___65699372");
        }

        [HarmonyPrefix]
        static void Prefix(object __instance, ref int __3)
        {
            NetworkedPlayerIdentity.NormalizeInflictingPlayerObjectId(
                ref __3,
                null,
                __instance,
                "RpcLogic_TargetRpc_Falldown");
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerCollision_PlayerIdFinalizers
    {
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var playerControl = PatchHelpers.SafeTypeByName("PlayerControl");
            if (playerControl != null)
            {
                foreach (string name in new[]
                {
                    "Server_GetHitBySomething",
                    "TargetRpc_Falldown",
                    "FallDown",
                    "Local_AddForceToRagdoll",
                    "RpcLogic___TargetRpc_Falldown___65699372",
                    "RpcLogic___TargetRpc_Falldown___3004728439",
                })
                {
                    var method = PatchHelpers.FindMethod(playerControl, name);
                    if (method != null)
                        yield return method;
                }
            }

            var sled = PatchHelpers.SafeTypeByName("Sled");
            if (sled != null)
            {
                foreach (string name in new[]
                {
                    "Target_NotifySledOwnerThatAPlayerWasHit",
                    "RpcLogic___Target_NotifySledOwnerThatAPlayerWasHit___328543758",
                })
                {
                    var method = PatchHelpers.FindMethod(sled, name);
                    if (method != null)
                        yield return method;
                }
            }
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception, MethodBase __originalMethod)
        {
            if (__exception == null) return null;
            if (!NetworkedPlayerIdentity.Enabled) return __exception;
            if (!NetworkedPlayerIdentity.IsPlayerIdNullArgument(__exception)) return __exception;

            NetworkedPlayerIdentity.LogOnce(
                $"collision-{__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}",
                $"[NetworkedPlayerIdentity] Suppressed null platform playerId in {__originalMethod.DeclaringType?.Name}.{__originalMethod.Name}.");
            return null;
        }
    }
}
