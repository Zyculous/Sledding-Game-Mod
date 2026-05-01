using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SledCoopMod
{
    public class LocalCoopUI : MonoBehaviour
    {
        public static LocalCoopUI? Instance { get; private set; }

        private const float PanelW = 360f;
        private const float RowH = 28f;
        private const float BtnW = 78f;

        private readonly string[] _guestUsernames = { "", "", "", "" };
        private readonly float[] _joinFlash = new float[4];

        private GUIStyle? _labelStyle;
        private GUIStyle? _headerStyle;
        private GUIStyle? _buttonStyle;
        private GUIStyle? _textStyle;
        private Texture2D? _bgTex;

        public bool IsSettingsOpen => false;

        private void Awake()
        {
            if (Instance != null && Instance != this) { Destroy(this); return; }
            Instance = this;
        }

        private void OnDestroy()
        {
            if (Instance == this) Instance = null;
            if (_bgTex != null) Destroy(_bgTex);
        }

        public void FlashJoin(int slotIndex)
        {
            if (slotIndex >= 0 && slotIndex < _joinFlash.Length)
                _joinFlash[slotIndex] = 2f;
        }

        private void Update()
        {
            for (int i = 0; i < _joinFlash.Length; i++)
            {
                if (_joinFlash[i] > 0f)
                    _joinFlash[i] -= Time.unscaledDeltaTime;
            }
        }

        private void OnGUI()
        {
            if (!ModConfig.Enabled.Value || !NetworkedInstanceManager.IsNetworkedModeConfigured)
                return;

            if (NetworkedInstanceManager.ShouldHideNetworkedOverlay)
                return;

            if (NetworkedInstanceManager.Instance?.IsChildClient == true)
                return;

            string scene = SceneManager.GetActiveScene().name;
            if (scene == "Boot" || scene == "")
                return;

            EnsureStyles();
            DrawSetupOverlay();
        }

        private void DrawSetupOverlay()
        {
            int maxPlayers = Math.Max(1, Math.Min(4, ModConfig.MaxLocalPlayers.Value));
            float panelH = 96f + maxPlayers * RowH + 56f;
            var panel = new Rect(12f, 12f, PanelW, panelH);
            DrawBackground(panel, new Color(0f, 0f, 0f, 0.72f));

            float x = panel.x + 8f;
            float y = panel.y + 6f;

            GUI.Label(new Rect(x, y, PanelW - 16f, 22f), "Networked Local Instances", _headerStyle);
            y += 26f;
            DrawBackground(new Rect(x, y, PanelW - 16f, 1f), new Color(1f, 1f, 1f, 0.25f));
            y += 6f;

            for (int i = 0; i < maxPlayers; i++)
            {
                LocalPlayerSlot? slot = LocalPlayerManager.Instance?.GetSlot(i);
                bool joined = slot?.IsJoined ?? false;
                bool flash = i < _joinFlash.Length && _joinFlash[i] > 0f;

                string label = i == 0
                    ? "  P1  Host"
                    : flash
                        ? $"  P{i + 1}  Joined"
                        : joined
                            ? $"  P{i + 1}  Ready"
                            : $"  P{i + 1}";

                GUI.Label(new Rect(x, y, 92f, RowH - 2f), label, _labelStyle);

                if (i > 0)
                {
                    string current = joined && !string.IsNullOrWhiteSpace(slot?.ProfileName)
                        ? slot!.ProfileName
                        : _guestUsernames[i];
                    var nameRect = new Rect(x + 96f, y + 2f, PanelW - 16f - 96f - BtnW - 8f, RowH - 6f);
                    string edited = GUI.TextField(nameRect, current ?? "", 32, _textStyle);
                    if (!string.Equals(edited, current, StringComparison.Ordinal))
                    {
                        _guestUsernames[i] = edited;
                        if (joined)
                            LocalPlayerManager.Instance?.SetProfileName(i, edited);
                    }

                    var btnRect = new Rect(x + PanelW - 16f - BtnW, y + 2f, BtnW, RowH - 6f);
                    if (joined)
                    {
                        if (GUI.Button(btnRect, "Remove", _buttonStyle))
                            InputRouter.Instance?.LeaveSlot(i);
                    }
                    else if (GUI.Button(btnRect, "+ Add P" + (i + 1), _buttonStyle))
                    {
                        string profile = NetworkedGuestProfile.Resolve(_guestUsernames[i]);
                        _guestUsernames[i] = profile;
                        InputRouter.Instance?.JoinSlot(i, profile);
                    }
                }

                y += RowH;
            }

            y += 4f;
            if (GUI.Button(new Rect(x, y, PanelW - 16f, 24f), "START CUSTOM SERVER", _buttonStyle))
                NetworkedInstanceManager.TryStartConfiguredNetworkedGame();
            y += 30f;

            string status = NetworkedInstanceManager.Instance?.Status ?? "Ready.";
            GUI.Label(new Rect(x, y, PanelW - 16f, 22f), "  " + status, _labelStyle);
        }

        private void EnsureStyles()
        {
            if (_labelStyle != null) return;

            _labelStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = 13,
                normal = { textColor = new Color(0.85f, 0.95f, 1f, 1f) }
            };
            _headerStyle = new GUIStyle(_labelStyle)
            {
                fontSize = 15,
                fontStyle = FontStyle.Bold,
                normal = { textColor = new Color(1f, 0.85f, 0.25f, 1f) }
            };
            _buttonStyle = new GUIStyle(GUI.skin.button) { fontSize = 12 };
            _textStyle = new GUIStyle(GUI.skin.textField) { fontSize = 12 };
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
