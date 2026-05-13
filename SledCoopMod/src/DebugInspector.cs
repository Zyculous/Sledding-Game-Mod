using System;
using System.Collections.Generic;
using System.Reflection;
using System.Text;
using UnityEngine;
using UnityEngine.UI;

namespace SledCoopMod
{
    /// <summary>
    /// Middle-click UI inspector. UI-only — meant to identify residual UI
    /// elements that the cleanup pass missed (e.g. the leftover "LOADING" /
    /// "TEXT CHAT ONLY LOBBIES" text). Uses only IL2CPP-Interop-safe APIs:
    ///
    ///   - per-type <c>GetComponent&lt;T&gt;()</c> generic + string-keyed
    ///     <c>GetComponent(string)</c> (never <c>GetComponent(Type)</c>,
    ///     never <c>GetComponents()</c> no-arg, never
    ///     <c>RectTransform.GetWorldCorners(Vector3[])</c>);
    ///   - manual rect-corner math via <c>RectTransform.TransformPoint</c>;
    ///   - multi-path canvas discovery (generic + reflective + scene-root
    ///     walk) so a single failing path can't blank the picker.
    ///
    /// Middle-click collects every RectTransform that contains the cursor,
    /// sorted by canvas <c>sortingOrder</c> + tree depth, with Graphic-bearing
    /// objects floated to the front. The inspector panel cycles through the
    /// candidates so even ambiguous picks (multiple stacked panels) reveal
    /// every GameObject beneath the cursor in turn.
    /// </summary>
    public class DebugInspector : MonoBehaviour
    {
        public static DebugInspector? Instance { get; private set; }

        // Components we know how to probe. Kept as plain strings because
        // GameObject.GetComponent(string) is the IL2CPP-Interop-safe lookup
        // for game-namespaced types and works equally well for Unity builtins
        // by short name.
        private static readonly string[] s_probedComponents =
        {
            // Layout / structure
            "RectTransform", "Canvas", "CanvasGroup", "CanvasRenderer",
            "GraphicRaycaster", "ContentSizeFitter", "AspectRatioFitter",
            "Mask", "RectMask2D",
            "HorizontalLayoutGroup", "VerticalLayoutGroup", "GridLayoutGroup",
            "LayoutElement",
            // Visuals
            "Image", "RawImage", "Text", "TextMeshProUGUI", "TextMeshPro",
            // Inputs
            "Button", "Toggle", "Slider", "Scrollbar", "ScrollRect", "InputField",
            "TMP_InputField",
            // Game-specific UI we already patch
            "UIPanel", "UIPausePanel", "UIHUD", "UILoading", "UILoadingTextAnimation",
            "UILobbyExplorer", "SteamLobbyListManager",
            "PlayerListDisplayUI", "PlayersListNameItem",
            "_Scripts.UI.Misc.UILoading",
            "_Scripts.UI.Components.UIPanel",
            "_Scripts.UI.In_Game.UIPausePanel",
            "_Scripts.UI.In_Game.UIHUD",
            "_Scripts.UI.Pre_Game.UILobbyExplorer",
            // Behaviour
            "Animator", "Animation", "AudioSource",
        };

        private List<GameObject> _candidates = new List<GameObject>();
        private int _candidateIndex;
        private GameObject? _pickedGo;
        private string _pickedPath = "";
        private string _pickedComponents = "";
        private string _pickedText = "";
        private RectTransform? _pickedRect;
        private GUIStyle? _labelStyle;
        private GUIStyle? _headerStyle;
        private GUIStyle? _smallStyle;
        private Texture2D? _outlineTex;
        private Texture2D? _bgTex;

        // Diagnostic counters.
        private bool _enableLoggedOn;
        private bool _outlineEnableLoggedOn;
        private int _middleDownLegacyHits;
        private int _middleDownInputSystemHits;

        // Outline-all-UI cache. Rebuilt on a slow cadence (every
        // ~30 frames) from Update; OnGUI just reads it.
        private struct OutlineEntry
        {
            public RectTransform Rt;
            public GameObject Go;
            public Rect ScreenRect;     // GUI-space (top-left origin)
            public string ShortName;
            public bool HasGraphic;
            public int SortOrder;
        }
        private readonly List<OutlineEntry> _outlineCache = new List<OutlineEntry>();
        private int _lastOutlineRebuildFrame;
        private const int OutlineRebuildIntervalFrames = 30;
        private const int OutlineMaxEntries = 256;
        private const float OutlineMinRectSize = 8f;     // px
        private GUIStyle? _outlineLabelStyle;
        private GUIStyle? _outlineTooltipBgStyle;

