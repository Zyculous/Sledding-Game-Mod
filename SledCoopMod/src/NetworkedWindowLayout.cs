using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SledCoopMod
{
    internal static class NetworkedWindowLayout
    {
        private const uint SWP_NOZORDER = 0x0004;
        private const uint SWP_NOACTIVATE = 0x0010;
        private const int SW_RESTORE = 9;
        private static string _lastAppliedLogKey = "";
        private static int _lastAppliedLogFrame;

        public static bool TryApplyToCurrentProcess(int slotIndex, int playerCount, bool allowHostMove)
        {
            if (!TryGetBounds(slotIndex, playerCount, allowHostMove, out var bounds))
                return true;

            try
            {
                Screen.fullScreen = false;
                Screen.fullScreenMode = FullScreenMode.Windowed;
                Screen.SetResolution(bounds.Width, bounds.Height, FullScreenMode.Windowed);
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedWindowLayout] Screen.SetResolution failed: {e.Message}");
            }

            IntPtr hwnd = IntPtr.Zero;
            try
            {
                var process = Process.GetCurrentProcess();
                process.Refresh();
                hwnd = process.MainWindowHandle;
            }
            catch { }

            if (hwnd == IntPtr.Zero)
                return false;

            try
            {
                try { ShowWindow(hwnd, SW_RESTORE); }
                catch { }

                if (!SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE))
                    return false;

                if (!VerifyWindowBounds(hwnd, bounds))
                    return false;

                LogApplied(slotIndex, playerCount, bounds, hwnd);
                return true;
            }
            catch (Exception e)
            {
                Plugin.Log.LogWarning($"[NetworkedWindowLayout] SetWindowPos failed: {e.Message}");
                return false;
            }
        }

        public static bool TryGetLaunchSize(int slotIndex, int playerCount, out int width, out int height)
        {
            width = 960;
            height = 540;

            if (!TryGetBounds(slotIndex, playerCount, allowHostMove: true, out var bounds))
                return false;

            width = bounds.Width;
            height = bounds.Height;
            return true;
        }

        private static bool VerifyWindowBounds(IntPtr hwnd, WindowBounds expected)
        {
            try
            {
                if (!GetWindowRect(hwnd, out var actual))
                    return true;

                int x = actual.Left;
                int y = actual.Top;
                int width = actual.Right - actual.Left;
                int height = actual.Bottom - actual.Top;
                return Math.Abs(x - expected.X) <= 24
                    && Math.Abs(y - expected.Y) <= 24
                    && Math.Abs(width - expected.Width) <= 96
                    && Math.Abs(height - expected.Height) <= 96;
            }
            catch
            {
                return true;
            }
        }

        private static void LogApplied(int slotIndex, int playerCount, WindowBounds bounds, IntPtr hwnd)
        {
            string key = $"{slotIndex}:{playerCount}:{bounds}:{hwnd}";
            int frame = Time.frameCount;
            if (key == _lastAppliedLogKey && frame - _lastAppliedLogFrame < 180)
                return;

            _lastAppliedLogKey = key;
            _lastAppliedLogFrame = frame;
            Plugin.Log.LogInfo($"[NetworkedWindowLayout] Applied slot={slotIndex} players={playerCount} bounds={bounds} hwnd=0x{hwnd.ToInt64():X}.");
        }

        public static int GetAvailableMonitorCount()
        {
            try
            {
                return Math.Max(1, GetMonitorBounds().Count);
            }
            catch
            {
                return 1;
            }
        }

        private static bool TryGetBounds(int slotIndex, int playerCount, bool allowHostMove, out WindowBounds bounds)
        {
            playerCount = Math.Max(1, Math.Min(4, playerCount));
            slotIndex = Math.Max(0, Math.Min(3, slotIndex));

            var monitors = GetMonitorBounds();
            var primary = monitors.Count > 0 ? monitors[0] : GetPrimaryFallbackBounds();

            if (ModConfig.MultiDisplayEnabled.Value && monitors.Count > slotIndex)
            {
                if (slotIndex == 0 && !allowHostMove)
                {
                    bounds = default;
                    return false;
                }

                bounds = monitors[slotIndex];
                return true;
            }

            Rect rect = GetNormalizedRect(slotIndex, playerCount);
            bounds = FromNormalized(primary, rect);
            return true;
        }

        private static Rect GetNormalizedRect(int slotIndex, int playerCount)
        {
            if (playerCount <= 1)
                return new Rect(0f, 0f, 1f, 1f);

            if (playerCount == 2)
            {
                bool vertical = ModConfig.TwoPlayerSplitOrientation.Value == "Vertical";
                if (vertical)
                    return slotIndex == 0 ? new Rect(0f, 0f, 0.5f, 1f) : new Rect(0.5f, 0f, 0.5f, 1f);
                return slotIndex == 0 ? new Rect(0f, 0.5f, 1f, 0.5f) : new Rect(0f, 0f, 1f, 0.5f);
            }

            if (playerCount == 3)
            {
                bool asymTop = ModConfig.ThreePlayerLayout.Value == "AsymmetricTop";
                if (asymTop)
                {
                    return slotIndex switch
                    {
                        0 => new Rect(0f, 0.5f, 1f, 0.5f),
                        1 => new Rect(0f, 0f, 0.5f, 0.5f),
                        _ => new Rect(0.5f, 0f, 0.5f, 0.5f),
                    };
                }

                return slotIndex switch
                {
                    0 => new Rect(0f, 0f, 0.5f, 1f),
                    1 => new Rect(0.5f, 0.5f, 0.5f, 0.5f),
                    _ => new Rect(0.5f, 0f, 0.5f, 0.5f),
                };
            }

            return slotIndex switch
            {
                0 => new Rect(0f, 0.5f, 0.5f, 0.5f),
                1 => new Rect(0.5f, 0.5f, 0.5f, 0.5f),
                2 => new Rect(0f, 0f, 0.5f, 0.5f),
                _ => new Rect(0.5f, 0f, 0.5f, 0.5f),
            };
        }

        private static WindowBounds FromNormalized(WindowBounds monitor, Rect rect)
        {
            int x = monitor.X + Mathf.RoundToInt(rect.x * monitor.Width);
            int y = monitor.Y + Mathf.RoundToInt((1f - rect.y - rect.height) * monitor.Height);
            int width = Math.Max(320, Mathf.RoundToInt(rect.width * monitor.Width));
            int height = Math.Max(240, Mathf.RoundToInt(rect.height * monitor.Height));
            return new WindowBounds(x, y, width, height);
        }

        private static List<WindowBounds> GetMonitorBounds()
        {
            var result = new List<WindowBounds>();
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (monitor, _, _, _) =>
                {
                    var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(monitor, ref info))
                        result.Add(new WindowBounds(info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right - info.rcMonitor.Left, info.rcMonitor.Bottom - info.rcMonitor.Top));
                    return true;
                }, IntPtr.Zero);
            }
            catch { }

            if (result.Count == 0)
                result.Add(GetPrimaryFallbackBounds());

            return result;
        }

        private static WindowBounds GetPrimaryFallbackBounds()
        {
            int width = 1920;
            int height = 1080;
            try
            {
                width = GetSystemMetrics(0);
                height = GetSystemMetrics(1);
            }
            catch { }

            if (width <= 0) width = Screen.currentResolution.width > 0 ? Screen.currentResolution.width : 1920;
            if (height <= 0) height = Screen.currentResolution.height > 0 ? Screen.currentResolution.height : 1080;
            return new WindowBounds(0, 0, width, height);
        }

        private readonly struct WindowBounds
        {
            public WindowBounds(int x, int y, int width, int height)
            {
                X = x;
                Y = y;
                Width = width;
                Height = height;
            }

            public int X { get; }
            public int Y { get; }
            public int Width { get; }
            public int Height { get; }

            public override string ToString() => $"{X},{Y},{Width}x{Height}";
        }

        private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, IntPtr lprcMonitor, IntPtr dwData);

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

        [DllImport("user32.dll")]
        private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

        [DllImport("user32.dll", CharSet = CharSet.Auto)]
        private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);
    }
}
