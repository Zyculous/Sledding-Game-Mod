using BepInEx.Configuration;

namespace SledCoopMod
{
    /// <summary>
    /// All user-facing feature flags for SledCoopMod.
    /// Stored in BepInEx/config/com.sledcorp.sledcoopmod.cfg
    /// </summary>
    internal static class ModConfig
    {
        // ── Master toggle ──────────────────────────────────────────────────────────
        public static ConfigEntry<bool> Enabled { get; private set; } = null!;

        // ── Local multiplayer ──────────────────────────────────────────────────────
        /// <summary>Enable same-machine networked local co-op (up to MaxLocalPlayers).</summary>
        public static ConfigEntry<bool> LocalCoopEnabled { get; private set; } = null!;

        /// <summary>Maximum number of local players (1-4).</summary>
        public static ConfigEntry<int> MaxLocalPlayers { get; private set; } = null!;

        // ── Window layout ─────────────────────────────────────────────────────────
        /// <summary>
        /// Preferred 2-player window layout.
        /// "Horizontal" = top/bottom halves. "Vertical" = left/right halves.
        /// </summary>
        public static ConfigEntry<string> TwoPlayerSplitOrientation { get; private set; } = null!;

        /// <summary>
        /// For 3 players: "AsymmetricTop" = large top + two small bottom,
        /// "AsymmetricLeft" = large left + two small right.
        /// </summary>
        public static ConfigEntry<string> ThreePlayerLayout { get; private set; } = null!;

        // ── Multi-display ──────────────────────────────────────────────────────────
        /// <summary>
        /// Enable optional multi-display mode. Child processes are moved to separate displays when available.
        /// </summary>
        public static ConfigEntry<bool> MultiDisplayEnabled { get; private set; } = null!;

        // ── Hybrid online (experimental) ───────────────────────────────────────────
        /// <summary>
        /// Allow multiple local players per single online connection.
        /// EXPERIMENTAL – may cause desyncs or ownership violations.
        /// Only applies when LocalCoopEnabled is true and a network session is active.
        /// </summary>
        public static ConfigEntry<bool> OnlineHybridEnabled { get; private set; } = null!;

        // ── Networked multi-instance ──────────────────────────────────────────────
        /// <summary>Use real FishNet client processes for extra local players.</summary>
        public static ConfigEntry<bool> NetworkedInstancesEnabled { get; private set; } = null!;

        /// <summary>Launch child game processes automatically for joined local slots.</summary>
        public static ConfigEntry<bool> LaunchChildProcesses { get; private set; } = null!;

        /// <summary>Tugboat port used by same-machine networked local coop.</summary>
        public static ConfigEntry<int> NetworkedInstancePort { get; private set; } = null!;

        // ── Debug / diagnostics ────────────────────────────────────────────────────
        /// <summary>Log verbose Phase-0 diagnostics on player spawn, input, and camera events.</summary>
        public static ConfigEntry<bool> VerboseLogging { get; private set; } = null!;

        // ── Join/leave ─────────────────────────────────────────────────────────────
        /// <summary>
        /// Allow players to join during an active race by pressing Start/Options on an
        /// unassigned controller.
        /// </summary>
        public static ConfigEntry<bool> MidSessionJoinEnabled { get; private set; } = null!;

        // ── In-game settings UI ────────────────────────────────────────────────────
        /// <summary>
        /// Keyboard key that opens the in-game settings overlay. Any UnityEngine.KeyCode name.
        /// </summary>
        public static ConfigEntry<string> SettingsHotkey { get; private set; } = null!;

        /// <summary>
        /// Modifier required alongside <see cref="SettingsHotkey"/> to open the
        /// settings overlay. One of None / Control / Shift / Alt.
        /// </summary>
        public static ConfigEntry<string> SettingsHotkeyModifier { get; private set; } = null!;

        // ── Debug inspector ────────────────────────────────────────────────────────
        /// <summary>
        /// When true, middle-click on the screen raycasts the UI / scene and
        /// shows the picked GameObject's name, hierarchy path, attached
        /// components, and (for RectTransforms) bounds outline. Use this to
        /// pinpoint residual UI text that the cleanup pass missed.
        /// </summary>
        public static ConfigEntry<bool> DebugInspectEnabled { get; private set; } = null!;

        /// <summary>
        /// When true, the mod re-asserts <c>Cursor.lockState = Locked</c> every
        /// frame during gameplay (when no native menu / mod modal is open) so
        /// the host's mouse stays trapped in the gameplay window.
        /// </summary>
        public static ConfigEntry<bool> ForceCursorLockInGameplay { get; private set; } = null!;

        /// <summary>
        /// When true, draws a thin border around every visible UI element on
        /// screen with its GameObject name shown in the corner. Hovering over
        /// an element expands to a tooltip with full info (path, components,
        /// text). Pairs with the middle-click inspector for chasing residual
        /// UI elements. Heavy: only enable when actively debugging.
        /// </summary>
        public static ConfigEntry<bool> DebugOutlineAllUi { get; private set; } = null!;

