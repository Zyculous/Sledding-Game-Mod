using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using FishNet.Managing;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

namespace SledCoopMod
{
    internal static class NetworkManagerFinder
    {
        private static readonly HashSet<string> _loggedFailures = new();

        public static NetworkManager? Find()
        {
            return TryFindObjectOfType()
                ?? TryFindFirstObjectByType()
                ?? TryFindNamedObject()
                ?? TryFindInActiveSceneRoots();
        }

        private static NetworkManager? TryFindObjectOfType()
        {
            try
            {
                var nm = UObject.FindObjectOfType<NetworkManager>();
                if (nm != null) return nm;
            }
            catch (Exception e) { LogOnce("FindObjectOfType<T>", e); }

            try
            {
                return UObject.FindObjectOfType<NetworkManager>(true);
            }
            catch (Exception e) { LogOnce("FindObjectOfType<T>(bool)", e); }

            return null;
        }

        private static NetworkManager? TryFindFirstObjectByType()
        {
            try
            {
                var nm = UObject.FindFirstObjectByType<NetworkManager>();
                if (nm != null) return nm;
            }
            catch (Exception e) { LogOnce("FindFirstObjectByType<T>", e); }

            try
            {
                var nm = UObject.FindFirstObjectByType<NetworkManager>(FindObjectsInactive.Include);
                if (nm != null) return nm;
            }
            catch (Exception e) { LogOnce("FindFirstObjectByType<T>(inactive)", e); }

            try
            {
                return UObject.FindAnyObjectByType<NetworkManager>(FindObjectsInactive.Include);
            }
            catch (Exception e) { LogOnce("FindAnyObjectByType<T>(inactive)", e); }

            return null;
        }

        private static NetworkManager? TryFindNamedObject()
        {
            string[] names =
            {
                "NetworkManager",
                "Network Manager",
                "FishNet NetworkManager",
                "FishNet Network Manager"
            };

            foreach (string name in names)
            {
                try
                {
                    var go = GameObject.Find(name);
                    if (go == null) continue;
                    var nm = go.GetComponent<NetworkManager>();
                    if (nm != null) return nm;
                }
                catch (Exception e) { LogOnce($"GameObject.Find({name})", e); }
            }

            return null;
        }

        private static NetworkManager? TryFindInActiveSceneRoots()
        {
            try
            {
                Scene scene = SceneManager.GetActiveScene();
                if (!scene.IsValid()) return null;

                GameObject[]? roots = GetSceneRootGameObjects(scene);
                if (roots == null) return null;

                foreach (var root in roots)
                {
                    var nm = FindInHierarchy(root);
                    if (nm != null) return nm;
                }
            }
            catch (Exception e) { LogOnce("scene root scan", e); }

            return null;
        }

        private static NetworkManager? FindInHierarchy(GameObject? go)
        {
            if (go == null) return null;

            try
            {
                var nm = go.GetComponent<NetworkManager>();
                if (nm != null) return nm;
            }
            catch (Exception e) { LogOnce("GetComponent<NetworkManager>", e); }

            Transform? transform = null;
            try { transform = go.transform; }
            catch { }
            if (transform == null) return null;

            int childCount;
            try { childCount = transform.childCount; }
            catch { return null; }

            for (int i = 0; i < childCount; i++)
            {
                try
                {
                    var child = transform.GetChild(i);
                    var found = FindInHierarchy(child?.gameObject);
                    if (found != null) return found;
                }
                catch { }
            }

            return null;
        }

        internal static GameObject[]? GetSceneRootGameObjectsExposed(Scene scene) => GetSceneRootGameObjects(scene);

        private static GameObject[]? GetSceneRootGameObjects(Scene scene)
        {
            var methods = scene.GetType().GetMethods(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

            foreach (var method in methods)
            {
                if (method.Name != "GetRootGameObjects") continue;
                if (method.GetParameters().Length != 0) continue;
                if (!typeof(GameObject[]).IsAssignableFrom(method.ReturnType)) continue;

                try { return method.Invoke(scene, null) as GameObject[]; }
                catch (Exception e) { LogOnce("Scene.GetRootGameObjects()", e); }
            }

            foreach (var method in methods)
            {
                if (method.Name != "GetRootGameObjects") continue;
                var parameters = method.GetParameters();
                if (parameters.Length != 1) continue;

                object? rootsContainer;
                try { rootsContainer = Activator.CreateInstance(parameters[0].ParameterType); }
                catch (Exception e)
                {
                    LogOnce("Scene.GetRootGameObjects(list).new", e);
                    continue;
                }

                if (rootsContainer == null) continue;

                try { method.Invoke(scene, new[] { rootsContainer }); }
                catch (Exception e)
                {
                    LogOnce("Scene.GetRootGameObjects(list)", e);
                    continue;
                }

                var toArray = rootsContainer.GetType().GetMethod("ToArray", Type.EmptyTypes);
                if (toArray != null)
                {
                    try
                    {
                        var array = toArray.Invoke(rootsContainer, null) as GameObject[];
                        if (array != null) return array;
                    }
                    catch (Exception e) { LogOnce("Scene roots ToArray", e); }
                }

                if (rootsContainer is IEnumerable enumerable)
                {
                    var result = new List<GameObject>();
                    foreach (var item in enumerable)
                        if (item is GameObject gameObject)
                            result.Add(gameObject);
                    return result.ToArray();
                }
            }

            return null;
        }

        private static void LogOnce(string api, Exception e)
        {
            if (!ModConfig.VerboseLogging.Value) return;
            if (!_loggedFailures.Add(api)) return;
            Plugin.Log.LogInfo($"[NetworkManagerFinder] {api} unavailable: {e.GetType().Name}: {e.Message}");
        }
    }
}
