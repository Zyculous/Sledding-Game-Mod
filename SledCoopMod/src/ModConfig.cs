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
        }
    }
}