        // InputSystem fallback (legacy Input is dead on this Unity 6 IL2CPP
        // build when Active Input Handling = Input System).
        private bool _inputSystemProbed;
        private object? _inputSystemMouse;
        private PropertyInfo? _inputSystemMiddleButtonProp;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
            DontDestroyOnLoad(gameObject);
            Plugin.Log.LogInfo($"[DebugInspector] Awake: enabled={ModConfig.DebugInspectEnabled.Value}");
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_outlineTex != null) Destroy(_outlineTex);
            if (_bgTex != null) Destroy(_bgTex);
        }

        // ── Update + input plumbing ────────────────────────────────────────────────

        private void Update()
        {
            UpdateOutlineCache();

            bool enabled = ModConfig.DebugInspectEnabled.Value;

            if (enabled && !_enableLoggedOn)
            {
                _enableLoggedOn = true;
                Plugin.Log.LogInfo("[DebugInspector] Polling middle-click for UI pick. F8 clears, F9 cycles candidate.");
            }
            else if (!enabled && _enableLoggedOn)
            {
                _enableLoggedOn = false;
                ClearPick();
            }

            if (!enabled) return;

            try { if (Input.GetKeyDown(KeyCode.F8)) { ClearPick(); return; } }
            catch { }
            try { if (Input.GetKeyDown(KeyCode.F9)) { CycleCandidate(+1); return; } }
            catch { }

            bool legacyDown = false;
            try { legacyDown = Input.GetMouseButtonDown(2); }
            catch { }

            if (legacyDown)
            {
                _middleDownLegacyHits++;
                Plugin.Log.LogInfo($"[DebugInspector] Middle click (legacy) hits={_middleDownLegacyHits}");
                PickAtMouse();
                return;
            }

            if (InputSystemMiddleClickedThisFrame())
            {
                _middleDownInputSystemHits++;
                Plugin.Log.LogInfo($"[DebugInspector] Middle click (InputSystem) hits={_middleDownInputSystemHits}");
                PickAtMouse();
            }
        }

        // ── Outline-all-UI: cache + render ────────────────────────────────────────

        private void UpdateOutlineCache()
        {
            bool outlineOn = ModConfig.DebugOutlineAllUi.Value;

            if (outlineOn && !_outlineEnableLoggedOn)
            {
                _outlineEnableLoggedOn = true;
                Plugin.Log.LogInfo("[DebugInspector] Outline-all-UI ON; refreshing every 30 frames.");
            }
            else if (!outlineOn && _outlineEnableLoggedOn)
            {
                _outlineEnableLoggedOn = false;
                _outlineCache.Clear();
            }

            if (!outlineOn) return;
            if (Time.frameCount - _lastOutlineRebuildFrame < OutlineRebuildIntervalFrames) return;
            _lastOutlineRebuildFrame = Time.frameCount;

            RebuildOutlineCache();
        }

        // Rebuild cache: walk every canvas's RectTransform tree, score each
        // RT, and keep only those that are visible, on-screen, and big
        // enough to draw a label on. Capped at OutlineMaxEntries to keep
        // OnGUI cheap.
        private void RebuildOutlineCache()
        {
            _outlineCache.Clear();

            float screenW = Screen.width;
            float screenH = Screen.height;

            var canvases = CollectCanvases();
            int rectsScanned = 0;

            foreach (var canvas in canvases)
            {
                if (canvas == null) continue;

                int sortOrder = 0;
                Camera? canvasCam = null;
                try
                {
                    sortOrder = canvas.sortingOrder;
                    if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                        canvasCam = canvas.worldCamera ?? Camera.main;
                }
                catch { }

                Transform? root = null;
                try { root = canvas.transform; }
                catch { continue; }

                foreach (var rt in EnumerateRectTransforms(root))
                {
                    rectsScanned++;
                    if (_outlineCache.Count >= OutlineMaxEntries) break;

                    GameObject go;
                    try { go = rt.gameObject; }
                    catch { continue; }
                    if (go == null) continue;
                    if (!IsActiveInHierarchy(go)) continue;
                    if (IsModUiObject(go)) continue;

                    Rect screenRect;
                    try { screenRect = ComputeRectInGuiSpace(rt, canvasCam); }
                    catch { continue; }

                    if (screenRect.width < OutlineMinRectSize || screenRect.height < OutlineMinRectSize)
                        continue;
                    if (screenRect.xMax < 0 || screenRect.xMin > screenW) continue;
                    if (screenRect.yMax < 0 || screenRect.yMin > screenH) continue;

                    bool hasGraphic = HasAnyGraphic(go);

                    _outlineCache.Add(new OutlineEntry
                    {
                        Rt = rt,
                        Go = go,
                        ScreenRect = screenRect,
                        ShortName = SafeName(go),
                        HasGraphic = hasGraphic,
                        SortOrder = sortOrder,
                    });
                }

                if (_outlineCache.Count >= OutlineMaxEntries) break;
            }
        }

        private static string SafeName(GameObject go)
        {
            try
            {
                string n = go.name;
                if (string.IsNullOrEmpty(n)) return "<unnamed>";
                return n.Length > 40 ? n.Substring(0, 40) + "…" : n;
            }
            catch { return "<unnamed>"; }
        }

        // Compute screen-space rect (GUI coords: top-left origin) for a
        // RectTransform. Uses RectTransform.rect + TransformPoint instead
        // of GetWorldCorners(Vector3[]) since the array-out overload
        // doesn't bind in IL2CPP-Interop.
        private static Rect ComputeRectInGuiSpace(RectTransform rt, Camera? cam)
        {
            Rect localRect = rt.rect;
            Vector3 bl = rt.TransformPoint(new Vector3(localRect.xMin, localRect.yMin, 0f));
            Vector3 br = rt.TransformPoint(new Vector3(localRect.xMax, localRect.yMin, 0f));
            Vector3 tl = rt.TransformPoint(new Vector3(localRect.xMin, localRect.yMax, 0f));
            Vector3 tr = rt.TransformPoint(new Vector3(localRect.xMax, localRect.yMax, 0f));

            Vector2 sBl = WorldOrOverlayToGuiSpace(bl, cam);
            Vector2 sBr = WorldOrOverlayToGuiSpace(br, cam);
            Vector2 sTl = WorldOrOverlayToGuiSpace(tl, cam);
            Vector2 sTr = WorldOrOverlayToGuiSpace(tr, cam);

            float minX = Mathf.Min(Mathf.Min(sBl.x, sBr.x), Mathf.Min(sTl.x, sTr.x));
            float maxX = Mathf.Max(Mathf.Max(sBl.x, sBr.x), Mathf.Max(sTl.x, sTr.x));
            float minY = Mathf.Min(Mathf.Min(sBl.y, sBr.y), Mathf.Min(sTl.y, sTr.y));
            float maxY = Mathf.Max(Mathf.Max(sBl.y, sBr.y), Mathf.Max(sTl.y, sTr.y));

            return new Rect(minX, minY, maxX - minX, maxY - minY);
        }

        private bool InputSystemMiddleClickedThisFrame()
        {
            EnsureInputSystemProbed();
            if (_inputSystemMouse == null || _inputSystemMiddleButtonProp == null) return false;

            try
            {
                object? button = _inputSystemMiddleButtonProp.GetValue(_inputSystemMouse);
                if (button == null) return false;

                var prop = button.GetType().GetProperty(
                    "wasPressedThisFrame",
                    BindingFlags.Instance | BindingFlags.Public);
                return prop?.GetValue(button) is bool b && b;
            }
            catch { return false; }
        }

        private void EnsureInputSystemProbed()
        {
            if (_inputSystemProbed) return;
            _inputSystemProbed = true;

            try
            {
                Type? mouseType = Type.GetType("UnityEngine.InputSystem.Mouse, Unity.InputSystem")
                    ?? SledCoopMod.Patches.PatchHelpers.SafeTypeByName("UnityEngine.InputSystem.Mouse");
                if (mouseType == null) return;

                var currentProp = mouseType.GetProperty("current", BindingFlags.Public | BindingFlags.Static);
                _inputSystemMouse = currentProp?.GetValue(null);
                _inputSystemMiddleButtonProp = mouseType.GetProperty(
                    "middleButton",
                    BindingFlags.Instance | BindingFlags.Public);

                Plugin.Log.LogInfo(
                    $"[DebugInspector] InputSystem mouse probe: mouse={(_inputSystemMouse != null ? "found" : "null")} middleButton={(_inputSystemMiddleButtonProp != null ? "found" : "null")}");
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[DebugInspector] InputSystem probe failed: {e.GetType().Name}: {e.Message}");
            }
        }

        private Vector2 ResolveMousePosition()
        {
            try
            {
                Vector3 v = Input.mousePosition;
                if (v.x != 0f || v.y != 0f) return new Vector2(v.x, v.y);
            }
            catch { }

            EnsureInputSystemProbed();
            if (_inputSystemMouse == null) return Vector2.zero;

            try
            {
                var positionProp = _inputSystemMouse.GetType().GetProperty(
                    "position",
                    BindingFlags.Instance | BindingFlags.Public);
                object? control = positionProp?.GetValue(_inputSystemMouse);
                if (control == null) return Vector2.zero;

                var readMethod = control.GetType().GetMethod(
                    "ReadValue",
                    BindingFlags.Instance | BindingFlags.Public,
                    null,
                    Type.EmptyTypes,
                    null);
                if (readMethod?.Invoke(control, null) is Vector2 v2) return v2;
            }
            catch { }
            return Vector2.zero;
        }

        // ── Pick ───────────────────────────────────────────────────────────────────

        private void PickAtMouse()
        {
            Vector2 mousePos = ResolveMousePosition();
            Plugin.Log.LogInfo($"[DebugInspector] Picking at screen pos ({mousePos.x:0}, {mousePos.y:0})");

            int rectsTested = 0;
            int canvasCount = 0;
            var hits = CollectCandidates(mousePos, ref rectsTested, ref canvasCount);

            Plugin.Log.LogInfo(
                $"[DebugInspector] UI pick: canvases={canvasCount} rectsTested={rectsTested} candidates={hits.Count}");

            if (hits.Count == 0)
            {
                ClearPick();
                return;
            }

            _candidates = hits;
            _candidateIndex = 0;
            ApplyCurrentCandidate();
        }

        private List<GameObject> CollectCandidates(Vector2 mousePos, ref int rectsTested, ref int canvasCount)
        {
            var scored = new List<(GameObject go, int score, bool hasGraphic, RectTransform rt)>();
            var canvases = CollectCanvases();

            foreach (var canvas in canvases)
            {
                canvasCount++;
                if (canvas == null) continue;

                int sortOrder = 0;
                Camera? canvasCam = null;
                try
                {
                    sortOrder = canvas.sortingOrder;
                    if (canvas.renderMode != RenderMode.ScreenSpaceOverlay)
                        canvasCam = canvas.worldCamera ?? Camera.main;
                }
                catch { }

                Transform? root = null;
                try { root = canvas.transform; }
                catch { continue; }

                foreach (var rt in EnumerateRectTransforms(root))
                {
                    rectsTested++;

                    GameObject go;
                    try { go = rt.gameObject; }
                    catch { continue; }
                    if (go == null) continue;
                    if (!IsActiveInHierarchy(go)) continue;
                    if (IsModUiObject(go)) continue;

                    bool inside;
                    try { inside = RectTransformUtility.RectangleContainsScreenPoint(rt, mousePos, canvasCam); }
                    catch { continue; }
                    if (!inside) continue;

                    bool hasGraphic = HasAnyGraphic(go);
                    int depth = ComputeDepthInCanvas(rt);
                    int score = sortOrder * 10000 + depth;
                    scored.Add((go, score, hasGraphic, rt));
                }
            }

            // Sort: graphic-bearing first, then by score descending.
            scored.Sort((a, b) =>
            {
                if (a.hasGraphic != b.hasGraphic) return b.hasGraphic.CompareTo(a.hasGraphic);
                return b.score.CompareTo(a.score);
            });

            var result = new List<GameObject>(scored.Count);
            var seen = new HashSet<int>();
            foreach (var s in scored)
            {
                int id;
                try { id = s.go.GetInstanceID(); }
                catch { continue; }
                if (!seen.Add(id)) continue;
                result.Add(s.go);
            }
            return result;
        }

        private static bool IsActiveInHierarchy(GameObject go)
        {
            try { return go.activeInHierarchy; }
            catch { return false; }
        }

        // Walk every RectTransform under `root` (inclusive) using a manual
        // stack. Avoids GetComponentsInChildren<RectTransform>(true) which
        // returns a typed array that may not marshal in IL2CPP-Interop.
        private static IEnumerable<RectTransform> EnumerateRectTransforms(Transform root)
        {
            if (root == null) yield break;

            var stack = new Stack<Transform>();
            stack.Push(root);

            int safety = 0;
            while (stack.Count > 0 && safety++ < 100000)
            {
                Transform t = stack.Pop();
                if (t == null) continue;

                RectTransform? rt = t as RectTransform;
                if (rt == null)
                {
                    // Not every Transform is a RectTransform; try GetComponent
                    // as a defensive fallback (cost is negligible).
                    try { rt = t.gameObject.GetComponent<RectTransform>(); }
                    catch { rt = null; }
                }
                if (rt != null) yield return rt;

                int childCount;
                try { childCount = t.childCount; }
                catch { continue; }

                for (int i = 0; i < childCount; i++)
                {
                    Transform? child = null;
                    try { child = t.GetChild(i); }
                    catch { }
                    if (child != null) stack.Push(child);
                }
            }
        }

        // Canvas discovery. Two paths, both built on IL2CPP-Interop-safe
        // primitives — no generic Object.FindObjectsOfType<T>() (the binder
        // can't resolve that overload on this Unity build), no
        // Scene.GetRootGameObjects() (also missing).
        //
        //   Path 1 — UiReferenceController singleton: the game's own UI
        //            registry. Every menu / HUD field is a UiToggleableMenu
        //            (panel : GameObject), and we walk to a parent Canvas.
        //            Game-native, no IL2CPP-Interop probing required.
        //
        //   Path 2 — Reflective Object.FindObjectsOfType(Type): the same
        //            invoke-via-MethodInfo dance NetworkedUiState already
        //            uses successfully for the cleanup pass.
        //
        // Each path lives in its own static helper so the binder only
        // resolves its call sites when that helper is invoked.
        private static List<Canvas> CollectCanvases()
        {
            var result = new List<Canvas>();
            var seen = new HashSet<int>();

            int viaGame = TryAddCanvasesFromGame(result, seen);
            if (viaGame > 0)
                Plugin.Log.LogInfo($"[DebugInspector] Canvas discovery via UiReferenceController: {viaGame}");

            int viaReflective = TryAddCanvasesFromReflective(result, seen);
            if (viaReflective > 0)
                Plugin.Log.LogInfo($"[DebugInspector] Canvas discovery via reflective FindObjectsOfType(Canvas): {viaReflective}");

            if (result.Count == 0)
                Plugin.Log.LogWarning("[DebugInspector] Canvas discovery yielded zero canvases on both paths.");

            return result;
        }

        // ── Path 1: walk UiReferenceController singleton ──────────────────────────

        // Field names on UiReferenceController whose values are
        // UiToggleableMenu (panel + firstSelectable) — taken from the
        // monodis dump at SledCoopMod/docs/00_overview.md and the existing
        // patches in NetworkedUiState.cs.
        private static readonly string[] s_uiReferenceMenuFields =
        {
            "mainMenu", "createLobby", "lobbyExplorer", "lobbyExplorerMenu",
            "lobbiesViewer", "lobbySettingsMenu", "hostLobbyConfirmInternetMenu",
            "passwordEnterMenu", "passwordPopup",
            "loading", "confirmInternet", "confirmMeanies",
            "racingPanel", "settingsPanel", "mapPanel",
            "pauseMenu", "settingsMenu", "creditsMenu", "blockedPlayersMenu",
            "controlMapperMenu", "quitAreYouSureMenu", "resetAllSettingsAreYouSure",
            "showPlayersMenu", "actionsMenu",
            "trinketEditingPanel", "trinketListPanel", "trinketSellAreYouSureMenu",
            "statsPanel", "reportPlayerMenu", "reportPlayer", "gamePreview",
            "interactionMenus",
        };

        // Direct GameObject fields on UiReferenceController (not wrapped in
        // UiToggleableMenu).
        private static readonly string[] s_uiReferenceCanvasFields =
        {
            "startUpCanvas", "playerActiveCanvas",
            "questionPanel_VoiceChat", "questionPanel_TextChat",
            "_loading",
        };

        private static int TryAddCanvasesFromGame(List<Canvas> sink, HashSet<int> seen)
        {
            object? ui = SafeGetUiReferenceController();
            if (ui == null) return 0;

            int added = 0;

            foreach (string field in s_uiReferenceMenuFields)
            {
                GameObject? panel = SafeGetMenuPanel(ui, field);
                if (panel == null) continue;
                added += TryAddCanvasFromGameObject(panel, sink, seen);
            }

            foreach (string field in s_uiReferenceCanvasFields)
            {
                GameObject? go = SafeGetFieldAsGameObject(ui, field);
                if (go == null) continue;
                added += TryAddCanvasFromGameObject(go, sink, seen);
            }

            // Also try inGameHUD which is a MonoBehaviour rather than a
            // panel — its gameObject hosts the in-game canvas.
            object? hud = SafeGetField(ui, "inGameHUD");
            GameObject? hudGo = SledCoopMod.ReflectionHelper.GetGameObject(hud);
            if (hudGo != null) added += TryAddCanvasFromGameObject(hudGo, sink, seen);

            return added;
        }

        private static int TryAddCanvasFromGameObject(GameObject go, List<Canvas> sink, HashSet<int> seen)
        {
            // Walk up the parents looking for the nearest Canvas. Most
            // panels are nested several levels under the canvas root.
            Canvas? canvas = null;
            try
            {
                Transform? t = go.transform;
                int safety = 0;
                while (t != null && safety++ < 32)
                {
                    Canvas? c = null;
                    try { c = t.gameObject.GetComponent<Canvas>(); }
                    catch { c = null; }
                    if (c != null) { canvas = c; break; }
                    t = t.parent;
                }
            }
            catch { return 0; }

            if (canvas == null) return 0;

            int id;
            try { id = canvas.GetInstanceID(); }
            catch { return 0; }
            if (!seen.Add(id)) return 0;

            sink.Add(canvas);
            return 1;
        }

        private static object? SafeGetUiReferenceController()
        {
            try
            {
                var t = SledCoopMod.Patches.PatchHelpers.SafeTypeByName("UiReferenceController");
                return t?.GetProperty("Instance",
                    BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic)?.GetValue(null);
            }
            catch { return null; }
        }

        private static object? SafeGetField(object instance, string fieldName)
        {
            try
            {
                var f = instance.GetType().GetField(
                    fieldName,
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return f?.GetValue(instance);
            }
            catch { return null; }
        }

        private static GameObject? SafeGetFieldAsGameObject(object instance, string fieldName)
        {
            object? value = SafeGetField(instance, fieldName);
            if (value is GameObject g) return g;
            return SledCoopMod.ReflectionHelper.GetGameObject(value);
        }

        private static GameObject? SafeGetMenuPanel(object ui, string menuFieldName)
        {
            object? menu = SafeGetField(ui, menuFieldName);
            if (menu == null) return null;
            try
            {
                var panelField = menu.GetType().GetField(
                    "panel",
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return panelField?.GetValue(menu) as GameObject;
            }
            catch { return null; }
        }

        // ── Path 2: reflective Object.FindObjectsOfType(Type) ─────────────────────

        private static int TryAddCanvasesFromReflective(List<Canvas> sink, HashSet<int> seen)
        {
            UnityEngine.Object[]? objs;
            try { objs = ReflectiveFindObjectsOfType(typeof(Canvas)); }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[DebugInspector] Reflective FindObjectsOfType failed: {e.GetType().Name}: {e.Message}");
                return 0;
            }
            if (objs == null || objs.Length == 0) return 0;

            int added = 0;
            foreach (var o in objs)
            {
                Canvas? c = o as Canvas;
                if (c == null) continue;

                int id;
                try { id = c.GetInstanceID(); }
                catch { continue; }
                if (!seen.Add(id)) continue;

                sink.Add(c);
                added++;
            }
            return added;
        }

        private static UnityEngine.Object[] ReflectiveFindObjectsOfType(Type type)
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

        private static bool IsModUiObject(GameObject go)
        {
            try
            {
                Transform? t = go.transform;
                int safety = 0;
                while (t != null && safety++ < 64)
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

        private static bool HasAnyGraphic(GameObject go)
        {
            try { if (go.GetComponent<Graphic>() != null) return true; }
            catch { }
            try { if (go.GetComponent<Text>() != null) return true; }
            catch { }
            try { if (go.GetComponent("TMP_Text") != null) return true; }
            catch { }
            try { if (go.GetComponent("TextMeshProUGUI") != null) return true; }
            catch { }
            return false;
        }

        private static int ComputeDepthInCanvas(RectTransform rt)
        {
            int depth = 0;
            try
            {
                Transform? t = rt;
                int safety = 0;
                while (t != null && safety++ < 64)
                {
                    depth++;
                    Canvas? c = null;
                    try { c = t.gameObject.GetComponent<Canvas>(); }
                    catch { }
                    if (c != null) break;
                    t = t.parent;
                }
            }
            catch { }
            return depth;
        }

        private void CycleCandidate(int delta)
        {
            if (_candidates.Count == 0) return;
            _candidateIndex = ((_candidateIndex + delta) % _candidates.Count + _candidates.Count) % _candidates.Count;
            ApplyCurrentCandidate();
        }

        private void ApplyCurrentCandidate()
        {
            if (_candidates.Count == 0) { ClearPick(); return; }
            SetPick(_candidates[_candidateIndex]);
        }

        public void SetPick(GameObject go)
        {
            if (go == null) { ClearPick(); return; }

            _pickedGo = go;
            _pickedPath = GetHierarchyPath(go);
            _pickedComponents = ProbeComponents(go);
            _pickedText = ReadAnyVisibleText(go);

            try { _pickedRect = go.GetComponent<RectTransform>(); }
            catch { _pickedRect = null; }

            string textPreview = _pickedText.Length > 80 ? _pickedText.Substring(0, 80) : _pickedText;
            Plugin.Log.LogInfo(
                $"[DebugInspector] Picked '{_pickedPath}' text='{textPreview}' components=[{_pickedComponents}]");
        }

        public void ClearPick()
        {
            _candidates.Clear();
            _candidateIndex = 0;
            _pickedGo = null;
            _pickedPath = "";
            _pickedComponents = "";
            _pickedText = "";
            _pickedRect = null;
        }

        // ── Component probe ───────────────────────────────────────────────────────

        // Walks the component probe list and returns a comma-separated list
        // of types found on the GameObject. Avoids GetComponents<Component>()
        // (no-arg generic) which doesn't bind in IL2CPP-Interop.
        private static string ProbeComponents(GameObject go)
        {
            var sb = new StringBuilder();
            int count = 0;
            foreach (string typeName in s_probedComponents)
            {
                Component? c = null;
                try { c = go.GetComponent(typeName); }
                catch { c = null; }
                if (c == null) continue;

                if (sb.Length > 0) sb.Append(", ");
                sb.Append(typeName);
                count++;
                if (count >= 14) { sb.Append(", …"); break; }
            }
            return sb.ToString();
        }

        private static string GetHierarchyPath(GameObject go)
        {
            try
            {
                var parts = new List<string>();
                Transform? t = go.transform;
                int safety = 0;
                while (t != null && safety++ < 64)
                {
                    string name = string.IsNullOrEmpty(t.gameObject.name) ? "<unnamed>" : t.gameObject.name;
                    parts.Add(name);
                    t = t.parent;
                }
                parts.Reverse();
                return string.Join("/", parts);
            }
            catch { return go.name ?? "<unknown>"; }
        }

        private static string ReadAnyVisibleText(GameObject go)
        {
            try
            {
                var legacy = go.GetComponent<Text>();
                if (legacy != null && !string.IsNullOrEmpty(legacy.text)) return legacy.text;
            }
            catch { }

            string viaTmp = ReadTmpText(go);
            if (!string.IsNullOrEmpty(viaTmp)) return viaTmp;

            // Walk one level of children for text-bearing components — many
            // residual UI elements stash their text in a child GO.
            try
            {
                int n = go.transform.childCount;
                for (int i = 0; i < n; i++)
                {
                    Transform? child = null;
                    try { child = go.transform.GetChild(i); }
                    catch { }
                    if (child == null) continue;

                    try
                    {
                        var legacy = child.gameObject.GetComponent<Text>();
                        if (legacy != null && !string.IsNullOrEmpty(legacy.text)) return legacy.text;
                    }
                    catch { }

                    string childTmp = ReadTmpText(child.gameObject);
                    if (!string.IsNullOrEmpty(childTmp)) return childTmp;
                }
            }
            catch { }

            return "";
        }

        private static string ReadTmpText(GameObject go)
        {
            foreach (string typeName in new[] { "TMP_Text", "TextMeshProUGUI", "TextMeshPro" })
            {
                Component? c = null;
                try { c = go.GetComponent(typeName); }
                catch { c = null; }
                if (c == null) continue;

                try
                {
                    var prop = c.GetType().GetProperty(
                        "text",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                    if (prop?.GetValue(c) is string s) return s ?? "";
                }
                catch { }
            }
            return "";
        }

        // ── Render ────────────────────────────────────────────────────────────────

        private void OnGUI()
        {
            bool inspectorOn = ModConfig.DebugInspectEnabled.Value;
            bool outlineOn = ModConfig.DebugOutlineAllUi.Value;

            if (!inspectorOn && !outlineOn) return;

            EnsureStyles();

            if (outlineOn)
                DrawOutlineOverlay();

            if (inspectorOn)
            {
                DrawHelpHud();
                if (_pickedGo != null)
                {
                    DrawRectOutline();
                    DrawInspectorPanel();
                }
            }
        }

        // ── Outline-all-UI render ─────────────────────────────────────────────────

        // Per OnGUI: tightest enclosing rect under the cursor wins (smallest
        // area that contains the mouse). All other cached rects get a thin
        // border + tiny corner label; the hovered one gets a bright border
        // + a tooltip near the cursor with full info. Reads only the cache;
        // cache rebuild is throttled in Update.
        private void DrawOutlineOverlay()
        {
            if (_outlineCache.Count == 0) return;

            EnsureOutlineStyles();

            Vector2 mouse;
            try { mouse = Event.current != null ? Event.current.mousePosition : Vector2.zero; }
            catch { mouse = Vector2.zero; }

            int hoveredIdx = -1;
            float hoveredArea = float.MaxValue;
            for (int i = 0; i < _outlineCache.Count; i++)
            {
                var r = _outlineCache[i].ScreenRect;
                if (!r.Contains(mouse)) continue;
                float area = r.width * r.height;
                if (area < hoveredArea) { hoveredArea = area; hoveredIdx = i; }
            }

            // Layered colors: thin cyan for plain rects, brighter cyan for
            // graphic-bearing rects, orange for the hovered rect.
            Color thin = new Color(0.25f, 0.85f, 1f, 0.35f);
            Color withGraphic = new Color(0.25f, 0.85f, 1f, 0.65f);
            Color hovered = new Color(1f, 0.6f, 0f, 0.95f);

            for (int i = 0; i < _outlineCache.Count; i++)
            {
                if (i == hoveredIdx) continue;
                var entry = _outlineCache[i];
                DrawBorderAroundRect(entry.ScreenRect, entry.HasGraphic ? withGraphic : thin, 1f);
                DrawCornerLabel(entry.ScreenRect, entry.ShortName, entry.HasGraphic ? withGraphic : thin);
            }

            if (hoveredIdx >= 0)
            {
                var entry = _outlineCache[hoveredIdx];
                DrawBorderAroundRect(entry.ScreenRect, hovered, 2f);
                DrawCornerLabel(entry.ScreenRect, entry.ShortName, hovered);
                DrawHoverTooltip(mouse, entry);
            }
        }

        private void DrawBorderAroundRect(Rect rect, Color color, float thickness)
        {
            if (_outlineTex == null)
            {
                _outlineTex = new Texture2D(1, 1);
                _outlineTex.SetPixel(0, 0, Color.white);
                _outlineTex.Apply();
            }

            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(new Rect(rect.x, rect.y, rect.width, thickness), _outlineTex);                                  // top
            GUI.DrawTexture(new Rect(rect.x, rect.y + rect.height - thickness, rect.width, thickness), _outlineTex);        // bottom
            GUI.DrawTexture(new Rect(rect.x, rect.y, thickness, rect.height), _outlineTex);                                 // left
            GUI.DrawTexture(new Rect(rect.x + rect.width - thickness, rect.y, thickness, rect.height), _outlineTex);        // right
            GUI.color = old;
        }

        private void DrawCornerLabel(Rect rect, string text, Color frameColor)
        {
            if (string.IsNullOrEmpty(text)) return;
            if (rect.width < 32f || rect.height < 14f) return;

            EnsureOutlineStyles();

            // Place label in the top-left corner of the rect, clipped to
            // the screen so it never disappears off-edge.
            float labelW = Mathf.Min(rect.width, 200f);
            var labelRect = new Rect(rect.x + 1f, rect.y + 1f, labelW, 13f);
            if (labelRect.y < 0f) labelRect.y = 0f;

            // Subtle backing so the text is readable over busy backgrounds.
            Color old = GUI.color;
            GUI.color = new Color(0f, 0f, 0f, 0.55f);
            GUI.DrawTexture(labelRect, _outlineTex!);
            GUI.color = frameColor;
            GUI.DrawTexture(new Rect(labelRect.x, labelRect.y + labelRect.height - 1f, labelRect.width, 1f), _outlineTex!);
            GUI.color = old;

            GUI.Label(new Rect(labelRect.x + 2f, labelRect.y - 1f, labelRect.width - 4f, labelRect.height + 2f),
                      text, _outlineLabelStyle);
        }

        private void DrawHoverTooltip(Vector2 mouse, OutlineEntry entry)
        {
            string path = GetHierarchyPath(entry.Go);
            string components = ProbeComponents(entry.Go);
            string text = ReadAnyVisibleText(entry.Go);
            string textPreview = text.Length > 160 ? text.Substring(0, 160) + "…" : text;

            // Position tooltip below-right of cursor, but flip to above /
            // left if it'd overflow the screen.
            float w = 420f;
            float h = string.IsNullOrEmpty(textPreview) ? 90f : 130f;
            float x = mouse.x + 16f;
            float y = mouse.y + 16f;
            if (x + w > Screen.width) x = mouse.x - w - 16f;
            if (y + h > Screen.height) y = mouse.y - h - 16f;
            if (x < 0f) x = 0f;
            if (y < 0f) y = 0f;

            var rect = new Rect(x, y, w, h);
            DrawBackground(rect, new Color(0f, 0f, 0f, 0.92f));

            GUILayout.BeginArea(new Rect(rect.x + 8f, rect.y + 6f, rect.width - 16f, rect.height - 12f));
            GUILayout.Label(entry.ShortName, _headerStyle);
            GUILayout.Label(path, _smallStyle);
            GUILayout.Label($"Components: {components}", _smallStyle);
            if (!string.IsNullOrEmpty(textPreview))
                GUILayout.Label($"Text: \"{textPreview}\"", _labelStyle);
            GUILayout.EndArea();
        }

        private void EnsureOutlineStyles()
        {
            if (_outlineLabelStyle != null) return;

            _outlineLabelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 10,
                alignment = TextAnchor.MiddleLeft,
                clipping = TextClipping.Clip,
                normal = { textColor = new Color(1f, 1f, 1f, 0.95f) },
                padding = new RectOffset(0, 0, 0, 0),
                margin = new RectOffset(0, 0, 0, 0),
            };
            _outlineTooltipBgStyle = new GUIStyle();
        }

        private void DrawHelpHud()
        {
            var rect = new Rect(8f, Screen.height - 56f, 360f, 48f);
            DrawBackground(rect, new Color(0f, 0f, 0f, 0.65f));
            GUILayout.BeginArea(new Rect(rect.x + 6f, rect.y + 4f, rect.width - 12f, rect.height - 8f));
            GUILayout.Label("Debug inspector ON", _headerStyle);
            GUILayout.Label("Middle-click pick · F8 clear · F9 cycle · Ctrl+P menu", _smallStyle);
            GUILayout.EndArea();
        }

        // Manual rect-outline draw. Avoids RectTransform.GetWorldCorners
        // (Vector3[] out parameter doesn't bind in IL2CPP-Interop). Instead
        // we read .rect (already a Rect struct) and TransformPoint each
        // local corner to world space, then to screen space.
        private void DrawRectOutline()
        {
            if (_pickedRect == null) return;

            try
            {
                Rect localRect;
                try { localRect = _pickedRect.rect; }
                catch { return; }

                Vector3 bl = SafeTransformPoint(_pickedRect, new Vector3(localRect.xMin, localRect.yMin, 0f));
                Vector3 tl = SafeTransformPoint(_pickedRect, new Vector3(localRect.xMin, localRect.yMax, 0f));
                Vector3 tr = SafeTransformPoint(_pickedRect, new Vector3(localRect.xMax, localRect.yMax, 0f));
                Vector3 br = SafeTransformPoint(_pickedRect, new Vector3(localRect.xMax, localRect.yMin, 0f));

                Camera? cam = ResolveCanvasCamera(_pickedRect);

                Vector2 sBl = WorldOrOverlayToGuiSpace(bl, cam);
                Vector2 sTl = WorldOrOverlayToGuiSpace(tl, cam);
                Vector2 sTr = WorldOrOverlayToGuiSpace(tr, cam);
                Vector2 sBr = WorldOrOverlayToGuiSpace(br, cam);

                DrawScreenLine(sBl, sTl);
                DrawScreenLine(sTl, sTr);
                DrawScreenLine(sTr, sBr);
                DrawScreenLine(sBr, sBl);
            }
            catch { }
        }

        private static Vector3 SafeTransformPoint(RectTransform rt, Vector3 local)
        {
            try { return rt.TransformPoint(local); }
            catch { return Vector3.zero; }
        }

        private static Vector2 WorldOrOverlayToGuiSpace(Vector3 worldOrOverlay, Camera? cam)
        {
            try
            {
                Vector3 sp = cam != null
                    ? cam.WorldToScreenPoint(worldOrOverlay)
                    : worldOrOverlay; // overlay-canvas world position is already in screen units
                return new Vector2(sp.x, Screen.height - sp.y);
            }
            catch { return Vector2.zero; }
        }

        private static Camera? ResolveCanvasCamera(RectTransform rect)
        {
            try
            {
                Canvas? canvas = null;
                Transform? t = rect;
                int safety = 0;
                while (t != null && safety++ < 64)
                {
                    try { canvas = t.gameObject.GetComponent<Canvas>(); }
                    catch { canvas = null; }
                    if (canvas != null) break;
                    t = t.parent;
                }

                if (canvas == null) return null;
                if (canvas.renderMode == RenderMode.ScreenSpaceOverlay) return null;
                return canvas.worldCamera ?? Camera.main;
            }
            catch { return null; }
        }

        private void DrawScreenLine(Vector2 a, Vector2 b)
        {
            if (_outlineTex == null)
            {
                _outlineTex = new Texture2D(1, 1);
                _outlineTex.SetPixel(0, 0, Color.white);
                _outlineTex.Apply();
            }

            Vector2 d = b - a;
            float len = d.magnitude;
            if (len < 0.5f) return;

            float angle = Mathf.Atan2(d.y, d.x) * Mathf.Rad2Deg;
            var rect = new Rect(a.x, a.y, len, 2f);

            Matrix4x4 prev = GUI.matrix;
            try
            {
                GUIUtility.RotateAroundPivot(angle, a);
                Color old = GUI.color;
                GUI.color = new Color(1f, 0.7f, 0f, 0.95f);
                GUI.DrawTexture(rect, _outlineTex);
                GUI.color = old;
            }
            finally { GUI.matrix = prev; }
        }

        private void DrawInspectorPanel()
        {
            float w = Mathf.Min(680f, Screen.width  - 40f);
            float h = 240f;
            var rect = new Rect(20f, 20f, w, h);
            DrawBackground(rect, new Color(0f, 0f, 0f, 0.85f));

            GUILayout.BeginArea(new Rect(rect.x + 12f, rect.y + 10f, rect.width - 24f, rect.height - 20f));

            string heading = _candidates.Count > 1
                ? $"Picked GameObject  ({_candidateIndex + 1}/{_candidates.Count})"
                : "Picked GameObject";
            GUILayout.Label(heading, _headerStyle);
            GUILayout.Label(_pickedPath, _labelStyle);
            GUILayout.Space(4f);
            GUILayout.Label($"Components: {_pickedComponents}", _smallStyle);
            if (!string.IsNullOrEmpty(_pickedText))
            {
                GUILayout.Space(4f);
                string display = _pickedText.Length > 240 ? _pickedText.Substring(0, 240) + "…" : _pickedText;
                GUILayout.Label($"Text: \"{display}\"", _labelStyle);
            }

            GUILayout.FlexibleSpace();
            GUILayout.BeginHorizontal();
            if (_candidates.Count > 1 && GUILayout.Button("◀ Prev", GUILayout.Width(70f))) CycleCandidate(-1);
            if (_candidates.Count > 1 && GUILayout.Button("Next ▶", GUILayout.Width(70f))) CycleCandidate(+1);
            if (GUILayout.Button("Hide GO", GUILayout.Width(80f))) TrySetActive(_pickedGo, false);
            if (GUILayout.Button("Show GO", GUILayout.Width(80f))) TrySetActive(_pickedGo, true);
            if (GUILayout.Button("Clear text", GUILayout.Width(90f))) TryClearText(_pickedGo);
            if (GUILayout.Button("Walk parent", GUILayout.Width(100f))) WalkParent();
            if (GUILayout.Button("Clear pick", GUILayout.Width(90f))) ClearPick();
            GUILayout.EndHorizontal();

            GUILayout.EndArea();
        }

        private void WalkParent()
        {
            try
            {
                if (_pickedGo == null || _pickedGo.transform.parent == null) return;
                _candidates.Clear();
                SetPick(_pickedGo.transform.parent.gameObject);
            }
            catch { }
        }

        private static void TrySetActive(GameObject? go, bool active)
        {
            if (go == null) return;
            try { go.SetActive(active); }
            catch { }
        }

        private static void TryClearText(GameObject? go)
        {
            if (go == null) return;
            try
            {
                var legacy = go.GetComponent<Text>();
                if (legacy != null) legacy.text = "";
            }
            catch { }

            foreach (string typeName in new[] { "TMP_Text", "TextMeshProUGUI", "TextMeshPro" })
            {
                Component? c = null;
                try { c = go.GetComponent(typeName); }
                catch { c = null; }
                if (c == null) continue;

                try
                {
                    var prop = c.GetType().GetProperty(
                        "text",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.FlattenHierarchy);
                    prop?.SetValue(c, "");
                }
                catch { }
            }
        }

        // ── Style scaffolding ──────────────────────────────────────────────────────

        private void EnsureStyles()
        {
            if (_labelStyle != null) return;

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                wordWrap = true,
                normal = { textColor = new Color(0.92f, 0.96f, 1f, 1f) }
            };
            _headerStyle = new GUIStyle(_labelStyle)
            {
                fontSize = 16,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.85f, 0.25f, 1f) }
            };
            _smallStyle = new GUIStyle(_labelStyle)
            {
                fontSize = 11,
                wordWrap = true,
                normal = { textColor = new Color(0.7f, 0.7f, 0.7f, 1f) }
            };
        }

        private void DrawBackground(Rect rect, Color color)
        {
            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, Color.white);
                _bgTex.Apply();
            }

            Color old = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, _bgTex);
            GUI.color = old;
        }
    }
}
