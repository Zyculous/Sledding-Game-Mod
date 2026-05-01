using System;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SledCoopMod
{
    /// <summary>
    /// Drops noisy third-party log lines (Dissonance + Epic Online Services SDK)
    /// from Player.log by Harmony-patching <see cref="UnityEngine.Logger"/>.
    ///
    /// PRIOR APPROACH (DON'T REINTRODUCE):
    ///   We tried implementing ILogHandler directly and replacing
    ///   <c>Debug.unityLogger.logHandler</c>.  Under Il2CppInterop /
    ///   BepInEx 6 IL2CPP, <c>UnityEngine.ILogHandler</c> appears in the
    ///   runtime metadata as a CLASS, not an interface, even though the
    ///   Unity Editor reference assembly we compile against declares it
    ///   as an interface.  C#'s "class : InterfaceName" syntax then trips
    ///   <c>System.TypeLoadException: ... attempts to implement a class
    ///   as an interface</c> the first time the type is touched, which
    ///   aborts <see cref="ModBootstrap.Awake"/> before
    ///   <see cref="GameObject.AddComponent{T}"/> for the runtime
    ///   managers (LocalPlayerManager / InputRouter / SceneWatcher / etc.)
    ///   ever runs — silently disabling the entire mod.
    ///
    ///   The Harmony route below side-steps the issue entirely: we patch
    ///   <see cref="UnityEngine.Logger.Log"/> overloads, which are regular
    ///   instance methods on a regular class.
    /// </summary>
    internal static class LogFilter
    {
        // Each candidate is matched as a substring against the rendered
        // message text.  Targeted entries only — broad filters can swallow
        // legitimate game logs that happen to share a token.
        private static readonly string[] _droppedSubstrings =
        {
            // Dissonance voice chat (mic restart, connect/disconnect, tracking).
            "[Dissonance:",
            // PlayEveryWare EOS SDK bridge.  Format e.g.
            //   "04/30/2026 14:46:55 LogEOS(Verbose): ..."
            //   "... LogEOSHTTP ...", "LogEOSAuth", "LogEOSConnect", "LogEOSLobby", "LogEOSP2P", etc.
            "LogEOS",
            // Native EOS SDK boot prefix (different format from the C# bridge).
            "NativePlugin (",
            // Direct EOS SDK function load entries.
            "EOS_Initialize",
            "EOS_Platform_Create",
            "EOSAuth",
            // Boot-time EOS/PlayEveryWare native initialization noise.
            "recognized by initialization routine",
            "PlayEveryWare.EpicOnlineServices",
        };

        // Public so the Harmony Prefixes below can call it without exposing the array.
        internal static bool ShouldDrop(string? text)
        {
            if (string.IsNullOrEmpty(text)) return false;
            foreach (var token in _droppedSubstrings)
                if (text!.IndexOf(token, StringComparison.Ordinal) >= 0) return true;
            return false;
        }

        // Convert a Logger.Log "message" object to a string the same way
        // Unity does internally (object.ToString()).
        internal static string SafeToString(object? message)
        {
            if (message == null) return string.Empty;
            try { return message.ToString() ?? string.Empty; }
            catch { return string.Empty; }
        }

        // Render a format-string with args the way String.Format would, but
        // never throw: a malformed format gets returned as-is so we can
        // still pattern-match on the template prefix.
        internal static string SafeFormat(string format, object[]? args)
        {
            if (string.IsNullOrEmpty(format)) return format ?? string.Empty;
            if (args == null || args.Length == 0) return format;
            try { return string.Format(format, args); }
            catch { return format; }
        }
    }

    // ── UnityEngine.Logger.Log(LogType, object) ──────────────────────────────────
    [HarmonyPatch]
    internal static class Patch_UnityLogger_Log_Object
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                return AccessTools.Method(typeof(UnityEngine.Logger), "Log",
                    new[] { typeof(UnityEngine.LogType), typeof(object) });
            }
            catch { return null; }
        }

        [HarmonyPrefix]
        static bool Prefix(object message)
        {
            return !LogFilter.ShouldDrop(LogFilter.SafeToString(message));
        }
    }

    // ── UnityEngine.Logger.Log(LogType, object, UnityEngine.Object) ──────────────
    [HarmonyPatch]
    internal static class Patch_UnityLogger_Log_Object_Context
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                return AccessTools.Method(typeof(UnityEngine.Logger), "Log",
                    new[] { typeof(UnityEngine.LogType), typeof(object), typeof(UnityEngine.Object) });
            }
            catch { return null; }
        }

        [HarmonyPrefix]
        static bool Prefix(object message)
        {
            return !LogFilter.ShouldDrop(LogFilter.SafeToString(message));
        }
    }

    // ── UnityEngine.Logger.Log(LogType, string, object) ──────────────────────────
    // Used by Dissonance.Log.Info(string, T) and similar tagged overloads.
    [HarmonyPatch]
    internal static class Patch_UnityLogger_Log_Tagged
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                return AccessTools.Method(typeof(UnityEngine.Logger), "Log",
                    new[] { typeof(UnityEngine.LogType), typeof(string), typeof(object) });
            }
            catch { return null; }
        }

        [HarmonyPrefix]
        static bool Prefix(string tag, object message)
        {
            // Either tag or rendered message can carry the noise prefix.
            if (LogFilter.ShouldDrop(tag)) return false;
            return !LogFilter.ShouldDrop(LogFilter.SafeToString(message));
        }
    }

    // ── UnityEngine.Logger.LogFormat(LogType, string, object[]) ──────────────────
    [HarmonyPatch]
    internal static class Patch_UnityLogger_LogFormat
    {
        static MethodBase? TargetMethod()
        {
            try
            {
                return AccessTools.Method(typeof(UnityEngine.Logger), "LogFormat",
                    new[] { typeof(UnityEngine.LogType), typeof(string), typeof(object[]) });
            }
            catch { return null; }
        }

        [HarmonyPrefix]
        static bool Prefix(string format, object[] args)
        {
            if (LogFilter.ShouldDrop(format)) return false;
            return !LogFilter.ShouldDrop(LogFilter.SafeFormat(format, args));
        }
    }
}
