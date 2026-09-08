using System.Runtime.InteropServices;

namespace Swoosh.Native;

public static class Win32
{
    public const int WM_INPUT = 0x00FF;
    public const int WM_HOTKEY = 0x0312;
    public const int WM_DESTROY = 0x0002;
    public const int WM_CLOSE = 0x0010;
    public const int WM_SYSCOMMAND = 0x0112;
    public const int SC_CLOSE = 0xF060;

    public const int RIM_TYPEHID = 2;
    public const uint RIDEV_INPUTSINK = 0x00000100;
    public const uint RID_INPUT = 0x10000003;
    public const uint RIDI_PREPARSEDDATA = 0x20000005;
    public const uint RIDI_DEVICEINFO = 0x2000000b;

    // GetAncestor
    public const uint GA_ROOT = 2;
    public const uint GA_ROOTOWNER = 3;

    // Window enumeration / alt-tab eligibility.
    public const int GW_OWNER = 4;
    public const long WS_EX_APPWINDOW = 0x00040000;
    public const int DWMWA_CLOAKED = 14;
    public const int WM_GETICON = 0x007F;
    public const int ICON_SMALL = 0;
    public const int ICON_BIG = 1;
    public const int ICON_SMALL2 = 2;
    public const int GCL_HICON = -14;
    public const int GCL_HICONSM = -34;
    public const uint SMTO_ABORTIFHUNG = 0x0002;

    // SetWindowPos flags
    public const uint SWP_NOSIZE = 0x0001;
    public const uint SWP_NOZORDER = 0x0004;
    public const uint SWP_NOACTIVATE = 0x0010;
    public const uint SWP_NOOWNERZORDER = 0x0200;
    public const uint SWP_FRAMECHANGED = 0x0020;
    public const uint SWP_NOSENDCHANGING = 0x0400;
    public const uint SWP_ASYNCWINDOWPOS = 0x4000;

    // ShowWindow
    public const int SW_RESTORE = 9;
    public const int SW_MINIMIZE = 6;
    public const int SW_MAXIMIZE = 3;
    public const int SW_SHOWNORMAL = 1;

    // GetWindowLong
    public const int GWL_STYLE = -16;
    public const long WS_MAXIMIZE = 0x01000000;
    public const long WS_MINIMIZE = 0x20000000;
    public const long WS_CAPTION = 0x00C00000;
    public const long WS_THICKFRAME = 0x00040000;

    public const uint MONITOR_DEFAULTTONEAREST = 0x00000002;
    public const uint MONITOR_DEFAULTTONULL = 0x00000000;

    // Extended window styles for the click-through overlay.
    public const int GWL_EXSTYLE = -20;
    public const long WS_EX_TRANSPARENT = 0x00000020;
    public const long WS_EX_TOOLWINDOW = 0x00000080;
    public const long WS_EX_NOACTIVATE = 0x08000000;
    public const long WS_EX_LAYERED = 0x00080000;

    public static readonly IntPtr HWND_TOPMOST = new(-1);

    // DWM
    public const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    public const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;
    public const int DWMWA_SYSTEMBACKDROP_TYPE = 38;
    public const int DWMSBT_MAINWINDOW = 2;  // Mica
    public const int DWMSBT_TRANSIENTWINDOW = 3; // Acrylic
    public const int DWMWA_WINDOW_CORNER_PREFERENCE = 33;
    public const int DWMWCP_ROUND = 2;       // full radius
    public const int DWMWCP_ROUNDSMALL = 3;   // tighter radius (menus/popups)

    [DllImport("dwmapi.dll")]
    public static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

    [StructLayout(LayoutKind.Sequential)]
    public struct MARGINS { public int Left, Right, Top, Bottom; }

    [DllImport("dwmapi.dll")]
    public static extern int DwmExtendFrameIntoClientArea(IntPtr hwnd, ref MARGINS margins);

    // DWM thumbnails: a live, GPU-composited preview of one window rendered into a rect on
    // another window's client area (the same mechanism behind Alt+Tab and Snap Assist
    // previews). See DWM_TNP_* flags below for which fields of DWM_THUMBNAIL_PROPERTIES apply.
    public const int DWM_TNP_RECTDESTINATION = 0x00000001;
    public const int DWM_TNP_RECTSOURCE = 0x00000002;
    public const int DWM_TNP_OPACITY = 0x00000004;
    public const int DWM_TNP_VISIBLE = 0x00000008;
    public const int DWM_TNP_SOURCECLIENTAREAONLY = 0x00000010;

