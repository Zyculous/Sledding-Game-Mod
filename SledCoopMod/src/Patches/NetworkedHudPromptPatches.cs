using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SledCoopMod.Patches
{
    [HarmonyPatch]
    internal static class Patch_ActionPromptIndicatorUI_NetworkedGlyphFallback
    {
        private static bool _logged;
        private static readonly System.Collections.Generic.HashSet<string> _diagnostics =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        static MethodBase? TargetMethod()
        {
            var type = PatchHelpers.SafeTypeByName("ActionPromptIndicatorUI");
            return type == null ? null : PatchHelpers.FindMethod(type, "SetIndicator");
        }

        [HarmonyPostfix]
        static void Postfix(object __instance, bool __0, string __1)
        {
            if (!NetworkedPlayerIdentity.Enabled || !SceneWatcher.IsInGameplayScene || !__0)
                return;

            string action = ResolveAction(__instance, __1);
            if (string.IsNullOrWhiteSpace(action))
                return;

            string button = GetButtonLabel(action);
            string label = action == "Snowball" ? "Pick up snowball" : "Place marker";
            string newText = $"[{button}] {label}";
            string oldText = GetPromptText(__instance);
            SetPromptText(__instance, newText);
            EnsurePromptLayout(__instance);
            LogPromptChange(action, __instance, oldText, newText);

            if (!_logged)
            {
                _logged = true;
                Plugin.Log.LogInfo("[NetworkedHudPrompt] Applied fallback key labels to snowball/marker HUD prompts for networked instances.");
            }
        }

        private static string ResolveAction(object prompt, string message)
        {
            string text = (message ?? "").ToLowerInvariant();
            string names = GetHierarchyNames(ReflectionHelper.GetGameObject(prompt)).ToLowerInvariant();
            string combined = $"{text} {names}";

            if (combined.IndexOf("snowball", StringComparison.OrdinalIgnoreCase) >= 0
                || combined.IndexOf("grabsnow", StringComparison.OrdinalIgnoreCase) >= 0)
                return "Snowball";

            if ((combined.IndexOf("respawn", StringComparison.OrdinalIgnoreCase) >= 0
                    && combined.IndexOf("place", StringComparison.OrdinalIgnoreCase) >= 0)
                || combined.IndexOf("anchorplace", StringComparison.OrdinalIgnoreCase) >= 0
                || combined.IndexOf("placemarker", StringComparison.OrdinalIgnoreCase) >= 0
                || combined.IndexOf("markerplace", StringComparison.OrdinalIgnoreCase) >= 0)
                return "PlaceMarker";

            return "";
        }

        private static string GetButtonLabel(string action)
        {
            AssignedInputDevice device = AssignedInputDevice.KeyboardWASD;
            try
            {
                var manager = NetworkedInstanceManager.Instance;
                if (manager != null && manager.AssignedDevice != AssignedInputDevice.None)
                    device = manager.AssignedDevice;
            }
            catch { }

            return device switch
            {
                AssignedInputDevice.KeyboardArrows => action == "Snowball" ? "Num7" : "Num3",
                AssignedInputDevice.Gamepad0 => action == "Snowball" ? "X" : "R3",
                AssignedInputDevice.Gamepad1 => action == "Snowball" ? "X" : "R3",
                AssignedInputDevice.Gamepad2 => action == "Snowball" ? "X" : "R3",
                AssignedInputDevice.Gamepad3 => action == "Snowball" ? "X" : "R3",
                _ => action == "Snowball" ? "G" : "R",
            };
        }

        private static void SetPromptText(object prompt, string text)
        {
            object? messageText = GetField(prompt, "messageText");
            if (messageText != null)
            {
                try
                {
                    var prop = messageText.GetType().GetProperty(
                        "text",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    prop?.SetValue(messageText, text);
                }
                catch { }

                try { ReflectionHelper.GetGameObject(messageText)?.SetActive(true); }
                catch { }
                return;
            }

            SetFirstChildText(ReflectionHelper.GetGameObject(prompt), text);
        }

        private static string GetPromptText(object prompt)
        {
            object? messageText = GetField(prompt, "messageText");
            string text = GetTextProperty(messageText);
            if (!string.IsNullOrWhiteSpace(text))
                return text;

            GameObject? root = ReflectionHelper.GetGameObject(prompt);
            if (root == null)
                return "";

            foreach (var component in GetKnownTextComponentsInChildren(root))
            {
                text = GetTextProperty(component);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            return "";
        }

        private static void EnsurePromptLayout(object prompt)
        {
            GameObject? go = ReflectionHelper.GetGameObject(prompt);
            if (go == null)
                return;

            try
            {
                var rect = go.GetComponent<RectTransform>();
                if (rect != null)
                {
                    rect.localScale = Vector3.one;
                    if (rect.sizeDelta.y < 40f)
                        rect.sizeDelta = new Vector2(rect.sizeDelta.x, 40f);
                }
            }
            catch { }

            try { NetworkedUiState.NormalizeHudIndicatorsIfNeeded(); }
            catch { }
        }

        private static void SetFirstChildText(GameObject? root, string text)
        {
            if (root == null)
                return;

            foreach (var component in GetKnownTextComponentsInChildren(root))
            {
                if (component == null)
                    continue;

                try
                {
                    var prop = component.GetType().GetProperty(
                        "text",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    prop?.SetValue(component, text);
                    ReflectionHelper.GetGameObject(component)?.SetActive(true);
                    return;
                }
                catch { }
            }
        }

        private static System.Collections.Generic.List<object> GetKnownTextComponentsInChildren(GameObject root)
        {
            var result = new System.Collections.Generic.List<object>();
            foreach (var transform in EnumerateTransforms(root.transform))
            {
                if (transform == null)
                    continue;

                foreach (string typeName in new[] { "TMPro.TMP_Text", "TMPro.TextMeshProUGUI", "UnityEngine.UI.Text" })
                {
                    object? component = GetComponentByType(transform.gameObject, ResolveComponentType(typeName));
                    if (component != null)
                        result.Add(component);
                }
            }

            return result;
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
                return typeof(UnityEngine.UI.Text);

            return PatchHelpers.SafeTypeByName(typeName);
        }

        private static string GetHierarchyNames(GameObject? go)
        {
            if (go == null)
                return "";

            var builder = new System.Text.StringBuilder();
            try
            {
                Transform? t = go.transform;
                while (t != null)
                {
                    if (builder.Length > 0)
                        builder.Append(' ');
                    builder.Append(t.gameObject.name);
                    t = t.parent;
                }
            }
            catch { }

            return builder.ToString();
        }

        private static void LogPromptChange(string action, object prompt, string oldText, string newText)
        {
            GameObject? go = ReflectionHelper.GetGameObject(prompt);
            string path = GetHierarchyPath(go);
            string key = $"{action}:{path}:{newText}";
            if (!_diagnostics.Add(key))
                return;

            string rectInfo = "<no-rect>";
            try
            {
                var rect = go?.GetComponent<RectTransform>();
                if (rect != null)
                    rectInfo = $"anchorMin={rect.anchorMin} anchorMax={rect.anchorMax} pivot={rect.pivot} pos={rect.anchoredPosition} size={rect.sizeDelta} scale={rect.localScale}";
            }
            catch { }

            Plugin.Log.LogInfo($"[NetworkedHudPrompt] Fallback prompt action={action} path='{path}' old='{oldText}' new='{newText}' rect='{rectInfo}'.");
        }

        private static string GetHierarchyPath(GameObject? go)
        {
            if (go == null)
                return "<no-object>";

            var parts = new System.Collections.Generic.List<string>();
            try
            {
                Transform? t = go.transform;
                while (t != null)
                {
                    parts.Add(t.gameObject.name ?? "<unnamed>");
                    t = t.parent;
                }

                parts.Reverse();
                return string.Join("/", parts);
            }
            catch { return go.name ?? "<unknown>"; }
        }

        private static string GetTextProperty(object? component)
        {
            if (component == null) return "";
            try
            {
                var prop = component.GetType().GetProperty(
                    "text",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return prop?.GetValue(component) as string ?? "";
            }
            catch { return ""; }
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
    }
}