        // ── Controller bindings ────────────────────────────────────────────────────
        // Per-slot joystick assignment. Stored as Rewired joystick
        // HardwareIdentifier (or empty for "auto"). ControllerBindings reads
        // and writes these; NetworkedRewiredIsolationManager honors them when
        // assigning gamepads to the canonical Player 0 on the host instance.
        public static ConfigEntry<string> SlotControllerBinding0 { get; private set; } = null!;
        public static ConfigEntry<string> SlotControllerBinding1 { get; private set; } = null!;
        public static ConfigEntry<string> SlotControllerBinding2 { get; private set; } = null!;
        public static ConfigEntry<string> SlotControllerBinding3 { get; private set; } = null!;

        public static void Init(ConfigFile cfg)
        {
            Enabled = cfg.Bind(
                "General", "Enabled", true,
                "Master on/off switch for the entire mod.");

            LocalCoopEnabled = cfg.Bind(
                "LocalCoop", "LocalCoopEnabled", true,
                "Enable same-machine networked local co-op.");

            MaxLocalPlayers = cfg.Bind(
                "LocalCoop", "MaxLocalPlayers", 4,
                new ConfigDescription(
                    "Maximum number of local players (1–4).",
                    new AcceptableValueRange<int>(1, 4)));

            TwoPlayerSplitOrientation = cfg.Bind(
                "SplitScreen", "TwoPlayerSplitOrientation", "Vertical",
                new ConfigDescription(
                    "2-player window layout for networked local instances.",
                    new AcceptableValueList<string>("Vertical", "Horizontal")));

            ThreePlayerLayout = cfg.Bind(
                "SplitScreen", "ThreePlayerLayout", "AsymmetricTop",
                new ConfigDescription(
                    "Window arrangement for 3 players.",
                    new AcceptableValueList<string>("AsymmetricTop", "AsymmetricLeft")));

            MultiDisplayEnabled = cfg.Bind(
                "MultiDisplay", "MultiDisplayEnabled", false,
                "Move child game windows to separate monitors when available.");

            OnlineHybridEnabled = cfg.Bind(
                "Online", "OnlineHybridEnabled", false,
                "EXPERIMENTAL: allow multiple local players on one network connection.");

            NetworkedInstancesEnabled = cfg.Bind(
                "NetworkedInstances", "Enabled", true,
                "Use real FishNet client processes for extra local players.");

            LaunchChildProcesses = cfg.Bind(
                "NetworkedInstances", "LaunchChildProcesses", true,
                "Host automatically launches one child game process for each joined extra local player.");

            NetworkedInstancePort = cfg.Bind(
                "NetworkedInstances", "Port", 7770,
                new ConfigDescription(
                    "Tugboat port used by networked multi-instance local coop.",
                    new AcceptableValueRange<int>(1, 65535)));

            VerboseLogging = cfg.Bind(
                "Debug", "VerboseLogging", false,
                "Log spawn/input/camera events for Phase-0 diagnostics.");

            MidSessionJoinEnabled = cfg.Bind(
                "LocalCoop", "MidSessionJoinEnabled", true,
                "Allow controllers to join/leave during an active match.");

            SettingsHotkey = cfg.Bind(
                "Hotkey", "SettingsHotkey", "P",
                "Key that opens the in-game settings overlay (any UnityEngine.KeyCode name).");

            SettingsHotkeyModifier = cfg.Bind(
                "Hotkey", "SettingsHotkeyModifier", "Control",
                new ConfigDescription(
                    "Modifier required alongside SettingsHotkey to open the overlay.",
                    new AcceptableValueList<string>("None", "Control", "Shift", "Alt")));

            DebugInspectEnabled = cfg.Bind(
                "Debug", "DebugInspectEnabled", false,
                "Middle-click on screen prints the picked GameObject and outlines it.");

            ForceCursorLockInGameplay = cfg.Bind(
                "Debug", "ForceCursorLockInGameplay", true,
                "Re-assert Cursor.lockState=Locked every frame during gameplay.");

            DebugOutlineAllUi = cfg.Bind(
                "Debug", "DebugOutlineAllUi", false,
                "Outline every visible UI element on screen with its GameObject name. Hover for full info.");

            SlotControllerBinding0 = cfg.Bind(
                "ControllerBindings", "Slot0", "Keyboard+Mouse",
                "Rewired joystick HardwareIdentifier, or 'Keyboard+Mouse', bound to local player slot 0. Empty = auto.");
            SlotControllerBinding1 = cfg.Bind(
                "ControllerBindings", "Slot1", "",
                "Rewired joystick HardwareIdentifier bound to local player slot 1. Empty = auto.");
            SlotControllerBinding2 = cfg.Bind(
                "ControllerBindings", "Slot2", "",
                "Rewired joystick HardwareIdentifier bound to local player slot 2. Empty = auto.");
            SlotControllerBinding3 = cfg.Bind(
                "ControllerBindings", "Slot3", "",
                "Rewired joystick HardwareIdentifier bound to local player slot 3. Empty = auto.");
        }
    }
}