    [StructLayout(LayoutKind.Sequential)]
    public struct DWM_THUMBNAIL_PROPERTIES
    {
        public int dwFlags;
        public RECT rcDestination;
        public RECT rcSource;
        public byte opacity;
        [MarshalAs(UnmanagedType.Bool)] public bool fVisible;
        [MarshalAs(UnmanagedType.Bool)] public bool fSourceClientAreaOnly;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct SIZE { public int cx, cy; }

    [DllImport("dwmapi.dll")]
    public static extern int DwmRegisterThumbnail(IntPtr hwndDestination, IntPtr hwndSource, out IntPtr thumb);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUnregisterThumbnail(IntPtr thumb);

    [DllImport("dwmapi.dll")]
    public static extern int DwmUpdateThumbnailProperties(IntPtr thumb, ref DWM_THUMBNAIL_PROPERTIES props);

    [DllImport("dwmapi.dll")]
    public static extern int DwmQueryThumbnailSourceSize(IntPtr thumb, out SIZE size);

    /// <summary>Apply a translucent Acrylic backdrop + dark/light framing + rounded corners
    /// to a flyout-style window (e.g. the tray menu). No-op before Win11.</summary>
    public static void EnableAcrylicFlyout(IntPtr hwnd, bool dark)
    {
        if (hwnd == IntPtr.Zero) return;
        int d = dark ? 1 : 0;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref d, sizeof(int));
        int backdrop = DWMSBT_TRANSIENTWINDOW; // Acrylic
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
        int pref = DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
        var m = new MARGINS { Left = -1, Right = -1, Top = -1, Bottom = -1 };
        DwmExtendFrameIntoClientArea(hwnd, ref m);
    }

    // Blur-behind for per-pixel-transparent (AllowsTransparency=true) WPF windows. Unlike
    // DWMWA_SYSTEMBACKDROP_TYPE (which needs a normal opaque window and blends through actual
    // zero-alpha pixels), this undocumented composition attribute is the technique that works
    // together with a layered/transparent window: it tells DWM to blur whatever is behind the
    // window wherever the window itself is transparent or tinted, rather than requiring the
    // window to render true holes.
    private const int WCA_ACCENT_POLICY = 19;
    private const int ACCENT_ENABLE_ACRYLICBLURBEHIND = 4;

    [StructLayout(LayoutKind.Sequential)]
    private struct ACCENT_POLICY
    {
        public int AccentState;
        public int AccentFlags;
        public int GradientColor; // 0xAABBGGRR
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WINDOWCOMPOSITIONATTRIBDATA
    {
        public int Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(IntPtr hwnd, ref WINDOWCOMPOSITIONATTRIBDATA data);

    /// <summary>Blur whatever is behind this transparent window, tinted by the given ARGB color
    /// (its alpha controls how strongly the tint reads over the blur). Needs the window to be
    /// per-pixel transparent (WPF <c>AllowsTransparency = true</c>); does nothing useful on a
    /// normal opaque window (use <see cref="EnableAcrylicFlyout"/> for those instead).</summary>
    public static void EnableAcrylicBlurBehind(IntPtr hwnd, byte a, byte r, byte g, byte b)
    {
        if (hwnd == IntPtr.Zero) return;
        var accent = new ACCENT_POLICY
        {
            AccentState = ACCENT_ENABLE_ACRYLICBLURBEHIND,
            GradientColor = (a << 24) | (b << 16) | (g << 8) | r,
        };
        int size = Marshal.SizeOf<ACCENT_POLICY>();
        IntPtr accentPtr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(accent, accentPtr, false);
            var data = new WINDOWCOMPOSITIONATTRIBDATA
            {
                Attribute = WCA_ACCENT_POLICY,
                Data = accentPtr,
                SizeOfData = size,
            };
            SetWindowCompositionAttribute(hwnd, ref data);
        }
        finally
        {
            Marshal.FreeHGlobal(accentPtr);
        }
    }

    /// <summary>Apply a Mica backdrop + dark titlebar to a Win11 window (no-op pre-Win11).</summary>
    public static void EnableMicaDark(IntPtr hwnd)
    {
        int on = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref on, sizeof(int));
        int backdrop = DWMSBT_MAINWINDOW;
        DwmSetWindowAttribute(hwnd, DWMWA_SYSTEMBACKDROP_TYPE, ref backdrop, sizeof(int));
    }

