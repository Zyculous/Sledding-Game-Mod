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

            // Make sure every layer of the row is active — the template can be
            // cloned in a state where intermediate containers are disabled, which
            // makes the row invisible even after we activate the root.
            ActivateAncestorsUpTo(row);

            // Clear ping FIRST so the broad "set anything name-shaped" sweep
            // below doesn't accidentally overwrite the player name with "" if the
            // ping field is named something like "PlayerNamePing".
            SetTextInChildByName(row, "textping", "");

            // Try the strict, well-known name first; if the prefab is wrapped or
            // renamed we fall back to fuzzier matches and finally to "first text
            // component in the row" so the player always sees their name.
            bool nameSet = SetTextInChildByName(row, "textplayername", entry.Name)
                || SetTextInChildByNamePart(row, "playername", entry.Name)
                || SetTextInChildByNamePart(row, "name", entry.Name, excludeContains: "ping");

            if (!nameSet)
                SetFirstTextInRow(row, entry.Name);
            else
                SetAnyEmptyNameText(row, entry.Name);

            // Brute-force fallback: any TMP_Text/Text in this row whose
            // current content looks like the editor placeholder
            // ("Playerwithareallylongname" or any single ≥18-char word with
            // no spaces) gets overwritten. The strict / part / fuzzy
            // searches above all key on the GameObject's *name*; if the
            // prefab uses a child name we don't recognise, we'd miss it
            // and the placeholder stays visible. This pass keys on the
            // content instead, so the placeholder is wiped no matter what
            // the GO is called.
            OverwritePlaceholderText(row, entry.Name);

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

            // The profile buttons fire callbacks against Steam/EOS that don't
            // exist in our loopback session and would crash — keep them visible
            // (so the row reads like a button) but non-interactable.
            DisableProfileButtons(row);
        }

        // Walk every TMP_Text/Text in the row's subtree and overwrite any
        // whose visible text is empty OR matches the prefab placeholder
        // pattern (a single ≥18-character word with no spaces, e.g.
        // "Playerwithareallylongname"). We deliberately do NOT touch
        // texts that look like real ping values ("0", "27ms", etc) or
        // anything containing spaces (real names can have spaces, but
        // placeholders virtually never do).
        private static void OverwritePlaceholderText(GameObject row, string name)
        {
            try
            {
                foreach (var transform in EnumerateTransforms(row.transform))
                {
                    if (transform == null) continue;

                    string goName = NormalizeName(transform.gameObject.name);
                    bool isPing = goName.IndexOf("ping", StringComparison.OrdinalIgnoreCase) >= 0;
                    if (isPing) continue;

                    foreach (var component in GetKnownTextComponents(transform.gameObject))
                    {
                        if (component == null) continue;

                        string current = ReadCurrentText(component) ?? "";
                        if (LooksLikePlaceholder(current))
                        {
                            SetText(component, name);
                        }
                    }
                }
            }
            catch { }
        }

        private static bool LooksLikePlaceholder(string text)
        {
            if (string.IsNullOrEmpty(text)) return true;
            string trimmed = text.Trim();
            if (trimmed.Length == 0) return true;
            if (trimmed.IndexOf(' ') >= 0) return false;        // real names can have spaces
            if (trimmed.Length >= 18) return true;              // "Playerwithareallylongname" etc
            // Common one-word placeholders the engine ships with:
            return string.Equals(trimmed, "PlayerName", StringComparison.OrdinalIgnoreCase)
                || string.Equals(trimmed, "Username", StringComparison.OrdinalIgnoreCase)
                || trimmed.IndexOf("PlayerName", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("Playerwith", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static string? ReadCurrentText(object? component)
        {
            if (component == null) return null;
            try
            {
                var prop = component.GetType().GetProperty(
                    "text",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (prop != null && prop.CanRead)
                    return prop.GetValue(component) as string;
            }
            catch { }

            foreach (string fieldName in new[] { "m_text", "_text" })
            {
                try
                {
                    var field = component.GetType().GetField(
                        fieldName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                    if (field != null && field.FieldType == typeof(string))
                        return field.GetValue(component) as string;
                }
                catch { }
            }
            return null;
        }

        private static void ActivateAncestorsUpTo(GameObject row)
        {
            try
            {
                Transform? t = row.transform;
                int safety = 0;
                while (t != null && safety++ < 8)
                {
                    GameObject go = t.gameObject;
                    if (!go.activeSelf)
                    {
                        try { go.SetActive(true); }
                        catch { }
                    }
                    t = t.parent;
                }
            }
            catch { }
        }

        private static void DisableProfileButtons(GameObject row)
        {
            try
            {
                foreach (var transform in EnumerateTransforms(row.transform))
                {
                    var selectable = transform?.gameObject != null
                        ? SafeGetComponent<Selectable>(transform.gameObject)
                        : null;
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

        private static bool SetTextInChildByName(GameObject root, string normalizedName, string value)
        {
            GameObject? child = FindChildByNormalizedName(root, normalizedName);
            if (child == null)
                return false;

            return SetTextOnGameObject(child, value);
        }

        private static bool SetTextInChildByNamePart(
            GameObject root,
            string normalizedSubstring,
            string value,
            string? excludeContains = null)
        {
            try
            {
                foreach (var transform in EnumerateTransforms(root.transform))
                {
                    if (transform == null)
                        continue;

                    string name = NormalizeName(transform.gameObject.name);
                    if (name.IndexOf(normalizedSubstring, StringComparison.OrdinalIgnoreCase) < 0)
                        continue;

                    if (!string.IsNullOrEmpty(excludeContains)
                        && name.IndexOf(excludeContains, StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    if (SetTextOnGameObject(transform.gameObject, value))
                        return true;
                }
            }
            catch { }

            return false;
        }

        private static bool SetFirstTextInRow(GameObject root, string value)
        {
            try
            {
                foreach (var transform in EnumerateTransforms(root.transform))
                {
                    if (transform == null)
                        continue;

                    string name = NormalizeName(transform.gameObject.name);
                    if (name.IndexOf("ping", StringComparison.OrdinalIgnoreCase) >= 0)
                        continue;

                    if (SetTextOnGameObject(transform.gameObject, value))
                        return true;
                }
            }
            catch { }

            return false;
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

            // IL2CPP-Interop wrappers for TMP_Text expose `text` as a property
            // *on the base class* (TMP_Text). Without FlattenHierarchy the
            // property lookup fails on the derived TextMeshProUGUI wrapper
            // and our SetValue silently no-ops, leaving the prefab placeholder
            // ("Playerwithareallylongname") visible. Walk the hierarchy first;
            // fall back to the m_text serialized field if the property path
            // doesn't bind.
            bool wrote = false;
            try
            {
                var t = textObject.GetType();
                var prop = t.GetProperty(
                    "text",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                if (prop != null && prop.CanWrite)
                {
                    prop.SetValue(textObject, text);
                    wrote = true;
                }
            }
            catch { }

            if (!wrote)
            {
                foreach (string fieldName in new[] { "m_text", "_text" })
                {
                    try
                    {
                        var field = textObject.GetType().GetField(
                            fieldName,
                            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                        if (field != null && field.FieldType == typeof(string))
                        {
                            field.SetValue(textObject, text);
                            wrote = true;
                            break;
                        }
                    }
                    catch { }
                }
            }

            try { GetGameObjectFromValue(textObject)?.SetActive(true); }
            catch { }

            // Force a re-render on TMP_Text so a directly-written backing
            // field actually paints.
            if (wrote)
            {
                try
                {
                    var setLayoutDirty = textObject.GetType().GetMethod(
                        "SetLayoutDirty",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                    setLayoutDirty?.Invoke(textObject, null);
                }
                catch { }
                try
                {
                    var setVerticesDirty = textObject.GetType().GetMethod(
                        "SetVerticesDirty",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.FlattenHierarchy);
                    setVerticesDirty?.Invoke(textObject, null);
                }
                catch { }
            }
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

        // IL2CPP-interop GameObject only exposes GetComponent(Il2CppSystem.Type)
        // and GetComponent(string). Calling GetComponent with a managed
        // System.Type throws MissingMethodException at the binder before any
        // try/catch can run. Route every game-type lookup through the string
        // overload (game types resolved by short name) and every Unity type
        // through the generic overload (which the interop does provide).
        private static object? GetComponentByType(GameObject? go, Type? type)
        {
            if (go == null || type == null)
                return null;

            if (type == typeof(Text))            return SafeGetComponent<Text>(go);
            if (type == typeof(Selectable))      return SafeGetComponent<Selectable>(go);
            if (type == typeof(Graphic))         return SafeGetComponent<Graphic>(go);

            return SafeGetComponentByTypeName(go, type.Name);
        }

        private static T? SafeGetComponent<T>(GameObject go) where T : Component
        {
            try { return go.GetComponent<T>(); }
            catch { return null; }
        }

        private static object? SafeGetComponentByTypeName(GameObject go, string typeName)
        {
            if (string.IsNullOrEmpty(typeName))
                return null;
            try { return go.GetComponent(typeName); }
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
