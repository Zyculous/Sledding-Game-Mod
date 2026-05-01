using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using UnityEngine.UI;

namespace SledCoopMod.Patches
{
    internal static class NetworkedPlayerListUi
    {
        private static readonly System.Collections.Generic.Dictionary<int, int> LastRefreshFrameByInstance =
            new System.Collections.Generic.Dictionary<int, int>();
        private static readonly System.Collections.Generic.Dictionary<string, int> LastRefreshLogFrameByKey =
            new System.Collections.Generic.Dictionary<string, int>(StringComparer.Ordinal);
        private static bool _openExceptionLogged;

        public static void Refresh(object? display, bool force)
        {
            if (!NetworkedPlayerIdentity.Enabled || display == null)
                return;

            var entries = NetworkedRosterManager.Instance?.GetEntries() ?? NetworkedPlayerIdentity.GetDisplayEntries();
            int instanceId = 0;
            try { instanceId = (display as UnityEngine.Object)?.GetInstanceID() ?? display.GetHashCode(); }
            catch { }

            if (!force && instanceId != 0)
            {
                int frame = Time.frameCount;
                if (LastRefreshFrameByInstance.TryGetValue(instanceId, out int lastFrame) && frame - lastFrame < 30)
                    return;

                LastRefreshFrameByInstance[instanceId] = frame;
            }

            GameObject? displayRoot = ReflectionHelper.GetGameObject(display);
            Transform? content = GetField(display, "playerListContent") as Transform;
            content ??= FindPlayerListContentFromProfileButton(displayRoot);
            if (content == null)
            {
                if (RenderFromPlayerProfileButton(displayRoot, entries, force, "profile-button:no-content"))
                    return;

                RenderFallbackText(displayRoot, entries);
                LogRefresh(entries.Count, "fallback:no-content", force);
                return;
            }

            GameObject? prefab = GetGameObjectFromValue(GetField(display, "playerItemPrefab"));
            prefab ??= FindFirstPlayerItem(content);
            prefab ??= FindProfileButtonTemplateRow(content.gameObject);
            if (prefab == null)
            {
                if (RenderFromPlayerProfileButton(content.gameObject, entries, force, "profile-button:no-prefab"))
                    return;

                RenderFallbackText(content.gameObject, entries);
                LogRefresh(entries.Count, "fallback:no-prefab", force);
                return;
            }

            HideExistingRows(content);

            int rows = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                GameObject? row = GetOrCreateRow(content, prefab, entry.ConnectionId);
                if (row == null)
                    continue;

                row.SetActive(true);
                row.transform.SetSiblingIndex(i);

                object? item = FindComponentByTypeName(row, "PlayersListNameItem");
                if (item != null)
                    PopulateRow(item, entry);

                PopulateProfileButtonRow(row, entry);
                rows++;
            }

            SetText(GetField(display, "playerInLobbyText"), entries.Count == 1 ? "1 Player" : $"{entries.Count} Players");
            HideFallbackText(displayRoot);
            LogRefresh(rows, "native", force);
        }

        public static void RefreshAll(bool force = false)
        {
            Type? type = PatchHelpers.SafeTypeByName("PlayerListDisplayUI");
            if (type == null)
                return;

            foreach (var obj in FindUnityObjectsOfType(type))
                Refresh(obj, force);
        }

