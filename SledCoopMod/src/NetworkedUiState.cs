using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using SledCoopMod.Patches;

namespace SledCoopMod
{
    internal static class NetworkedUiState
    {
        private static int _lastLoadingClearFrame;
        private static int _lastPauseSelectFrame;
        private static bool _loadingClearLogged;
        private static bool _startupClearLogged;
        private static bool _orphanedStartupClearLogged;
        private static bool _mainMenuReturnLogged;
        private static bool _mainMenuBootOpenLogged;
        private static bool _pauseSelectLogged;
        private static bool _hudIndicatorNormalizeLogged;
        private static int _lastHudIndicatorNormalizeFrame;
        private static int _forceStartupCleanupUntilFrame;
        private static int _lastBootMainMenuAttemptFrame;
        private static readonly System.Collections.Generic.HashSet<string> _clearedUiDiagnostics =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);
        private static readonly System.Collections.Generic.HashSet<string> _hudPromptDiagnostics =
            new System.Collections.Generic.HashSet<string>(StringComparer.Ordinal);

        public static bool IsNativeMenuOpen()
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return false;

            object? ui = GetUiReferenceController();
            if (ui == null) return false;

            try
            {
                object? result = Call(ui, "GetIsInMenu");
                if (result is bool inMenu && inMenu)
                    return true;
            }
            catch { }

            try
            {
                var field = ui.GetType().GetField("isMenuActive", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field?.GetValue(ui) is bool fieldValue && fieldValue)
                    return true;
            }
            catch { }

