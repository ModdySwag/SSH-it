using System.Runtime.InteropServices;

namespace SshIt.Services;

/// <summary>Small DWM/Win32 helpers for standard Windows 11 window behaviour.</summary>
public static class WindowEffects
{
    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    private const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    private const int DWMWCP_ROUND = 2;

    public static void ApplyDarkMode(IntPtr hwnd, bool dark)
    {
        try
        {
            var value = dark ? 1 : 0;
            DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref value, sizeof(int));
            // Older builds used attribute 19.
            DwmSetWindowAttribute(hwnd, 19, ref value, sizeof(int));
            var corner = DWMWCP_ROUND;
            DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref corner, sizeof(int));
        }
        catch { }
    }

    /// <summary>Maximising a borderless window otherwise covers the taskbar.</summary>
    public static void ClampMaximizedSize(IntPtr hwnd, IntPtr lParam)
    {
        try
        {
            var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            if (monitor == IntPtr.Zero) return;

            var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (!GetMonitorInfo(monitor, ref info)) return;

            var mmi = Marshal.PtrToStructure<MINMAXINFO>(lParam);
            mmi.ptMaxPosition.X = info.rcWork.Left - info.rcMonitor.Left;
            mmi.ptMaxPosition.Y = info.rcWork.Top - info.rcMonitor.Top;
            mmi.ptMaxSize.X = info.rcWork.Right - info.rcWork.Left;
            mmi.ptMaxSize.Y = info.rcWork.Bottom - info.rcWork.Top;
            mmi.ptMaxTrackSize.X = mmi.ptMaxSize.X;
            mmi.ptMaxTrackSize.Y = mmi.ptMaxSize.Y;
            Marshal.StructureToPtr(mmi, lParam, true);
        }
        catch { }
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MINMAXINFO
    {
        public POINT ptReserved;
        public POINT ptMaxSize;
        public POINT ptMaxPosition;
        public POINT ptMinTrackSize;
        public POINT ptMaxTrackSize;
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
}