        public static void TryOpenPlayersMenu()
        {
            if (!NetworkedPlayerIdentity.Enabled)
                return;

            object? ui = GetUiReferenceController();
            if (ui == null)
                return;

            object? menu = GetField(ui, "showPlayersMenu");
            bool opened = false;

            try
            {
                var method = ui.GetType().GetMethod(
                    "OpenMenu",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (method != null && menu != null)
                {
                    method.Invoke(ui, new[] { menu });
                    opened = true;
                }
            }
            catch (Exception e)
            {
                if (!_openExceptionLogged)
                {
                    _openExceptionLogged = true;
                    Plugin.Log.LogWarning($"[NetworkedPlayerListUi] Native player-list menu open threw in loopback mode; falling back to direct panel activation: {e.GetType().Name}.");
                }
            }

            GameObject? panel = GetStructField(menu, "panel") as GameObject;
            if (!opened && panel != null)
                panel.SetActive(true);

            bool refreshedNative = RefreshPlayerListsInPanel(panel, true);
            if (!refreshedNative)
            {
                var entries = NetworkedRosterManager.Instance?.GetEntries() ?? NetworkedPlayerIdentity.GetDisplayEntries();
                if (!RenderFromPlayerProfileButton(panel, entries, true, "profile-button:no-display"))
                {
                    RenderFallbackText(panel, entries);
                    LogRefresh(entries.Count, "fallback:no-display", true);
                }
            }
            RefreshAll(force: true);
        }

        private static void HideExistingRows(Transform content)
        {
            var children = new System.Collections.Generic.List<GameObject>();
            try
            {
                for (int i = 0; i < content.childCount; i++)
                {
                    var child = content.GetChild(i);
                    if (child != null)
                        children.Add(child.gameObject);
                }
            }
            catch { }

            foreach (var child in children)
            {
                if (child == null)
                    continue;

                try { child.SetActive(false); }
                catch { }
            }
        }

        private static GameObject? GetOrCreateRow(Transform content, GameObject prefab, int connectionId)
        {
            string rowName = $"SledCoop_PlayerList_{connectionId}";
            GameObject? existing = FindDirectChild(content, rowName);
            if (existing != null)
                return existing;

            try
            {
                var clone = UnityEngine.Object.Instantiate(prefab);
                clone.name = rowName;
                clone.transform.SetParent(content, false);
                return clone;
            }
            catch { return null; }
        }

        private static void PopulateRow(object item, NetworkedPlayerDisplayEntry entry)
        {
            SetText(GetField(item, "nameText"), entry.Name);
            SetText(GetField(item, "pingText"), "");

            SetFieldGameObjectActive(item, "inGroupIndicator", false);
            SetFieldGameObjectActive(item, "speakingIndicator", false);
            SetFieldGameObjectActive(item, "mutedIndicator", false);
            SetFieldGameObjectActive(item, "blockedIndicator", false);
            SetFieldGameObjectActive(item, "isLocalPlayerBackground", entry.IsLocal);
            SetFieldGameObjectActive(item, "hostIdentifier", entry.IsHost);

            SetFieldGameObjectActive(item, "steamProfileImage", entry.IsHost);
            SetFieldGameObjectActive(item, "xboxProfileImage", false);
            SetFieldGameObjectActive(item, "ps4ProfileImage", false);
            SetFieldGameObjectActive(item, "nintendoSwitchProfileImage", false);

            DisableButtonField(item, "playerProfileActionsButton");
            DisableButtonField(item, "quickMuteButton");
        }

        private static void RenderFallbackText(
            GameObject? root,
            System.Collections.Generic.List<NetworkedPlayerDisplayEntry> entries)
        {
            if (root == null || entries.Count == 0)
                return;

            GameObject? panel = FindLikelyPanelRoot(root);
            if (panel == null)
                return;

            GameObject textGo = FindOrCreateDirectChild(panel.transform, "SledCoop_PlayerListFallback");
            textGo.SetActive(true);

            var rect = textGo.GetComponent<RectTransform>() ?? textGo.AddComponent<RectTransform>();
            rect.anchorMin = new Vector2(0f, 0f);
            rect.anchorMax = new Vector2(1f, 1f);
            rect.pivot = new Vector2(0f, 1f);
            rect.offsetMin = new Vector2(48f, 80f);
            rect.offsetMax = new Vector2(-48f, -80f);
            rect.localScale = Vector3.one;

            var text = textGo.GetComponent<Text>() ?? textGo.AddComponent<Text>();
            try { text.font = Resources.GetBuiltinResource<Font>("Arial.ttf"); }
            catch { }
            text.color = Color.white;
            text.fontSize = 28;
            text.alignment = TextAnchor.UpperLeft;
            text.horizontalOverflow = HorizontalWrapMode.Wrap;
            text.verticalOverflow = VerticalWrapMode.Overflow;
            text.raycastTarget = false;

            var lines = new System.Text.StringBuilder();
            lines.AppendLine("Players");
            foreach (var entry in entries)
            {
                lines.Append(entry.IsLocal ? "> " : "  ");
                lines.Append(entry.Name);
                if (entry.IsHost)
                    lines.Append("  Host");
                lines.AppendLine();
            }

            text.text = lines.ToString();
        }

        private static GameObject? FindLikelyPanelRoot(GameObject root)
        {
            Transform? t = root.transform;
            while (t != null)
            {
                string name = t.gameObject.name ?? "";
                if (name.IndexOf("show", StringComparison.OrdinalIgnoreCase) >= 0
                    && name.IndexOf("player", StringComparison.OrdinalIgnoreCase) >= 0)
                    return t.gameObject;

                t = t.parent;
            }

            return root;
        }

        private static GameObject FindOrCreateDirectChild(Transform parent, string name)
        {
            try
            {
                for (int i = 0; i < parent.childCount; i++)
                {
                    var child = parent.GetChild(i);
                    if (child != null && string.Equals(child.gameObject.name, name, StringComparison.Ordinal))
                        return child.gameObject;
                }
            }
            catch { }

            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            return go;
        }

        private static bool RefreshPlayerListsInPanel(GameObject? panel, bool force)
        {
            if (panel == null)
                return false;

            bool refreshed = false;
            foreach (var component in FindComponentsByTypeName(panel, "PlayerListDisplayUI"))
            {
                Refresh(component, force);
                refreshed = true;
            }

            if (!refreshed)
            {
                var entries = NetworkedRosterManager.Instance?.GetEntries() ?? NetworkedPlayerIdentity.GetDisplayEntries();
                refreshed = RenderFromPlayerProfileButton(panel, entries, force, "profile-button:panel");
            }

            return refreshed;
        }

        private static bool RenderFromPlayerProfileButton(
            GameObject? root,
            System.Collections.Generic.List<NetworkedPlayerDisplayEntry> entries,
            bool force,
            string mode)
        {
            if (root == null || entries.Count == 0)
                return false;

            GameObject? template = FindProfileButtonTemplateRow(root);
            if (template == null)
                return false;

            Transform? content = template.transform.parent;
            if (content == null)
                return false;

            HideExistingRows(content);

            int rows = 0;
            for (int i = 0; i < entries.Count; i++)
            {
                var entry = entries[i];
                GameObject? row = GetOrCreateRow(content, template, entry.ConnectionId);
                if (row == null)
                    continue;

                row.SetActive(true);
                row.transform.SetSiblingIndex(i);
                PopulateProfileButtonRow(row, entry);
                rows++;
            }

            HideFallbackText(root);
            LogRefresh(rows, mode, force);
            return rows > 0;
        }

        private static GameObject? FindFirstPlayerItem(Transform content)
        {
            try
            {
                for (int i = 0; i < content.childCount; i++)
                {
                    var child = content.GetChild(i);
                    if (child == null)
                        continue;

                    if (FindComponentByTypeName(child.gameObject, "PlayersListNameItem") != null)
                        return child.gameObject;
                }
            }
            catch { }

            return null;
        }

        private static Transform? FindPlayerListContentFromProfileButton(GameObject? root)
        {
            GameObject? template = FindProfileButtonTemplateRow(root);
            return template?.transform.parent;
        }

        private static GameObject? FindProfileButtonTemplateRow(GameObject? root)
        {
            GameObject? button = FindChildByNormalizedName(root, "playerprofilebutton");
            if (button == null)
                return null;

            try
            {
                Transform? t = button.transform;
                while (t != null)
                {
                    if (FindComponentByTypeName(t.gameObject, "PlayersListNameItem") != null)
                        return t.gameObject;

                    string name = NormalizeName(t.gameObject.name);
                    if (name.IndexOf("playerlistnameitem", StringComparison.OrdinalIgnoreCase) >= 0)
                        return t.gameObject;

                    if (t.parent == null)
                        break;

                    string parentName = NormalizeName(t.parent.gameObject.name);
                    if (parentName.IndexOf("content", StringComparison.OrdinalIgnoreCase) >= 0
                        || parentName.IndexOf("viewport", StringComparison.OrdinalIgnoreCase) >= 0)
                        return t.gameObject;

                    t = t.parent;
                }
            }
            catch { }

            return button;
        }

        private static GameObject? FindChildByNormalizedName(GameObject? root, string normalizedName)
        {
            if (root == null)
                return null;

            try
            {
                foreach (var transform in EnumerateTransforms(root.transform))
                {
                    if (transform == null)
                        continue;

                    if (NormalizeName(transform.gameObject.name).Equals(normalizedName, StringComparison.OrdinalIgnoreCase))
                        return transform.gameObject;
                }
            }
            catch { }

            return null;
        }

        private static void PopulateProfileButtonRow(GameObject row, NetworkedPlayerDisplayEntry entry)
        {
            try { row.name = $"SledCoop_PlayerList_{entry.ConnectionId}"; }
            catch { }

            SetTextInChildByName(row, "textplayername", entry.Name);
            SetTextInChildByName(row, "textping", "");
            SetAnyEmptyNameText(row, entry.Name);

            SetChildActiveByName(row, "localplayerbackground", entry.IsLocal);
            SetChildActiveByName(row, "hostidentifier", entry.IsHost);
            SetChildActiveByName(row, "identifieralreadyinparty", false);
            SetChildActiveByName(row, "speakingindicator", false);
            SetChildActiveByName(row, "mutedindicator", false);
            SetChildActiveByName(row, "blockedindicator", false);
            SetChildActiveByName(row, "steam", entry.IsHost);
            SetChildActiveByName(row, "xbox", false);
            SetChildActiveByName(row, "psn", false);
            SetChildActiveByName(row, "switch", false);

            DisableProfileButtons(row);
        }

        private static void DisableProfileButtons(GameObject row)
        {
            try
            {
                foreach (var transform in EnumerateTransforms(row.transform))
                {
                    var selectable = transform?.gameObject.GetComponent(typeof(Selectable)) as Selectable;
                    if (selectable == null)
                        continue;

                    string name = NormalizeName(selectable.gameObject.name);
                    if (name.IndexOf("playerprofilebutton", StringComparison.OrdinalIgnoreCase) >= 0
                        || name.IndexOf("quickmute", StringComparison.OrdinalIgnoreCase) >= 0)
                        selectable.interactable = false;
                }
            }
            catch { }
        }

        private static void SetChildActiveByName(GameObject root, string normalizedName, bool active)
        {
            GameObject? child = FindChildByNormalizedName(root, normalizedName);
            try { child?.SetActive(active); }
            catch { }
        }

        private static void SetTextInChildByName(GameObject root, string normalizedName, string value)
        {
            GameObject? child = FindChildByNormalizedName(root, normalizedName);
            if (child == null)
                return;

            SetTextOnGameObject(child, value);
        }

        private static void SetAnyEmptyNameText(GameObject row, string value)
        {
            try
            {
                foreach (var transform in EnumerateTransforms(row.transform))
                {
                    if (transform == null)
                        continue;

                    string name = NormalizeName(transform.gameObject.name);
                    if (name.IndexOf("playername", StringComparison.OrdinalIgnoreCase) < 0
                        && name.IndexOf("name", StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    SetTextOnGameObject(transform.gameObject, value);
                }
            }
            catch { }
        }

        private static System.Collections.Generic.List<Transform> EnumerateTransforms(Transform root)
        {
            var result = new System.Collections.Generic.List<Transform>();
            AddTransform(root, result);
            return result;
        }

        private static void AddTransform(Transform? transform, System.Collections.Generic.List<Transform> result)
        {
            if (transform == null)
                return;

            result.Add(transform);

            int childCount;
            try { childCount = transform.childCount; }
            catch { return; }

            for (int i = 0; i < childCount; i++)
            {
                try { AddTransform(transform.GetChild(i), result); }
                catch { }
            }
        }

        private static GameObject? FindDirectChild(Transform content, string name)
        {
            try
            {
                for (int i = 0; i < content.childCount; i++)
                {
                    var child = content.GetChild(i);
                    if (child != null && string.Equals(child.gameObject.name, name, StringComparison.Ordinal))
                        return child.gameObject;
                }
            }
            catch { }

            return null;
        }

        private static System.Collections.Generic.IEnumerable<object> FindComponentsByTypeName(GameObject root, string typeName)
        {
            var result = new System.Collections.Generic.List<object>();
            AddComponentsByTypeName(root.transform, typeName, result);
            return result;
        }

        private static void AddComponentsByTypeName(
            Transform? transform,
            string typeName,
            System.Collections.Generic.List<object> result)
        {
            if (transform == null)
                return;

            object? component = FindComponentByTypeName(transform.gameObject, typeName);
            if (component != null)
                result.Add(component);

            int childCount;
            try { childCount = transform.childCount; }
            catch { return; }

            for (int i = 0; i < childCount; i++)
            {
                try { AddComponentsByTypeName(transform.GetChild(i), typeName, result); }
                catch { }
            }
        }

        private static object? FindComponentByTypeName(GameObject? go, string typeName)
        {
            if (go == null)
                return null;

            Type? type = ResolveComponentType(typeName);
            return GetComponentByType(go, type);
        }

        private static void SetFieldGameObjectActive(object instance, string fieldName, bool active)
        {
            GameObject? go = GetGameObjectFromValue(GetField(instance, fieldName));
            try { go?.SetActive(active); }
            catch { }
        }

        private static void DisableButtonField(object instance, string fieldName)
        {
            object? button = GetField(instance, fieldName);
            try
            {
                if (button is Selectable selectable)
                    selectable.interactable = false;
            }
            catch { }

            try { GetGameObjectFromValue(button)?.SetActive(false); }
            catch { }
        }

        private static void SetText(object? textObject, string text)
        {
            if (textObject == null)
                return;

            try
            {
                var prop = textObject.GetType().GetProperty(
                    "text",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                prop?.SetValue(textObject, text);
            }
            catch { }

            try { GetGameObjectFromValue(textObject)?.SetActive(true); }
            catch { }
        }

        private static bool SetTextOnGameObject(GameObject? go, string text)
        {
            if (go == null)
                return false;

            foreach (var component in GetKnownTextComponents(go))
            {
                if (component == null)
                    continue;

                SetText(component, text);
                return true;
            }

            return false;
        }

        private static System.Collections.Generic.List<object> GetKnownTextComponents(GameObject go)
        {
            var result = new System.Collections.Generic.List<object>();
            foreach (string typeName in new[] { "TMPro.TMP_Text", "TMPro.TextMeshProUGUI", "UnityEngine.UI.Text" })
            {
                object? component = GetComponentByType(go, ResolveComponentType(typeName));
                if (component != null)
                    result.Add(component);
            }

            return result;
        }

        private static object? GetComponentByType(GameObject? go, Type? type)
        {
            if (go == null || type == null)
                return null;

            try { return go.GetComponent(type); }
            catch { return null; }
        }

        private static Type? ResolveComponentType(string typeName)
        {
            if (string.Equals(typeName, "UnityEngine.UI.Text", StringComparison.Ordinal)
                || string.Equals(typeName, "Text", StringComparison.Ordinal))
                return typeof(Text);

            if (string.Equals(typeName, "UnityEngine.UI.Selectable", StringComparison.Ordinal)
                || string.Equals(typeName, "Selectable", StringComparison.Ordinal))
                return typeof(Selectable);

            return PatchHelpers.SafeTypeByName(typeName);
        }

        private static bool HasTextProperty(object? textObject)
        {
            if (textObject == null)
                return false;

            try
            {
                return textObject.GetType().GetProperty(
                    "text",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic) != null;
            }
            catch { return false; }
        }

        private static void HideFallbackText(GameObject? root)
        {
            if (root == null)
                return;

            try
            {
                GameObject? panel = FindLikelyPanelRoot(root);
                if (panel == null)
                    return;

                GameObject? fallback = FindDirectChild(panel.transform, "SledCoop_PlayerListFallback");
                if (fallback != null)
                    fallback.SetActive(false);
            }
            catch { }
        }

        private static void LogRefresh(int rowCount, string mode, bool force)
        {
            string key = $"{mode}:{rowCount}:{force}";
            int frame = Time.frameCount;
            if (LastRefreshLogFrameByKey.TryGetValue(key, out int lastFrame) && frame - lastFrame < 300)
                return;

            LastRefreshLogFrameByKey[key] = frame;
            Plugin.Log.LogInfo($"[NetworkedPlayerListUi] Rendering loopback player list rows={rowCount} mode={mode} force={force}.");
        }

        private static object? GetField(object? instance, string fieldName)
        {
            if (instance == null)
                return null;

            try
            {
                var field = instance.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(instance);
            }
            catch { return null; }
        }

        private static object? GetStructField(object? instance, string fieldName)
        {
            if (instance == null)
                return null;

            try
            {
                var field = instance.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(instance);
            }
            catch { return null; }
        }

        private static GameObject? GetGameObjectFromValue(object? value)
        {
            return value as GameObject ?? ReflectionHelper.GetGameObject(value);
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

        private static object? GetUiReferenceController()
        {
            try
            {
                var t = PatchHelpers.SafeTypeByName("UiReferenceController");
                return t?.GetProperty("Instance", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            }
            catch { return null; }
        }

        private static string NormalizeName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return "";

            var chars = new char[value.Length];
            int count = 0;
            foreach (char c in value)
            {
                if (char.IsLetterOrDigit(c))
                    chars[count++] = char.ToLowerInvariant(c);
            }

            return new string(chars, 0, count);
        }
    }

    [HarmonyPatch]
    internal static class Patch_PlayerListDisplayUI_Networked
    {
        static System.Collections.Generic.IEnumerable<MethodBase> TargetMethods()
        {
            var type = PatchHelpers.SafeTypeByName("PlayerListDisplayUI");
            if (type == null)
                yield break;

            foreach (string name in new[]
            {
                "OnEnable",
                "OnDisable",
                "Update",
                "Button_CheckForIllegalPlayers",
                "UpdatePlayerList",
                "SetPlayerList",
                "SetPlayersInLobby",
            })
            {
                var method = PatchHelpers.FindMethod(type, name);
                if (method != null)
                    yield return method;
            }
        }

        [HarmonyPrefix]
        static bool Prefix(object __instance, MethodBase __originalMethod)
        {
            if (!NetworkedPlayerIdentity.Enabled)
                return true;

            NetworkedPlayerListUi.Refresh(__instance, force: __originalMethod.Name != "Update");
            return false;
        }
    }

    [HarmonyPatch]
    internal static class Patch_UiReferenceController_HandleInput_WhenInLobby_NetworkedPlayers
    {
        static MethodBase? TargetMethod()
        {
            var type = PatchHelpers.SafeTypeByName("UiReferenceController");
            return type == null ? null : PatchHelpers.FindMethod(type, "HandleInput_WhenInLobby");
        }

        [HarmonyPostfix]
        static void Postfix()
        {
            if (NetworkedPlayerIdentity.Enabled)
                NetworkedPlayerListUi.RefreshAll();
        }

        [HarmonyFinalizer]
        static Exception? Finalizer(Exception __exception)
        {
            if (__exception == null)
                return null;

            if (!NetworkedPlayerIdentity.Enabled)
                return __exception;

            NetworkedPlayerIdentity.LogOnce(
                "UiReferenceController.HandleInput_WhenInLobby",
                $"[NetworkedPlayerListUi] Suppressed native lobby player-list input failure in loopback mode: {__exception.GetType().Name}.");
            NetworkedPlayerListUi.TryOpenPlayersMenu();
            return null;
        }
    }

    [HarmonyPatch]
    internal static class Patch_UIPausePanel_Button_ViewPlayers_Networked
    {
        static MethodBase? TargetMethod()
        {
            var type = PatchHelpers.SafeTypeByName("_Scripts.UI.In_Game.UIPausePanel");
            return type == null ? null : PatchHelpers.FindMethod(type, "Button_ViewPlayers");
        }

        [HarmonyPrefix]
        static bool Prefix()
        {
            if (!NetworkedPlayerIdentity.Enabled)
                return true;

            NetworkedPlayerListUi.TryOpenPlayersMenu();
            return false;
        }
    }
}
