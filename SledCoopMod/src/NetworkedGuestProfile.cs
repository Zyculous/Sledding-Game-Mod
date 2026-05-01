using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace SledCoopMod
{
    internal static class NetworkedGuestProfile
    {
        private static readonly System.Random Rng = new System.Random();

        public static string GenerateGuestName()
        {
            lock (Rng)
                return $"guest{Rng.Next(0, 100):00}";
        }

        public static string Resolve(string? raw)
        {
            string sanitized = Sanitize(raw);
            return string.IsNullOrWhiteSpace(sanitized) ? GenerateGuestName() : sanitized;
        }

        public static string Sanitize(string? raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return "";

            var sb = new StringBuilder(32);
            foreach (char c in raw.Trim())
            {
                if (char.IsLetterOrDigit(c))
                    sb.Append(c);
                else if ((c == ' ' || c == '_' || c == '-') && sb.Length > 0)
                    sb.Append('_');

                if (sb.Length >= 32)
                    break;
            }

            return sb.ToString().Trim('_');
        }

        public static bool TryRewriteGameSaveFileName(string fileName, out string rewritten)
        {
            rewritten = fileName;

            var manager = NetworkedInstanceManager.Instance;
            if (manager?.IsChildClient != true)
                return false;

            string profile = Resolve(manager.ProfileName);
            string baseName = Path.GetFileName(fileName);
            if (string.IsNullOrWhiteSpace(baseName))
                baseName = "save.json";

            EnsureGuestDirectory(profile);
            rewritten = $"SledCoopMod/Guests/{profile}/{baseName}";
            return !string.Equals(fileName, rewritten, StringComparison.Ordinal);
        }

        public static bool GuestSaveFileExists(string rewrittenFileName)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(rewrittenFileName))
                    return false;

                string path = Path.Combine(
                    Application.persistentDataPath,
                    rewrittenFileName.Replace('/', Path.DirectorySeparatorChar));
                return File.Exists(path);
            }
            catch { return false; }
        }

        private static void EnsureGuestDirectory(string profile)
        {
            try
            {
                string root = Application.persistentDataPath;
                if (string.IsNullOrWhiteSpace(root)) return;
                Directory.CreateDirectory(Path.Combine(root, "SledCoopMod", "Guests", profile));
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[NetworkedGuestProfile] Could not pre-create guest save directory: {e.Message}");
            }
        }
    }
}
