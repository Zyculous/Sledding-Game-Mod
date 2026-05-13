using System;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SledCoopMod
{
    public class NetworkedFocusManager : MonoBehaviour
    {
        private bool _rewiredConfigured;
        private bool _inputSystemConfigured;
        private bool _runInBackgroundLogged;
        private bool _cursorClipActive;

        public static NetworkedFocusManager? Instance { get; private set; }

        public static void ForceReleaseCursorForMenu()
        {
            try
            {
                Instance?.ReleaseCursorClip();
                Cursor.lockState = CursorLockMode.None;
                Cursor.visible = true;
            }
            catch { }
        }
        private IntPtr _windowHandle;
        private int _nextRewiredAttemptFrame;
        private int _nextWindowRefreshFrame;
        private int _forceMouseLockUntilFrame;

        private void Awake()
        {
            Instance = this;
            DontDestroyOnLoad(gameObject);
            ApplyRunInBackground();
            TryConfigureUnityInputSystemBackgroundInput();
            TryConfigureRewiredBackgroundInput();
        }

        private void Update()
        {
            if (!NetworkedInstanceManager.IsNetworkedModeConfigured)
                return;

            ApplyRunInBackground();
            if (!_inputSystemConfigured)
                TryConfigureUnityInputSystemBackgroundInput();

            if (!_rewiredConfigured && Time.frameCount >= _nextRewiredAttemptFrame)
            {
                _nextRewiredAttemptFrame = Time.frameCount + 120;
                TryConfigureRewiredBackgroundInput();
            }

            if (Time.frameCount >= _nextWindowRefreshFrame)
            {
                _nextWindowRefreshFrame = Time.frameCount + 60;
                RefreshWindowHandle();
            }

            UpdateMouseCapture();
        }

        private void OnApplicationFocus(bool hasFocus)
        {
            if (hasFocus)
            {
                _forceMouseLockUntilFrame = Time.frameCount + 180;
            }
            else
            {
                ReleaseCursorClip();
            }
        }

        private void OnDestroy()
        {
            ReleaseCursorClip();
        }

        private static bool ShouldManageGameplayCursor =>
            NetworkedInstanceManager.IsNetworkedModeConfigured
            && NetworkedInstanceManager.ShouldHideNetworkedOverlay
            && SceneWatcher.IsInGameplayScene
            && Time.timeScale > 0.01f
            && !NetworkedUiState.IsNativeMenuOpen()
            && !ModSettingsUi.IsAnyOpen;

        private void ApplyRunInBackground()
        {
            try
            {
                if (Application.runInBackground)
                    return;

                Application.runInBackground = true;
                if (!_runInBackgroundLogged)
                {
                    _runInBackgroundLogged = true;
                    Plugin.Log.LogInfo("[NetworkedFocusManager] Application.runInBackground enabled for networked local instances.");
                }
            }
            catch (Exception e)
            {
                if (!_runInBackgroundLogged)
                {
                    _runInBackgroundLogged = true;
                    Plugin.Log.LogWarning($"[NetworkedFocusManager] Could not enable runInBackground: {e.Message}");
                }
            }
        }

        private void TryConfigureRewiredBackgroundInput()
        {
            try
            {
                Type? reInput = FindType("Rewired.ReInput");
                if (reInput == null)
                    return;

                object? configuration = reInput.GetProperty("configuration", BindingFlags.Public | BindingFlags.Static)?.GetValue(null);
                if (configuration == null)
                    return;

                PropertyInfo? ignoreFocus = configuration.GetType().GetProperty(
                    "ignoreInputWhenAppNotInFocus",
                    BindingFlags.Public | BindingFlags.Instance);
                if (ignoreFocus == null || !ignoreFocus.CanWrite)
                    return;

                ignoreFocus.SetValue(configuration, false);
                _rewiredConfigured = true;
                Plugin.Log.LogInfo("[NetworkedFocusManager] Rewired background input enabled (ignoreInputWhenAppNotInFocus=false).");
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[NetworkedFocusManager] Rewired background input setup deferred: {e.Message}");
            }
        }

        private void TryConfigureUnityInputSystemBackgroundInput()
        {
            try
            {
                var settings = UnityEngine.InputSystem.InputSystem.settings;
                if (settings == null)
                    return;

                settings.backgroundBehavior = UnityEngine.InputSystem.InputSettings.BackgroundBehavior.IgnoreFocus;
                _inputSystemConfigured = true;
                Plugin.Log.LogInfo("[NetworkedFocusManager] Unity Input System background behavior set to IgnoreFocus.");
            }
            catch (Exception e)
            {
                Plugin.Log.LogDebug($"[NetworkedFocusManager] Unity Input System background setup deferred: {e.Message}");
            }
        }

        private static Type? FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, throwOnError: false);
                    if (type != null)
                        return type;
                }
                catch { }
            }
            return null;
        }

        private void UpdateMouseCapture()
        {
            if (_windowHandle == IntPtr.Zero)
                RefreshWindowHandle();

            if (_windowHandle == IntPtr.Zero)
                return;

            bool foreground = IsThisWindowForeground();
            if (!foreground)
                return;

            if (!ShouldManageGameplayCursor)
            {
                if (NetworkedUiState.IsNativeMenuOpen())
                {
                    try
                    {
                        Cursor.lockState = CursorLockMode.None;
                        Cursor.visible = true;
                    }
                    catch { }
                }

                ReleaseCursorClip();
                return;
            }

            bool nativeWantsLock = Cursor.lockState == CursorLockMode.Locked || !Cursor.visible;
            bool forceLock = Time.frameCount <= _forceMouseLockUntilFrame;
            bool userForce = ModConfig.ForceCursorLockInGameplay.Value;
            if (!nativeWantsLock && !forceLock && !userForce)
            {
                ReleaseCursorClip();
                return;
            }

            try
            {
                Cursor.lockState = CursorLockMode.Locked;
                Cursor.visible = false;
            }
            catch { }

            ClipCursorToClientArea();
        }

        private void RefreshWindowHandle()
        {
            try
            {
                using var process = Process.GetCurrentProcess();
                process.Refresh();
                _windowHandle = process.MainWindowHandle;
            }
            catch
            {
                _windowHandle = IntPtr.Zero;
            }
        }

        private bool IsThisWindowForeground()
        {
            try
            {
                return _windowHandle != IntPtr.Zero && GetForegroundWindow() == _windowHandle;
            }
            catch
            {
                return Application.isFocused;
            }
        }

        private void ClipCursorToClientArea()
        {
            try
            {
                if (!GetClientRect(_windowHandle, out RECT client))
                    return;

                POINT topLeft = new POINT { X = client.Left, Y = client.Top };
                POINT bottomRight = new POINT { X = client.Right, Y = client.Bottom };
                if (!ClientToScreen(_windowHandle, ref topLeft) || !ClientToScreen(_windowHandle, ref bottomRight))
                    return;

                var clip = new RECT
                {
                    Left = topLeft.X,
                    Top = topLeft.Y,
                    Right = Math.Max(topLeft.X + 1, bottomRight.X),
                    Bottom = Math.Max(topLeft.Y + 1, bottomRight.Y)
                };

                if (ClipCursor(ref clip))
                    _cursorClipActive = true;
            }
            catch { }
        }

        private void ReleaseCursorClip()
        {
            if (!_cursorClipActive)
                return;

            try { ClipCursor(IntPtr.Zero); }
            catch { }
            _cursorClipActive = false;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern bool GetClientRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClientToScreen(IntPtr hWnd, ref POINT lpPoint);

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(ref RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool ClipCursor(IntPtr lpRect);
    }
}
