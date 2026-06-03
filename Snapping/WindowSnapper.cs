using Swoosh.Native;

namespace Swoosh.Snapping;

/// <summary>Resolves the window under the cursor and snaps it to a zone.</summary>
public sealed class WindowSnapper
{
    /// <summary>Logical-pixel band below the top of a window treated as the titlebar.</summary>
    public int TitlebarHeight { get; set; } = 44;

    public IntPtr WindowUnderCursor()
    {
        if (!Win32.GetCursorPos(out var pt)) return IntPtr.Zero;
        IntPtr h = Win32.WindowFromPoint(pt);
        if (h == IntPtr.Zero) return IntPtr.Zero;
        return Win32.GetAncestor(h, Win32.GA_ROOT);
    }

    /// <summary>
    /// Returns the window to act on if the cursor is currently over a manageable
    /// window's titlebar (Swish-style), else IntPtr.Zero. <paramref name="diag"/>
    /// captures why arming did/didn't happen for the log.
    /// </summary>
    public IntPtr ArmTarget(out string diag)
    {
        if (!Win32.GetCursorPos(out var pt)) { diag = "no-cursor"; return IntPtr.Zero; }
        IntPtr raw = Win32.WindowFromPoint(pt);
        IntPtr h = raw == IntPtr.Zero ? IntPtr.Zero : Win32.GetAncestor(raw, Win32.GA_ROOT);
        if (!IsManageable(h))
        {
            diag = $"not-manageable hwnd=0x{h.ToInt64():X} class='{Win32.GetWindowClass(h)}'";
            return IntPtr.Zero;
        }

        int top = WindowTop(h);
        if (!Win32.GetWindowRect(h, out var r)) { diag = "no-rect"; return IntPtr.Zero; }
        uint dpi = Win32.GetDpiForWindow(h);
        if (dpi == 0) dpi = 96;
        int band = (int)Math.Round(TitlebarHeight * dpi / 96.0);
        bool over = pt.Y >= top && pt.Y <= top + band && pt.X >= r.Left && pt.X <= r.Right;
        diag = $"cur=({pt.X},{pt.Y}) top={top} band={band} L={r.Left} R={r.Right} over={over} class='{Win32.GetWindowClass(h)}'";
        return over ? h : IntPtr.Zero;
    }

    private static int WindowTop(IntPtr hwnd)
    {
        if (!Win32.GetWindowRect(hwnd, out var r)) return 0;
        int top = r.Top;
        if (Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var f, System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>()) == 0)
            top = f.Top;
        return top;
    }

    public bool CursorOverTitlebar(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd)) return false;
        if (!Win32.GetCursorPos(out var pt)) return false;
        if (!Win32.GetWindowRect(hwnd, out var r)) return false;
        int top = WindowTop(hwnd);
        uint dpi = Win32.GetDpiForWindow(hwnd);
        if (dpi == 0) dpi = 96;
        int band = (int)Math.Round(TitlebarHeight * dpi / 96.0);
        return pt.Y >= top && pt.Y <= top + band &&
               pt.X >= r.Left && pt.X <= r.Right;
    }

    public bool IsManageable(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !Win32.IsWindow(hwnd) || !Win32.IsWindowVisible(hwnd))
            return false;
        string cls = Win32.GetWindowClass(hwnd);
        // Exclude the desktop, shell, and start/search surfaces.
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Windows.UI.Core.CoreWindow"
            or "XamlExplorerHostIslandWindow")
            return false;
        long style = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
        return (style & Win32.WS_CAPTION) != 0; // has a titlebar
    }

    public Win32.RECT WorkAreaFor(IntPtr hwnd)
    {
        IntPtr mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
        Win32.GetMonitorInfo(mon, ref mi);
        return mi.rcWork;
    }

    /// <summary>Compute the visible target rectangle for a zone within a work area.</summary>
    public static Win32.RECT ZoneRect(Win32.RECT w, SnapZone zone)
    {
        int x = w.Left, y = w.Top, cw = w.Width, ch = w.Height;
        int halfW = cw / 2, halfH = ch / 2;
        int thirdW = cw / 3;
        return zone switch
        {
            SnapZone.LeftHalf => R(x, y, halfW, ch),
            SnapZone.RightHalf => R(x + halfW, y, cw - halfW, ch),
            SnapZone.TopHalf => R(x, y, cw, halfH),
            SnapZone.BottomHalf => R(x, y + halfH, cw, ch - halfH),
            SnapZone.TopLeft => R(x, y, halfW, halfH),
            SnapZone.TopRight => R(x + halfW, y, cw - halfW, halfH),
            SnapZone.BottomLeft => R(x, y + halfH, halfW, ch - halfH),
            SnapZone.BottomRight => R(x + halfW, y + halfH, cw - halfW, ch - halfH),
            SnapZone.LeftThird => R(x, y, thirdW, ch),
            SnapZone.CenterThird => R(x + thirdW, y, thirdW, ch),
            SnapZone.RightThird => R(x + 2 * thirdW, y, cw - 2 * thirdW, ch),
            SnapZone.Center => R(x + cw / 6, y + ch / 6, cw * 2 / 3, ch * 2 / 3),
            SnapZone.Maximize => w,
            _ => w,
        };
    }

    private static Win32.RECT R(int x, int y, int cw, int ch) =>
        new() { Left = x, Top = y, Right = x + cw, Bottom = y + ch };

    public void Apply(IntPtr hwnd, SnapZone zone)
    {
        if (zone == SnapZone.None || !IsManageable(hwnd)) return;

        if (zone == SnapZone.Minimize)
        {
            Win32.ShowWindow(hwnd, Win32.SW_MINIMIZE);
            return;
        }

        // Restore first so SetWindowPos geometry takes effect.
        long style = Win32.GetWindowLong(hwnd, Win32.GWL_STYLE);
        if ((style & (Win32.WS_MAXIMIZE | Win32.WS_MINIMIZE)) != 0)
            Win32.ShowWindow(hwnd, Win32.SW_RESTORE);

        if (zone == SnapZone.Maximize)
        {
            Win32.ShowWindow(hwnd, Win32.SW_MAXIMIZE);
            return;
        }

        var work = WorkAreaFor(hwnd);
        var target = ZoneRect(work, zone);

        // Compensate for the invisible DWM resize border so the *visible* frame
        // aligns pixel-perfectly with the work-area subdivision (Swish-style).
        int ml = 0, mt = 0, mr = 0, mb = 0;
        if (Win32.GetWindowRect(hwnd, out var wr) &&
            Win32.DwmGetWindowAttribute(hwnd, Win32.DWMWA_EXTENDED_FRAME_BOUNDS,
                out var fb, System.Runtime.InteropServices.Marshal.SizeOf<Win32.RECT>()) == 0)
        {
            ml = fb.Left - wr.Left;
            mt = fb.Top - wr.Top;
            mr = wr.Right - fb.Right;
            mb = wr.Bottom - fb.Bottom;
        }

        int px = target.Left - ml;
        int py = target.Top - mt;
        int pw = target.Width + ml + mr;
        int ph = target.Height + mt + mb;

        Win32.SetWindowPos(hwnd, IntPtr.Zero, px, py, pw, ph,
            Win32.SWP_NOZORDER | Win32.SWP_NOOWNERZORDER | Win32.SWP_NOACTIVATE);
    }
}