            return IsMenuPanelActive(ui, "pauseMenu")
                || IsMenuPanelActive(ui, "settingsMenu")
                || IsMenuPanelActive(ui, "quitAreYouSureMenu")
                || IsMenuPanelActive(ui, "showPlayersMenu")
                || IsMenuPanelActive(ui, "statsPanel");
        }

        public static void ApplyMenuCursorState()
        {
            if (!IsNativeMenuOpen())
                return;

            try
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            catch { }

            SelectPauseMenuDefault();
        }

        public static void ClearStuckLoadingIfNeeded()
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return;

            if (Time.frameCount - _lastLoadingClearFrame < 30)
                return;

            _lastLoadingClearFrame = Time.frameCount;

            // The mod skips the EOSAuthenticator boot flow, so the game's loading
            // panel and stray "LOADING" text never get cleared by the normal boot
            // path. Always nuke them — at boot we additionally force the main
            // menu open so the player can actually see something interactable.
            bool inGameplay = SceneWatcher.IsInGameplayScene
                || NetworkedInstanceManager.ShouldHideNetworkedOverlay
                || Time.frameCount < _forceStartupCleanupUntilFrame;

            object? ui = GetUiReferenceController();
            if (ui == null)
            {
                ClearOrphanedStartupUiObjects();
                ClearAllRuntimeStartupUiObjects();
                return;
            }

            try { Call(ui, "DisableLoading"); }
            catch { }

            try
            {
                var loadingState = ui.GetType().GetField("_isLoading", BindingFlags.Instance | BindingFlags.NonPublic);
                loadingState?.SetValue(ui, false);
            }
            catch { }

            GameObject? loadingPanel = GetMenuPanel(ui, "loading");
            if (loadingPanel != null && loadingPanel.activeSelf)
            {
                loadingPanel.SetActive(false);
                if (!_loadingClearLogged)
                {
                    _loadingClearLogged = true;
                    Plugin.Log.LogInfo("[NetworkedUiState] Cleared stuck native loading panel.");
                }
            }

            // Always strip orphaned LOADING text/objects, even on the boot scene.
            ClearOrphanedStartupUiObjects();
            ClearAllRuntimeStartupUiObjects();

            if (inGameplay)
            {
                ClearStartupAndLoadingObjects(ui);
            }
            else
            {
                EnsureMainMenuOpenAtBoot(ui);
            }
        }

        private static void EnsureMainMenuOpenAtBoot(object ui)
        {
            // Only retry once per second so we don't fight the game's own UI events.
            if (Time.frameCount - _lastBootMainMenuAttemptFrame < 60)
                return;
            _lastBootMainMenuAttemptFrame = Time.frameCount;

            // If a meaningful menu (main menu, lobby flow, settings) is already
            // active, don't touch anything — the player has navigated somewhere.
            foreach (string fieldName in new[]
            {
                "mainMenu",
                "createLobby",
                "lobbyExplorer",
                "lobbiesViewer",
                "lobbySettingsMenu",
                "hostLobbyConfirmInternetMenu",
                "passwordEnterMenu",
                "passwordPopup",
                "settingsMenu",
                "quitAreYouSureMenu",
                "showPlayersMenu",
                "statsPanel",
                "pauseMenu",
            })
            {
                if (IsMenuPanelActive(ui, fieldName))
                {
                    if (string.Equals(fieldName, "mainMenu", StringComparison.Ordinal) && !_mainMenuBootOpenLogged)
                    {
                        _mainMenuBootOpenLogged = true;
                        Plugin.Log.LogInfo("[NetworkedUiState] Native main menu already active after boot; nothing to force.");
                    }
                    return;
                }
            }

            bool opened = false;

            try
            {
                object? mainMenu = GetFieldValue(ui, "mainMenu");
                if (mainMenu != null)
                {
                    var openMenu = ui.GetType().GetMethod(
                        "OpenMenu",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (openMenu != null)
                    {
                        openMenu.Invoke(ui, new[] { mainMenu });
                        opened = true;
                    }
                }
            }
            catch { }

            if (!opened)
            {
                GameObject? mainMenuPanel = GetMenuPanel(ui, "mainMenu");
                if (mainMenuPanel != null)
                {
                    try
                    {
                        mainMenuPanel.SetActive(true);
                        opened = true;
                    }
                    catch { }
                }
            }

            if (opened)
            {
                try
                {
                    Cursor.lockState = CursorLockMode.None;
                    Cursor.visible = true;
                }
                catch { }

                if (!_mainMenuBootOpenLogged)
                {
                    _mainMenuBootOpenLogged = true;
                    Plugin.Log.LogInfo("[NetworkedUiState] Forced native main menu open after EOS-skip boot.");
                }
            }
        }

        public static void ForceStartupUiCleanup(string reason)
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return;

            if (!SceneWatcher.IsInGameplayScene && !NetworkedInstanceManager.ShouldHideNetworkedOverlay)
                return;

            // Extend the force-cleanup window to ~30 s at 60 fps. Belt-and-braces
            // alongside the source-level prefix patches in
            // NetworkedLoadingTextPatches.cs and NetworkedLobbyExplorerPatches.cs:
            // those stop new writes, this nukes anything that landed before the
            // patches engaged.
            _forceStartupCleanupUntilFrame = Math.Max(_forceStartupCleanupUntilFrame, Time.frameCount + 1800);

            try
            {
                object? ui = GetUiReferenceController();
                if (ui != null)
                    ClearStartupAndLoadingObjects(ui);
                else
                {
                    ClearOrphanedStartupUiObjects();
                    ClearAllRuntimeStartupUiObjects();
                }
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedUiState] Startup UI cleanup '{reason}' failed once: {e.GetType().Name}: {e.Message}");
            }
        }

        public static void NormalizeHudIndicatorsIfNeeded()
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured || !SceneWatcher.IsInGameplayScene)
                return;

            if (Time.frameCount - _lastHudIndicatorNormalizeFrame < 60)
                return;

            _lastHudIndicatorNormalizeFrame = Time.frameCount;

            object? ui = GetUiReferenceController();
            if (ui == null)
            {
                NormalizeAllHudIndicatorObjects();
                return;
            }

            object? inGameHud = GetMemberValue(ui, "inGameHUD");
            object? indicators = GetMemberValue(inGameHud, "indicators");

            if (indicators == null)
            {
                GameObject? hudGo = ReflectionHelper.GetGameObject(inGameHud);
                indicators = FindComponentByTypeName(hudGo, "_Scripts.UI.In_Game.UIHUD_Indicators");
            }

            if (indicators == null)
            {
                NormalizeAllHudIndicatorObjects();
                return;
            }

            bool changed = NormalizeHudIndicators(indicators);
            changed |= NormalizeAllHudIndicatorObjects();

            if (changed && !_hudIndicatorNormalizeLogged)
            {
                _hudIndicatorNormalizeLogged = true;
                Plugin.Log.LogInfo("[NetworkedUiState] Restored native HUD action prompt layout for networked gameplay.");
            }
        }

        public static void ReturnToMainMenuAfterNetworkLeave()
        {
            object? ui = GetUiReferenceController();
            if (ui == null) return;

            try { Call(ui, "DisableLoading"); }
            catch { }

            try
            {
                var loadingState = ui.GetType().GetField("_isLoading", BindingFlags.Instance | BindingFlags.NonPublic);
                loadingState?.SetValue(ui, false);
            }
            catch { }

            try { Call(ui, "CloseAllOpenMenus", false); }
            catch { }

            ClearStartupAndLoadingObjects(ui);

            bool opened = false;
            try
            {
                object? mainMenu = GetFieldValue(ui, "mainMenu");
                var openMenu = ui.GetType().GetMethod("OpenMenu", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (openMenu != null && mainMenu != null)
                {
                    openMenu.Invoke(ui, new[] { mainMenu });
                    opened = true;
                }
            }
            catch { }

            if (!opened)
            {
                try
                {
                    GameObject? mainMenuPanel = GetMenuPanel(ui, "mainMenu");
                    if (mainMenuPanel != null)
                    {
                        mainMenuPanel.SetActive(true);
                        opened = true;
                    }
                }
                catch { }
            }

            try
            {
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            catch { }

            if (opened && !_mainMenuReturnLogged)
            {
                _mainMenuReturnLogged = true;
                Plugin.Log.LogInfo("[NetworkedUiState] Returned native UI to the main menu after networked lobby leave.");
            }
        }

        private static void ClearStartupAndLoadingObjects(object ui)
        {
            bool changed = false;

            // Plain GameObject fields on UiReferenceController.
            foreach (string fieldName in new[]
            {
                "startUpCanvas",
                "questionPanel_VoiceChat",
                "questionPanel_TextChat",
            })
            {
                changed |= DeactivateFieldGameObject(ui, fieldName);
            }

            // UiToggleableMenu wrappers — pause/in-game HUD must stay alive,
            // but loading/lobby/main-menu/start-up overlays should not stay
            // active once gameplay has started.
            foreach (string menuField in new[]
            {
                "loading",
                "confirmInternet",
                "confirmMeanies",
                "mainMenu",
                "createLobby",
                "lobbyExplorer",
                "lobbiesViewer",
                "lobbySettingsMenu",
                "hostLobbyConfirmInternetMenu",
                "passwordEnterMenu",
                "passwordPopup",
                "gamePreview",
            })
            {
                changed |= DeactivateMenuPanel(ui, menuField);
            }

            changed |= DeactivateFieldGameObject(ui, "_loading");
            changed |= ClearStartupUiOwnerFields("SteamLobbyListManager");
            changed |= ClearStartupUiOwnerFields("_Scripts.UI.Pre_Game.UILobbyExplorer");
            changed |= ClearOrphanedStartupUiObjects();
            changed |= ClearAllRuntimeStartupUiObjects();

            if (changed && !_startupClearLogged)
            {
                _startupClearLogged = true;
                Plugin.Log.LogInfo("[NetworkedUiState] Cleared native startup/loading UI objects after networked gameplay started.");
            }
        }

        private static bool ClearOrphanedStartupUiObjects()
        {
            bool changed = false;
            int count = 0;

            GameObject[] objects = FindAllGameObjects();
            if (objects.Length == 0)
                return false;

            foreach (var go in objects)
            {
                if (go == null)
                    continue;

                try
                {
                    if (!go.scene.IsValid())
                        continue;

                    if (IsSledCoopObject(go))
                        continue;

                    bool startupText = LooksLikeStartupUiText(go);
                    bool startupObject = !startupText && HasCanvasParent(go) && LooksLikeStartupUiObject(go);
                    if (startupText || startupObject)
                    {
                        LogClearedUiObject(go, startupText ? GetAnyText(go) : "");
                        go.SetActive(false);
                        changed = true;
                        count++;
                    }
                }
                catch { }
            }

            changed |= ClearStartupTextComponentsOfType(PatchHelpers.SafeTypeByName("TMPro.TMP_Text"), ref count, includeInactive: false);
            changed |= ClearStartupTextComponentsOfType(typeof(Text), ref count, includeInactive: false);

            if (changed && !_orphanedStartupClearLogged)
            {
                _orphanedStartupClearLogged = true;
                Plugin.Log.LogInfo($"[NetworkedUiState] Cleared {count} orphaned loading/lobby UI object(s) from networked gameplay.");
            }

            return changed;
        }

        private static bool ClearAllRuntimeStartupUiObjects()
        {
            bool changed = false;
            int count = 0;

            changed |= ClearStartupTextComponentsOfType(PatchHelpers.SafeTypeByName("TMPro.TMP_Text"), ref count, includeInactive: true);
            changed |= ClearStartupTextComponentsOfType(typeof(Text), ref count, includeInactive: true);
            changed |= ClearStartupTextComponentsOfType(typeof(Graphic), ref count, includeInactive: true);

            foreach (var obj in FindUnityObjectsOfTypeAll(typeof(GameObject)))
            {
                if (obj is not GameObject go || go == null)
                    continue;

                try
                {
                    if (!go.scene.IsValid() || IsSledCoopObject(go))
                        continue;

                    bool startupText = LooksLikeStartupUiText(go);
                    bool startupObject = HasCanvasParent(go) && LooksLikeStartupUiObject(go);
                    if (!startupText && !startupObject)
                        continue;

                    LogClearedUiObject(go, GetAnyText(go));
                    ClearTextComponentsUnder(go);
                    if (go.activeSelf)
                        go.SetActive(false);
                    changed = true;
                    count++;
                }
                catch { }
            }

            if (changed && !_orphanedStartupClearLogged)
            {
                _orphanedStartupClearLogged = true;
                Plugin.Log.LogInfo($"[NetworkedUiState] Cleared {count} runtime loading/lobby UI object(s) from networked gameplay.");
            }

            return changed;
        }

        private static GameObject[] FindAllGameObjects()
        {
            try
            {
                var canvases = FindUnityObjectsOfType(typeof(Canvas));
                var result = new System.Collections.Generic.List<GameObject>();
                var seen = new System.Collections.Generic.HashSet<int>();

                foreach (var obj in canvases)
                {
                    var canvas = obj as Canvas;
                    if (canvas == null)
                        continue;

                    AddTransformTree(canvas.transform, result, seen);
                }

                return result.ToArray();
            }
            catch
            {
                try
                {
                    var canvases = FindUnityObjectsOfType(typeof(Canvas));
                    var result = new System.Collections.Generic.List<GameObject>();
                    foreach (var obj in canvases)
                    {
                        var canvas = obj as Canvas;
                        if (canvas != null)
                            result.Add(canvas.gameObject);
                    }
                    return result.ToArray();
                }
                catch { return Array.Empty<GameObject>(); }
            }
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

        private static UnityEngine.Object[] FindUnityObjectsOfTypeAll(Type? type)
        {
            if (type == null)
                return Array.Empty<UnityEngine.Object>();

            try
            {
                var method = typeof(Resources).GetMethod(
                    "FindObjectsOfTypeAll",
                    BindingFlags.Static | BindingFlags.Public,
                    null,
                    new[] { typeof(Type) },
                    null);

                return method?.Invoke(null, new object[] { type }) as UnityEngine.Object[]
                    ?? Array.Empty<UnityEngine.Object>();
            }
            catch { return Array.Empty<UnityEngine.Object>(); }
        }

        private static void AddTransformTree(
            Transform? transform,
            System.Collections.Generic.List<GameObject> result,
            System.Collections.Generic.HashSet<int> seen)
        {
            if (transform == null)
                return;

            try
            {
                GameObject go = transform.gameObject;
                if (go != null)
                {
                    int id = go.GetInstanceID();
                    if (seen.Add(id))
                        result.Add(go);
                }
            }
            catch { }

            int childCount;
            try { childCount = transform.childCount; }
            catch { return; }

            for (int i = 0; i < childCount; i++)
            {
                try { AddTransformTree(transform.GetChild(i), result, seen); }
                catch { }
            }
        }

        private static bool NormalizeIndicatorPanel(GameObject panel)
        {
            bool changed = false;

            try
            {
                var rect = panel.GetComponent<RectTransform>();
                if (rect != null)
                {
                    changed |= SetVector2(rect.anchorMin, Vector2.one, value => rect.anchorMin = value);
                    changed |= SetVector2(rect.anchorMax, Vector2.one, value => rect.anchorMax = value);
                    changed |= SetVector2(rect.pivot, Vector2.one, value => rect.pivot = value);
                    changed |= SetVector2(rect.anchoredPosition, new Vector2(-24f, -24f), value => rect.anchoredPosition = value);

                    Vector2 size = rect.sizeDelta;
                    float width = Mathf.Max(size.x, 537.4602f);
                    float height = Mathf.Max(size.y, 331.8494f);
                    if (!Approximately(size.x, width) || !Approximately(size.y, height))
                    {
                        rect.sizeDelta = new Vector2(width, height);
                        changed = true;
                    }
                }

                var layout = panel.GetComponent<VerticalLayoutGroup>();
                if (layout != null)
                {
                    var padding = layout.padding ?? new RectOffset();
                    if (padding.right != 50 || padding.top != 15)
                    {
                        padding.right = 50;
                        padding.top = 15;
                        layout.padding = padding;
                        changed = true;
                    }

                    if (!Approximately(layout.spacing, 15f))
                    {
                        layout.spacing = 15f;
                        changed = true;
                    }

                    if (layout.childAlignment != TextAnchor.UpperRight)
                    {
                        layout.childAlignment = TextAnchor.UpperRight;
                        changed = true;
                    }

                    if (!layout.childForceExpandWidth)
                    {
                        layout.childForceExpandWidth = true;
                        changed = true;
                    }

                    if (!layout.childControlWidth)
                    {
                        layout.childControlWidth = true;
                        changed = true;
                    }
                }
            }
            catch { }

            return changed;
        }

        private static bool NormalizeIndicatorChild(GameObject? go)
        {
            if (go == null)
                return false;

            bool changed = false;
            try
            {
                var rect = go.GetComponent<RectTransform>();
                if (rect == null)
                    return false;

                Vector2 size = rect.sizeDelta;
                if (size.y < 40f)
                {
                    rect.sizeDelta = new Vector2(size.x, 40f);
                    changed = true;
                }
            }
            catch { }

            return changed;
        }

        private static bool NormalizeAllHudIndicatorObjects()
        {
            bool changed = false;
            Type? type = PatchHelpers.SafeTypeByName("_Scripts.UI.In_Game.UIHUD_Indicators");
            if (type == null)
                type = PatchHelpers.SafeTypeByName("UIHUD_Indicators");

            if (type == null)
                return false;

            foreach (var obj in FindUnityObjectsOfType(type))
                changed |= NormalizeHudIndicators(obj);

            return changed;
        }

        private static bool NormalizeHudIndicators(object? indicators)
        {
            if (indicators == null)
                return false;

            bool changed = false;
            GameObject? panel = GetGameObjectFromValue(GetMemberValue(indicators, "indicatorsPanel"));
            if (panel != null)
                changed |= NormalizeIndicatorPanel(panel);

            foreach (string fieldName in new[]
            {
                "indicator_grabSnowball",
                "indicator_respawnAnchorPlace",
            })
            {
                changed |= NormalizeIndicatorChild(GetGameObjectFromValue(GetMemberValue(indicators, fieldName)));
            }

            changed |= NormalizeActionPromptFallback(indicators, "indicator_grabSnowball", "Snowball");
            changed |= NormalizeActionPromptFallback(indicators, "indicator_respawnAnchorPlace", "PlaceMarker");
            return changed;
        }

        private static bool NormalizeActionPromptFallback(object indicators, string fieldName, string action)
        {
            object? value = GetMemberValue(indicators, fieldName);
            GameObject? go = GetGameObjectFromValue(value);
            object? prompt = value;

            if (prompt == null || prompt is GameObject)
                prompt = FindComponentByTypeName(go, "ActionPromptIndicatorUI");

            if (prompt == null)
                return false;

            string button = GetPromptButtonLabel(action);
            string label = action == "Snowball" ? "Pick up snowball" : "Place marker";
            string newText = $"[{button}] {label}";
            string oldText = GetPromptText(prompt);
            bool changed = SetPromptText(prompt, newText);

            try
            {
                GameObject? promptGo = ReflectionHelper.GetGameObject(prompt) ?? go;
                var rect = promptGo?.GetComponent<RectTransform>();
                if (rect != null)
                {
                    if (rect.localScale != Vector3.one)
                    {
                        rect.localScale = Vector3.one;
                        changed = true;
                    }

                    if (rect.sizeDelta.y < 40f)
                    {
                        rect.sizeDelta = new Vector2(rect.sizeDelta.x, 40f);
                        changed = true;
                    }
                }

                if (changed)
                    LogHudPromptNormalization(action, promptGo, oldText, newText, rect);
            }
            catch { }

            return changed;
        }

        private static bool SetPromptText(object prompt, string text)
        {
            object? messageText = GetMemberValue(prompt, "messageText");
            if (messageText == null)
                return SetFirstChildText(ReflectionHelper.GetGameObject(prompt), text);

            string current = GetTextProperty(messageText);
            if (string.Equals(current, text, StringComparison.Ordinal))
                return false;

            try
            {
                var prop = messageText.GetType().GetProperty(
                    "text",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                prop?.SetValue(messageText, text);
                return true;
            }
            catch { return false; }
        }

        private static string GetPromptText(object prompt)
        {
            object? messageText = GetMemberValue(prompt, "messageText");
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

        private static void LogHudPromptNormalization(string action, GameObject? go, string oldText, string newText, RectTransform? rect)
        {
            string path = go != null ? GetHierarchyPath(go) : "<no-object>";
            string rectInfo = "<no-rect>";
            try
            {
                if (rect != null)
                    rectInfo = $"anchorMin={rect.anchorMin} anchorMax={rect.anchorMax} pivot={rect.pivot} pos={rect.anchoredPosition} size={rect.sizeDelta} scale={rect.localScale}";
            }
            catch { }

            string key = $"{action}:{path}:{newText}";
            if (!_hudPromptDiagnostics.Add(key))
                return;

            Plugin.Log.LogInfo($"[NetworkedUiState] Normalized HUD prompt action={action} path='{path}' old='{oldText}' new='{newText}' rect='{rectInfo}'.");
        }

        private static bool SetFirstChildText(GameObject? root, string text)
        {
            if (root == null)
                return false;

            foreach (var component in GetKnownTextComponentsInChildren(root))
            {
                if (component == null)
                    continue;

                string current = GetTextProperty(component);
                if (string.Equals(current, text, StringComparison.Ordinal))
                    return false;

                ClearOrSetTextProperty(component, text);
                ReflectionHelper.GetGameObject(component)?.SetActive(true);
                return true;
            }

            return false;
        }

        private static string GetPromptButtonLabel(string action)
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

        private static bool ClearStartupUiOwnerFields(string typeName)
        {
            bool changed = false;
            Type? type = PatchHelpers.SafeTypeByName(typeName);
            if (type == null)
                return false;

            foreach (var owner in FindUnityObjectsOfType(type))
            {
                if (owner == null)
                    continue;

                changed |= DeactivateFieldGameObject(owner, "textChatOnlyLobbyWarning");
                changed |= DeactivateFieldGameObject(owner, "fullscreenLoadingIndicator");
                changed |= DeactivateFieldGameObject(owner, "loadingIndicator");
                changed |= DeactivateFieldGameObject(owner, "loading");

                try
                {
                    var field = owner.GetType().GetField("searchingForOnlyTextChatLobbies", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null && field.FieldType == typeof(bool))
                        field.SetValue(owner, false);
                }
                catch { }
            }

            return changed;
        }

        private static bool SetVector2(Vector2 current, Vector2 value, Action<Vector2> setter)
        {
            if (Approximately(current.x, value.x) && Approximately(current.y, value.y))
                return false;

            setter(value);
            return true;
        }

        private static bool Approximately(float a, float b) =>
            Mathf.Abs(a - b) < 0.01f;

        private static GameObject? GetGameObjectFromValue(object? value)
        {
            GameObject? go = value as GameObject;
            return go ?? ReflectionHelper.GetGameObject(value);
        }

        private static object? FindComponentByTypeName(GameObject? root, string typeName)
        {
            if (root == null)
                return null;

            try
            {
                return FindComponentByTypeName(root.transform, typeName);
            }
            catch { }

            return null;
        }

        private static object? FindComponentByTypeName(Transform? transform, string typeName)
        {
            if (transform == null)
                return null;

            object? component = GetComponentByType(transform.gameObject, ResolveComponentType(typeName));
            if (component != null)
                return component;

            int childCount;
            try { childCount = transform.childCount; }
            catch { return null; }

            for (int i = 0; i < childCount; i++)
            {
                object? found = null;
                try { found = FindComponentByTypeName(transform.GetChild(i), typeName); }
                catch { }

                if (found != null)
                    return found;
            }

            return null;
        }

        private static bool IsSledCoopObject(GameObject go)
        {
            try
            {
                Transform? t = go.transform;
                while (t != null)
                {
                    string name = t.gameObject.name ?? "";
                    if (name.StartsWith("SledCoop", StringComparison.Ordinal))
                        return true;
                    t = t.parent;
                }
            }
            catch { }

            return false;
        }

        private static bool LooksLikeStartupUiObject(GameObject go)
        {
            string name = go.name ?? "";
            string normalizedName = NormalizeUiToken(name);
            if (normalizedName.Equals("loading", StringComparison.OrdinalIgnoreCase)
                || normalizedName.Equals("textchatonlyindicatoronoff", StringComparison.OrdinalIgnoreCase))
                return true;

            foreach (string token in new[]
            {
                "textChatOnlyLobbyWarning",
                "searchingForOnlyTextChatLobbies",
                "textChatOnlyLobby",
                "Text Chat Only",
                "TEXT CHAT ONLY LOBBIES",
                "TEXT ONLY CHAT LOBBIES",
                "TEXT HAT ONLY LOBBIES",
                "questionPanel_TextChat",
                "questionPanel_VoiceChat",
                "lobbiesViewer",
                "lobbyExplorer",
                "createLobby",
                "hostLobbyConfirmInternetMenu",
                "loadingText",
                "loadingThrobber",
                "Loading Text",
                "UI_Loading",
                "_loading",
            })
            {
                if (name.IndexOf(token, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;

                string normalizedToken = NormalizeUiToken(token);
                if (normalizedToken.Length > 0 && normalizedName.IndexOf(normalizedToken, StringComparison.OrdinalIgnoreCase) >= 0)
                    return true;
            }

            return name.Equals("loading", StringComparison.OrdinalIgnoreCase)
                || name.Equals("Loading", StringComparison.OrdinalIgnoreCase);
        }

        private static bool LooksLikeStartupUiText(GameObject go)
        {
            return LooksLikeStartupUiTextValue(GetStartupText(go));
        }

        private static bool LooksLikeStartupUiTextValue(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return false;

            string trimmed = text.Trim();
            string normalized = NormalizeUiToken(trimmed);
            if (trimmed.Equals("Loading", StringComparison.OrdinalIgnoreCase)
                || trimmed.Equals("Loading...", StringComparison.OrdinalIgnoreCase)
                || (trimmed.Length <= 24 && trimmed.StartsWith("Loading", StringComparison.OrdinalIgnoreCase))
                || normalized.Equals("loading", StringComparison.OrdinalIgnoreCase)
                || normalized.Equals("loadingloading", StringComparison.OrdinalIgnoreCase))
                return true;

            bool mentionsTextChat = trimmed.IndexOf("text chat", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("text only", StringComparison.OrdinalIgnoreCase) >= 0
                || trimmed.IndexOf("text hat only", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("textonlychat", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("onlytextchat", StringComparison.OrdinalIgnoreCase) >= 0;
            bool mentionsLobby = trimmed.IndexOf("lobb", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("lobb", StringComparison.OrdinalIgnoreCase) >= 0;
            return (mentionsTextChat && mentionsLobby)
                || normalized.IndexOf("textchatonlylobbies", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("textonlychatlobbies", StringComparison.OrdinalIgnoreCase) >= 0
                || normalized.IndexOf("texthatonlylobbies", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        private static bool ClearStartupTextComponentsOfType(Type? type, ref int count, bool includeInactive)
        {
            if (type == null)
                return false;

            bool changed = false;
            UnityEngine.Object[] objects = includeInactive
                ? FindUnityObjectsOfTypeAll(type)
                : FindUnityObjectsOfType(type);

            foreach (var obj in objects)
            {
                if (obj == null)
                    continue;

                try
                {
                    if (!LooksLikeStartupUiTextValue(GetTextProperty(obj)))
                        continue;

                    GameObject? go = ReflectionHelper.GetGameObject(obj);
                    if (go == null || IsSledCoopObject(go))
                        continue;

                    string oldText = GetTextProperty(obj);
                    LogClearedUiObject(go, oldText);
                    ClearTextProperty(obj);
                    if (go.activeSelf)
                        go.SetActive(false);
                    count++;
                    changed = true;
                }
                catch { }
            }

            return changed;
        }

        private static void ClearTextProperty(object? component)
        {
            ClearOrSetTextProperty(component, "");
        }

        private static bool ClearOrSetTextProperty(object? component, string value)
        {
            if (component == null) return false;
            try
            {
                var prop = component.GetType().GetProperty(
                    "text",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop != null)
                {
                    prop.SetValue(component, value);
                    return true;
                }
            }
            catch { }

            foreach (string fieldName in new[] { "m_text", "_text", "text" })
            {
                try
                {
                    var field = component.GetType().GetField(
                        fieldName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field != null && field.FieldType == typeof(string))
                    {
                        field.SetValue(component, value);
                        return true;
                    }
                }
                catch { }
            }

            return false;
        }

        private static string GetStartupText(GameObject? go)
        {
            if (go == null)
                return "";

            foreach (var component in GetKnownTextComponents(go))
            {
                string text = GetTextProperty(component);
                if (!string.IsNullOrWhiteSpace(text))
                    return text;
            }

            return "";
        }

        private static bool ClearStartupText(GameObject? go)
        {
            if (go == null)
                return false;

            bool changed = false;
            foreach (var component in GetKnownTextComponents(go))
            {
                if (!LooksLikeStartupUiTextValue(GetTextProperty(component)))
                    continue;

                changed |= ClearOrSetTextProperty(component, "");
            }

            return changed;
        }

        private static System.Collections.Generic.List<object> GetKnownTextComponentsInChildren(GameObject root)
        {
            var result = new System.Collections.Generic.List<object>();
            foreach (var transform in EnumerateTransforms(root.transform))
            {
                if (transform == null)
                    continue;

                result.AddRange(GetKnownTextComponents(transform.gameObject));
            }

            return result;
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

        private static string GetAnyText(GameObject? go)
        {
            if (go == null)
                return "";

            return GetStartupText(go);
        }

        private static void LogClearedUiObject(GameObject? go, string text)
        {
            if (go == null)
                return;

            string path = GetHierarchyPath(go);
            string key = $"{path}:{text}";
            if (!_clearedUiDiagnostics.Add(key))
                return;

            string cleanText = (text ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
            if (cleanText.Length > 80)
                cleanText = cleanText.Substring(0, 80);

            Plugin.Log.LogInfo($"[NetworkedUiState] Cleared startup UI object path='{path}' text='{cleanText}'.");
        }

        private static string GetHierarchyPath(GameObject go)
        {
            try
            {
                var parts = new System.Collections.Generic.List<string>();
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

        private static bool HasCanvasParent(GameObject go)
        {
            try
            {
                Transform? t = go.transform;
                while (t != null)
                {
                    try
                    {
                        if (t.gameObject.GetComponent<Canvas>() != null)
                            return true;
                    }
                    catch { }

                    t = t.parent;
                }
            }
            catch { }

            return false;
        }

        private static string NormalizeUiToken(string text)
        {
            if (string.IsNullOrWhiteSpace(text))
                return "";

            var chars = new char[text.Length];
            int count = 0;
            foreach (char c in text)
            {
                if (char.IsLetterOrDigit(c))
                    chars[count++] = char.ToLowerInvariant(c);
            }

            return new string(chars, 0, count);
        }

        private static string GetTextProperty(object? component)
        {
            if (component == null) return "";
            try
            {
                var prop = component.GetType().GetProperty(
                    "text",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (prop?.GetValue(component) is string text)
                    return text;
            }
            catch { }

            foreach (string fieldName in new[] { "m_text", "_text", "text" })
            {
                try
                {
                    var field = component.GetType().GetField(
                        fieldName,
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (field?.GetValue(component) is string text)
                        return text;
                }
                catch { }
            }

            return "";
        }

        private static bool DeactivateMenuPanel(object ui, string menuFieldName)
        {
            GameObject? panel = GetMenuPanel(ui, menuFieldName);
            if (panel == null || !panel.activeSelf) return false;
            panel.SetActive(false);
            return true;
        }

        private static bool DeactivateFieldGameObject(object owner, string fieldName)
        {
            object? value = GetFieldValue(owner, fieldName);
            GameObject? go = value as GameObject;
            go ??= ReflectionHelper.GetGameObject(value);

            if (go == null)
                return false;

            string oldText = GetAnyText(go);
            LogClearedUiObject(go, oldText);
            ClearTextComponentsUnder(go);

            if (!go.activeSelf)
                return false;

            go.SetActive(false);
            return true;
        }

        private static void ClearTextComponentsUnder(GameObject root)
        {
            try
            {
                foreach (var transform in EnumerateTransforms(root.transform))
                {
                    if (transform == null)
                        continue;

                    ClearStartupText(transform.gameObject);
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

        private static void SelectPauseMenuDefault()
        {
            if (Time.frameCount - _lastPauseSelectFrame < 15)
                return;

            _lastPauseSelectFrame = Time.frameCount;

            object? ui = GetUiReferenceController();
            if (ui == null) return;

            GameObject? panel = GetMenuPanel(ui, "pauseMenu");
            if (panel == null || !panel.activeInHierarchy)
                return;

            var eventSystem = EventSystem.current;
            if (eventSystem == null)
                return;

            GameObject? selected = eventSystem.currentSelectedGameObject;
            if (selected != null && selected.transform != null && selected.transform.IsChildOf(panel.transform))
                return;

            GameObject? first = GetMenuFirstSelectable(ui, "pauseMenu") ?? FindFirstSelectable(panel);
            if (first == null)
                return;

            eventSystem.SetSelectedGameObject(first);
            if (!_pauseSelectLogged)
            {
                _pauseSelectLogged = true;
                Plugin.Log.LogInfo("[NetworkedUiState] Selected native pause menu default control and released gameplay cursor.");
            }
        }

        private static GameObject? FindFirstSelectable(GameObject root)
        {
            try
            {
                foreach (var transform in EnumerateTransforms(root.transform))
                {
                    var selectable = transform?.gameObject.GetComponent(typeof(Selectable)) as Selectable;
                    if (selectable != null && selectable.gameObject.activeInHierarchy && selectable.interactable)
                        return selectable.gameObject;
                }
            }
            catch { }

            return null;
        }

        private static bool IsMenuPanelActive(object ui, string fieldName)
        {
            GameObject? panel = GetMenuPanel(ui, fieldName);
            return panel != null && panel.activeInHierarchy;
        }

        private static GameObject? GetMenuPanel(object ui, string fieldName)
        {
            object? menu = GetFieldValue(ui, fieldName);
            return GetStructField(menu, "panel") as GameObject;
        }

        private static GameObject? GetMenuFirstSelectable(object ui, string fieldName)
        {
            object? menu = GetFieldValue(ui, fieldName);
            return GetStructField(menu, "firstSelectable") as GameObject;
        }

        private static object? GetFieldValue(object instance, string fieldName)
        {
            try
            {
                var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(instance);
            }
            catch { return null; }
        }

        private static object? GetMemberValue(object? instance, string memberName)
        {
            if (instance == null)
                return null;

            try
            {
                var field = instance.GetType().GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                if (field != null)
                    return field.GetValue(instance);
            }
            catch { }

            try
            {
                var prop = instance.GetType().GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return prop?.GetValue(instance);
            }
            catch { return null; }
        }

        private static object? GetStructField(object? instance, string fieldName)
        {
            if (instance == null) return null;
            try
            {
                var field = instance.GetType().GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return field?.GetValue(instance);
            }
            catch { return null; }
        }

        private static object? Call(object instance, string methodName, params object[] args)
        {
            try
            {
                var method = instance.GetType().GetMethod(methodName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return method?.Invoke(instance, args.Length == 0 ? null : args);
            }
            catch { return null; }
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
    }

    public class NetworkedUiStateManager : MonoBehaviour
    {
        private static bool _updateErrorLogged;

        private void Awake()
        {
            DontDestroyOnLoad(gameObject);
        }

        private void Update()
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return;

            try
            {
                NetworkedUiState.ClearStuckLoadingIfNeeded();
                NetworkedUiState.NormalizeHudIndicatorsIfNeeded();
                NetworkedUiState.ApplyMenuCursorState();
            }
            catch (Exception e)
            {
                if (_updateErrorLogged)
                    return;

                _updateErrorLogged = true;
                Plugin.Log.LogWarning($"[NetworkedUiState] UI state update failed once; continuing without aborting gameplay: {e.GetType().Name}: {e.Message}");
            }
        }
    }
}