    /// <summary>Give a top-level popup (e.g. the tray menu) Win11 rounded corners and
    /// dark-mode framing. No-op before Windows 11; harmless on older builds.</summary>
    public static void EnableRoundedDark(IntPtr hwnd, bool smallRadius = true)
    {
        if (hwnd == IntPtr.Zero) return;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));
        int pref = smallRadius ? DWMWCP_ROUNDSMALL : DWMWCP_ROUND;
        DwmSetWindowAttribute(hwnd, DWMWA_WINDOW_CORNER_PREFERENCE, ref pref, sizeof(int));
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT { public int X; public int Y; }

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
        public int Width => Right - Left;
        public int Height => Bottom - Top;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct WINDOWPLACEMENT
    {
        public int length;
        public uint flags;
        public uint showCmd;
        public POINT ptMinPosition;
        public POINT ptMaxPosition;
        public RECT rcNormalPosition;
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTHEADER
    {
        public uint dwType;
        public uint dwSize;
        public IntPtr hDevice;
        public IntPtr wParam;
    }

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICE
    {
        public ushort usUsagePage;
        public ushort usUsage;
        public uint dwFlags;
        public IntPtr hwndTarget;
    }

    [DllImport("user32.dll")]
    public static extern bool RegisterRawInputDevices(
        [MarshalAs(UnmanagedType.LPArray, SizeParamIndex = 1)] RAWINPUTDEVICE[] pRawInputDevices,
        uint uiNumDevices, uint cbSize);

    [StructLayout(LayoutKind.Sequential)]
    public struct RAWINPUTDEVICELIST
    {
        public IntPtr hDevice;
        public uint dwType;
    }

    [DllImport("user32.dll", SetLastError = true)]
    public static extern uint GetRawInputDeviceList(
        [In, Out] RAWINPUTDEVICELIST[]? pRawInputDeviceList, ref uint puiNumDevices, uint cbSize);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputData(IntPtr hRawInput, uint uiCommand,
        IntPtr pData, ref uint pcbSize, uint cbSizeHeader);

    [DllImport("user32.dll")]
    public static extern uint GetRawInputDeviceInfo(IntPtr hDevice, uint uiCommand,
        IntPtr pData, ref uint pcbSize);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr hWnd, uint Msg, IntPtr wParam, IntPtr lParam);

    /// <summary>Politely ask a window to close (same as clicking its X). The app can prompt to
    /// save and may ignore it; we never force-kill.</summary>
    public static void CloseWindow(IntPtr hwnd)
    {
        if (hwnd != IntPtr.Zero)
            PostMessage(hwnd, WM_SYSCOMMAND, (IntPtr)SC_CLOSE, IntPtr.Zero);
    }

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern IntPtr WindowFromPoint(POINT Point);

    [DllImport("user32.dll")]
    public static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter,
        int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("winmm.dll")]
    public static extern uint timeBeginPeriod(uint uPeriod);

    [DllImport("winmm.dll")]
    public static extern uint timeEndPeriod(uint uPeriod);

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "GetWindowLongPtrW")]
    public static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", SetLastError = true, EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public static long GetWindowLong(IntPtr hWnd, int nIndex) =>
        GetWindowLongPtr(hWnd, nIndex).ToInt64();

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    public delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, ref RECT lprcClip, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr lprcClip, MonitorEnumProc lpfnEnum, IntPtr dwData);

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, IntPtr lpdwProcessId);

    [DllImport("user32.dll")]
    public static extern bool AttachThreadInput(uint idAttach, uint idAttachTo, bool fAttach);

    [DllImport("user32.dll")]
    public static extern IntPtr SetFocus(IntPtr hWnd);

    [DllImport("kernel32.dll")]
    public static extern uint GetCurrentThreadId();

    [DllImport("user32.dll")]
    public static extern bool BringWindowToTop(IntPtr hWnd);

    // SPI_*FOREGROUNDLOCKTIMEOUT: Windows refuses SetForegroundWindow from a background process
    // unless it recently received real user input, as an anti-annoyance heuristic - and repeated
    // rapid calls (e.g. re-triggering Snap Assist several times quickly, or right after a system
    // UI like Settings held focus) can make even the AttachThreadInput trick below get silently
    // ignored. Zeroing this out for the duration of the call is the standard, documented way to
    // make SetForegroundWindow unconditionally succeed regardless of that heuristic.
    private const uint SPI_GETFOREGROUNDLOCKTIMEOUT = 0x2000;
    private const uint SPI_SETFOREGROUNDLOCKTIMEOUT = 0x2001;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, ref uint pvParam, uint fWinIni);

    /// <summary>Force a window to the foreground reliably, defeating the foreground-lock
    /// that makes a bare SetForegroundWindow fail when the caller isn't the active app.
    /// Also raises the window in the Z-order so it sits above other windows, not just
    /// activated behind them.</summary>
    public static void ForceForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;
        IntPtr fg = GetForegroundWindow();
        uint fgThread = GetWindowThreadProcessId(fg, IntPtr.Zero);
        uint thisThread = GetCurrentThreadId();
        bool attached = fgThread != thisThread && AttachThreadInput(fgThread, thisThread, true);

        uint oldTimeout = 0;
        SystemParametersInfo(SPI_GETFOREGROUNDLOCKTIMEOUT, 0, ref oldTimeout, 0);
        uint zero = 0;
        // No SPIF_SENDCHANGE/SPIF_UPDATEINIFILE flags: this is a transient, in-process-only
        // change we restore in the finally block, not a persisted system setting.
        SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, ref zero, 0);
        try
        {
            BringWindowToTop(hwnd);
            SetForegroundWindow(hwnd);
            SetFocus(hwnd);
        }
        finally
        {
            SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0, ref oldTimeout, 0);
            if (attached) AttachThreadInput(fgThread, thisThread, false);
        }
    }

    // Low-level mouse hook (used to dismiss the tray flyout on an outside click).
    public const int WH_MOUSE_LL = 14;
    public const int WM_LBUTTONDOWN = 0x0201;
    public const int WM_MOUSEMOVE = 0x0200;
    public const int WM_RBUTTONDOWN = 0x0204;
    public const int WM_MBUTTONDOWN = 0x0207;
    public const int WM_MBUTTONUP = 0x0208;
    public const int WM_NCLBUTTONDOWN = 0x00A1;
    public const int WM_NCRBUTTONDOWN = 0x00A4;
    public const uint LLMHF_INJECTED = 0x00000001;

    [StructLayout(LayoutKind.Sequential)]
    public struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint mouseData;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    public delegate IntPtr LowLevelMouseProc(int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn,
        IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetWindowText(IntPtr hWnd, char[] lpString, int nMaxCount);

    [DllImport("user32.dll")]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    [DllImport("dwmapi.dll")]
    public static extern int DwmGetWindowAttribute(IntPtr hwnd, int dwAttribute,
        out RECT pvAttribute, int cbAttribute);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    [DllImport("shcore.dll")]
    public static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);

    /// <summary>Effective DPI of the monitor currently under the mouse cursor (falls back to 96).</summary>
    public static uint GetDpiForCursor()
    {
        if (GetCursorPos(out var pt))
        {
            IntPtr mon = MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
            if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out uint dx, out _) == 0 && dx > 0)
                return dx;
        }
        return 96;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern int GetClassName(IntPtr hWnd, char[] lpClassName, int nMaxCount);

    public static string GetWindowClass(IntPtr hWnd)
    {
        var buf = new char[256];
        int n = GetClassName(hWnd, buf, buf.Length);
        return n > 0 ? new string(buf, 0, n) : string.Empty;
    }

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern bool MessageBeep(uint uType);

    public const byte VK_SHIFT = 0x10;
    public const byte VK_CONTROL = 0x11;
    public const byte VK_MENU = 0x12; // Alt
    public const byte VK_MBUTTON = 0x04;
    public const byte VK_ESCAPE = 0x1B;
    public const byte VK_LWIN = 0x5B;
    public const byte VK_LEFT = 0x25;
    public const byte VK_RIGHT = 0x27;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll")]
    public static extern short GetAsyncKeyState(int vKey);

    /// <summary>True if the given virtual key is currently held down.</summary>
    public static bool IsKeyDown(int vKey) => (GetAsyncKeyState(vKey) & 0x8000) != 0;

    /// <summary>Switch the active virtual desktop left/right via Win+Ctrl+Arrow.</summary>
    public static void SwitchVirtualDesktop(bool right)
    {
        byte arrow = right ? VK_RIGHT : VK_LEFT;
        keybd_event(VK_LWIN, 0, 0, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, 0, UIntPtr.Zero);
        keybd_event(arrow, 0, 0, UIntPtr.Zero);
        keybd_event(arrow, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_CONTROL, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
        keybd_event(VK_LWIN, 0, KEYEVENTF_KEYUP, UIntPtr.Zero);
    }

    public static string GetWindowTitle(IntPtr hWnd)
    {
        int len = GetWindowTextLength(hWnd);
        if (len <= 0) return string.Empty;
        var buf = new char[len + 1];
        int n = GetWindowText(hWnd, buf, buf.Length);
        return new string(buf, 0, n);
    }

    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, int uCmd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    public static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int dwAttribute, out int pvAttribute, int cbAttribute);

    /// <summary>True if a window is cloaked by DWM (e.g. on another virtual desktop, or a
    /// suspended UWP shell window). Cloaked windows should be excluded from the app switcher.</summary>
    public static bool IsCloaked(IntPtr hWnd)
    {
        return DwmGetWindowAttributeInt(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0;
    }

    [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")]
    public static extern IntPtr GetClassLongPtr(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessageTimeout(IntPtr hWnd, int Msg, IntPtr wParam, IntPtr lParam,
        uint fuFlags, uint uTimeout, out IntPtr lpdwResult);
}
