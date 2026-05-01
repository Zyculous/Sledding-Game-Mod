using System;
using System.Reflection;
using UnityEngine;

namespace SledCoopMod
{
    /// <summary>
    /// Reflection helpers for accessing IL2CPP-generated interop types at runtime
    /// without requiring their DLLs at compile time.
    ///
    /// BACKGROUND:
    ///   Game types (PlayerControl, etc.) live in Assembly-CSharp which only exists
    ///   after BepInEx generates interop stubs.  Harmony patches receive these as
    ///   'object', so we retrieve properties via reflection until real stubs land.
    /// </summary>
    internal static class ReflectionHelper
    {
        // Cached PropertyInfo lookups keyed by (Type, propertyName)
        private static readonly System.Collections.Generic.Dictionary<(Type, string), PropertyInfo?>
            _propCache = new();

        // ── Public API ─────────────────────────────────────────────────────────────

        /// <summary>
        /// Try to get the UnityEngine.GameObject from any MonoBehaviour-derived
        /// IL2CPP interop object without a compile-time reference to its type.
        /// </summary>
        public static GameObject? GetGameObject(object? instance)
        {
            if (instance == null) return null;
            try
            {
                var prop = GetCachedProperty(instance.GetType(), "gameObject");
                return prop?.GetValue(instance) as GameObject;
            }
            catch { return null; }
        }

        /// <summary>
        /// Try to read a named property value from an unknown runtime object.
        /// Returns null on any failure.
        /// </summary>
        public static object? GetProperty(object? instance, string name)
        {
            if (instance == null) return null;
            try
            {
                var prop = GetCachedProperty(instance.GetType(), name);
                return prop?.GetValue(instance);
            }
            catch { return null; }
        }

        /// <summary>
        /// Try to call a parameterless method on an unknown runtime object.
        /// Returns null on any failure.
        /// </summary>
        public static object? CallMethod(object? instance, string name)
        {
            if (instance == null) return null;
            try
            {
                var method = instance.GetType().GetMethod(
                    name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                return method?.Invoke(instance, null);
            }
            catch { return null; }
        }

        /// <summary>
        /// Try to call a parameterless static method on a type found by name.
        /// </summary>
        public static object? CallStaticMethod(string typeName, string methodName)
        {
            try
            {
                var t = SledCoopMod.Patches.PatchHelpers.SafeTypeByName(typeName);
                if (t == null) return null;
                var m = t.GetMethod(
                    methodName, BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                return m?.Invoke(null, null);
            }
            catch { return null; }
        }

        // ── Private helpers ────────────────────────────────────────────────────────

        private static PropertyInfo? GetCachedProperty(Type type, string name)
        {
            var key = (type, name);
            if (!_propCache.TryGetValue(key, out var pi))
            {
                pi = type.GetProperty(
                    name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                _propCache[key] = pi;
            }
            return pi;
        }
    }
}
