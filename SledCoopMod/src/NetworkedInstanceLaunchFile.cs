using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;

namespace SledCoopMod
{
    internal static class NetworkedInstanceLaunchFile
    {
        private const int LeaseSeconds = 180;

        public static string Create(int slot, ushort port, int playerCount, string profile, AssignedInputDevice device, string profiles)
        {
            Directory.CreateDirectory(GetLaunchDir());
            CleanupStale();

            string token = Guid.NewGuid().ToString("N");
            string path = Path.Combine(GetLaunchDir(), $"pending-{token}.txt");
            string[] lines =
            {
                "role=client",
                "host=127.0.0.1",
                $"port={port}",
                $"slot={slot}",
                $"player-count={Math.Max(1, Math.Min(4, playerCount))}",
                $"profile={NetworkedGuestProfile.Resolve(profile)}",
                $"device={device}",
                $"profiles={profiles}",
                $"parentPid={GetCurrentPid()}",
                $"createdUtc={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}",
                $"token={token}",
            };
            File.WriteAllLines(path, lines);
            return token;
        }

        public static bool TryClaim(out NetworkedInstanceArgs? args)
        {
            args = null;
            CleanupStale();

            string dir = GetLaunchDir();
            if (!Directory.Exists(dir)) return false;

            foreach (string file in Directory.GetFiles(dir, "pending-*.txt"))
            {
                try
                {
                    var parsed = ParseFile(file);
                    if (parsed == null) continue;
                    if (!IsFresh(parsed.CreatedUtc)) continue;
                    if (parsed.ParentPid > 0 && !IsProcessRunning(parsed.ParentPid)) continue;

                    string claimedPath = Path.Combine(dir, $"claimed-{parsed.Token}-{GetCurrentPid()}.txt");
                    File.Move(file, claimedPath);
                    args = parsed.Args;
                    return true;
                }
                catch { }
            }

            return false;
        }

        private static ParsedLaunch? ParseFile(string file)
        {
            var args = NetworkedInstanceArgs.FromLaunchDefaults();
            long createdUtc = 0;
            int parentPid = -1;
            string token = Path.GetFileNameWithoutExtension(file).Replace("pending-", "");

            foreach (string line in File.ReadAllLines(file))
            {
                int eq = line.IndexOf('=');
                if (eq <= 0) continue;
                string key = line.Substring(0, eq).Trim();
                string value = line.Substring(eq + 1).Trim();
                switch (key)
                {
                    case "createdUtc":
                        long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out createdUtc);
                        break;
                    case "parentPid":
                        int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out parentPid);
                        break;
                    case "token":
                        if (!string.IsNullOrWhiteSpace(value)) token = value;
                        break;
                    default:
                        args.Apply(key, value);
                        break;
                }
            }

            return args.IsClient ? new ParsedLaunch(args, createdUtc, parentPid, token) : null;
        }

        private static void CleanupStale()
        {
            string dir = GetLaunchDir();
            if (!Directory.Exists(dir)) return;

            foreach (string file in Directory.GetFiles(dir, "*.txt"))
            {
                try
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(file);
                    if (age.TotalSeconds > LeaseSeconds)
                        File.Delete(file);
                }
                catch { }
            }
        }

        private static bool IsFresh(long createdUtc)
        {
            if (createdUtc <= 0) return false;
            long age = DateTimeOffset.UtcNow.ToUnixTimeSeconds() - createdUtc;
            return age >= 0 && age <= LeaseSeconds;
        }

        private static bool IsProcessRunning(int pid)
        {
            try
            {
                Process.GetProcessById(pid);
                return true;
            }
            catch { return false; }
        }

        private static int GetCurrentPid()
        {
            try { return Process.GetCurrentProcess().Id; }
            catch { return -1; }
        }

        private static string GetLaunchDir()
        {
            string root;
            try { root = Path.GetTempPath(); }
            catch { root = Environment.CurrentDirectory; }
            return Path.Combine(root, "SledCoopMod", "networked-instance-launch");
        }

        private sealed class ParsedLaunch
        {
            public ParsedLaunch(NetworkedInstanceArgs args, long createdUtc, int parentPid, string token)
            {
                Args = args;
                CreatedUtc = createdUtc;
                ParentPid = parentPid;
                Token = token;
            }

            public NetworkedInstanceArgs Args { get; }
            public long CreatedUtc { get; }
            public int ParentPid { get; }
            public string Token { get; }
        }
    }
}
