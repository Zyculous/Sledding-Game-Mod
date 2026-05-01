using System;
using System.Collections.Generic;

namespace SledCoopMod
{
    internal enum NetworkedInstanceRole
    {
        Host,
        Client
    }

    internal sealed class NetworkedInstanceArgs
    {
        public NetworkedInstanceRole Role { get; private set; } = NetworkedInstanceRole.Host;
        public string HostAddress { get; private set; } = "127.0.0.1";
        public ushort Port { get; private set; } = 7770;
        public int Slot { get; private set; }
        public int PlayerCount { get; private set; } = 1;
        public string Profile { get; private set; } = "Host";
        public AssignedInputDevice Device { get; private set; } = AssignedInputDevice.None;
        private readonly Dictionary<int, string> _profilesBySlot = new Dictionary<int, string>();

        public bool IsClient => Role == NetworkedInstanceRole.Client;

        public static NetworkedInstanceArgs FromLaunchDefaults()
        {
            return new NetworkedInstanceArgs();
        }

        public static NetworkedInstanceArgs Parse()
        {
            var result = new NetworkedInstanceArgs();
            result.ApplyEnvironment();

            string[] args;
            try { args = Environment.GetCommandLineArgs(); }
            catch { args = Array.Empty<string>(); }

            foreach (string raw in args)
            {
                if (string.IsNullOrWhiteSpace(raw) || !raw.StartsWith("--sledcoop-", StringComparison.OrdinalIgnoreCase))
                    continue;

                int eq = raw.IndexOf('=');
                string key = eq >= 0 ? raw.Substring(0, eq) : raw;
                string value = eq >= 0 ? raw.Substring(eq + 1) : "";

                switch (key.ToLowerInvariant())
                {
                    case "--sledcoop-role":
                        result.Apply("role", value);
                        break;
                    case "--sledcoop-host":
                        result.Apply("host", value);
                        break;
                    case "--sledcoop-port":
                        result.Apply("port", value);
                        break;
                    case "--sledcoop-slot":
                        result.Apply("slot", value);
                        break;
                    case "--sledcoop-player-count":
                        result.Apply("player-count", value);
                        break;
                    case "--sledcoop-profile":
                        result.Apply("profile", value);
                        break;
                    case "--sledcoop-device":
                        result.Apply("device", value);
                        break;
                    case "--sledcoop-profiles":
                        result.Apply("profiles", value);
                        break;
                }
            }

            if (result.IsClient && result.Profile == "Host")
                result.Profile = NetworkedGuestProfile.Resolve($"guest{Math.Max(1, result.Slot):00}");

            if (!string.IsNullOrWhiteSpace(result.Profile))
                result._profilesBySlot[result.Slot] = result.Profile;

            if (!result.IsClient && NetworkedInstanceLaunchFile.TryClaim(out var claimed) && claimed != null)
                result = claimed;

            return result;
        }

        private void ApplyEnvironment()
        {
            Apply("role", GetEnv("SLEDCOOP_ROLE"));
            Apply("host", GetEnv("SLEDCOOP_HOST"));
            Apply("port", GetEnv("SLEDCOOP_PORT"));
            Apply("slot", GetEnv("SLEDCOOP_SLOT"));
            Apply("player-count", GetEnv("SLEDCOOP_PLAYER_COUNT"));
            Apply("profile", GetEnv("SLEDCOOP_PROFILE"));
            Apply("device", GetEnv("SLEDCOOP_DEVICE"));
            Apply("profiles", GetEnv("SLEDCOOP_PROFILES"));
        }

        public void Apply(string key, string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return;
            key = key.Trim().TrimStart('-').Replace("sledcoop-", "", StringComparison.OrdinalIgnoreCase).ToLowerInvariant();

            switch (key)
            {
                case "role":
                    Role = value.Equals("client", StringComparison.OrdinalIgnoreCase)
                        ? NetworkedInstanceRole.Client
                        : NetworkedInstanceRole.Host;
                    break;
                case "host":
                    HostAddress = value;
                    break;
                case "port":
                    if (ushort.TryParse(value, out ushort port) && port > 0)
                        Port = port;
                    break;
                case "slot":
                    if (int.TryParse(value, out int slot))
                        Slot = Math.Max(0, Math.Min(3, slot));
                    break;
                case "player-count":
                case "playercount":
                    if (int.TryParse(value, out int playerCount))
                        PlayerCount = Math.Max(1, Math.Min(4, playerCount));
                    break;
                case "profile":
                    Profile = NetworkedGuestProfile.Resolve(value);
                    break;
                case "device":
                    Device = ParseDevice(value);
                    break;
                case "profiles":
                    ApplyProfiles(value);
                    break;
            }
        }

        public bool TryGetProfileForSlot(int slot, out string profile)
        {
            profile = "";
            if (!_profilesBySlot.TryGetValue(slot, out string? value) || string.IsNullOrWhiteSpace(value))
                return false;

            profile = value;
            return true;
        }

        public static string EncodeProfiles(System.Collections.Generic.IEnumerable<System.Collections.Generic.KeyValuePair<int, string>> profiles)
        {
            var parts = new System.Collections.Generic.List<string>();
            foreach (var pair in profiles)
            {
                int slot = Math.Max(0, Math.Min(3, pair.Key));
                string profile = NetworkedGuestProfile.Sanitize(pair.Value);
                if (string.IsNullOrWhiteSpace(profile))
                    continue;

                parts.Add($"{slot}={Uri.EscapeDataString(profile)}");
            }

            return string.Join("|", parts);
        }

        private void ApplyProfiles(string value)
        {
            foreach (string part in value.Split('|'))
            {
                if (string.IsNullOrWhiteSpace(part))
                    continue;

                int eq = part.IndexOf('=');
                if (eq <= 0)
                    continue;

                if (!int.TryParse(part.Substring(0, eq), out int slot))
                    continue;

                string raw = part.Substring(eq + 1);
                try { raw = Uri.UnescapeDataString(raw); }
                catch { }

                string profile = NetworkedGuestProfile.Sanitize(raw);
                if (!string.IsNullOrWhiteSpace(profile))
                    _profilesBySlot[Math.Max(0, Math.Min(3, slot))] = profile;
            }
        }

        private static string? GetEnv(string key)
        {
            try { return Environment.GetEnvironmentVariable(key); }
            catch { return null; }
        }

        private static AssignedInputDevice ParseDevice(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return AssignedInputDevice.None;

            if (Enum.TryParse(value, ignoreCase: true, out AssignedInputDevice parsed))
                return parsed;

            if (value.StartsWith("Gamepad", StringComparison.OrdinalIgnoreCase)
                && int.TryParse(value.Substring("Gamepad".Length), out int index)
                && index >= 0 && index <= 3)
            {
                return (AssignedInputDevice)((int)AssignedInputDevice.Gamepad0 + index);
            }

            return AssignedInputDevice.None;
        }
    }
}
