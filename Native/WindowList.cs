using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Swoosh.Native;

/// <summary>One open application window eligible for the app switcher.</summary>
public sealed record AppWindow(IntPtr Hwnd, string Title, ImageSource? Icon);
/// <summary>Enumerates the user's open top-level application windows (the Alt+Tab set on the
/// current virtual desktop) and resolves each one's icon for the switcher HUD.</summary>
public static class WindowList
{
    /// <summary>The Alt+Tab-eligible windows on the current desktop, in Z-order
    /// (front-most first, which is how EnumWindows returns them).</summary>
    public static List<AppWindow> GetSwitchableWindows()
    {
        var result = new List<AppWindow>();
        uint ownPid = (uint)Environment.ProcessId;

        Win32.EnumWindows((hwnd, _) =>
        {
            if (!IsAltTabWindow(hwnd)) return true;

            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == ownPid) return true; // never list Swoosh's own HUD/settings windows

            string title = Win32.GetWindowTitle(hwnd);
            if (string.IsNullOrWhiteSpace(title)) return true;

            result.Add(new AppWindow(hwnd, title, GetIcon(hwnd)));
            return true;
        }, IntPtr.Zero);

        return result;
    }

    /// <summary>Standard Alt+Tab eligibility: a visible, un-cloaked, top-level window that is
    /// its own root owner and not a pure tool window.</summary>
    private static bool IsAltTabWindow(IntPtr hwnd)
    {
        if (!Win32.IsWindowVisible(hwnd)) return false;
        if (Win32.IsCloaked(hwnd)) return false;                 // on another desktop or suspended

        // The Alt+Tab entry is the root owner of an owner chain, so owned dialogs fold into
        // their owner rather than appearing as separate entries.
        if (Win32.GetAncestor(hwnd, Win32.GA_ROOTOWNER) != hwnd) return false;

        long ex = Win32.GetWindowLong(hwnd, Win32.GWL_EXSTYLE);
        // Tool windows are excluded unless they explicitly opt into the taskbar/Alt+Tab.
        if ((ex & Win32.WS_EX_TOOLWINDOW) != 0 && (ex & Win32.WS_EX_APPWINDOW) == 0) return false;

        return true;
    }

    /// <summary>Resolve a window's icon, preferring the large window icon and falling back through
    /// the smaller window/class icons, then the process executable's icon. Returns a frozen
    /// <see cref="ImageSource"/> or null.</summary>
    private static ImageSource? GetIcon(IntPtr hwnd)
    {
        IntPtr hicon = SendIcon(hwnd, Win32.ICON_BIG);
        if (hicon == IntPtr.Zero) hicon = SendIcon(hwnd, Win32.ICON_SMALL2);
        if (hicon == IntPtr.Zero) hicon = SendIcon(hwnd, Win32.ICON_SMALL);
        if (hicon == IntPtr.Zero) hicon = Win32.GetClassLongPtr(hwnd, Win32.GCL_HICON);
        if (hicon == IntPtr.Zero) hicon = Win32.GetClassLongPtr(hwnd, Win32.GCL_HICONSM);

        if (hicon != IntPtr.Zero)
        {
            try
            {
                var src = Imaging.CreateBitmapSourceFromHIcon(hicon, Int32Rect.Empty,
                    BitmapSizeOptions.FromEmptyOptions());
                src.Freeze();
                return src;
            }
            catch { /* fall through to the executable-icon fallback */ }
        }

        return GetExecutableIcon(hwnd);
    }

    /// <summary>Last-resort icon: extract the icon embedded in the owning process's executable.
    /// Covers apps that expose no window/class icon (e.g. some console hosts).</summary>
    private static ImageSource? GetExecutableIcon(IntPtr hwnd)
    {
        try
        {
            Win32.GetWindowThreadProcessId(hwnd, out uint pid);
            if (pid == 0) return null;
            using var proc = System.Diagnostics.Process.GetProcessById((int)pid);
            string? path = proc.MainModule?.FileName;
            if (string.IsNullOrEmpty(path)) return null;
            using var ico = System.Drawing.Icon.ExtractAssociatedIcon(path);
            if (ico == null) return null;
            var src = Imaging.CreateBitmapSourceFromHIcon(ico.Handle, Int32Rect.Empty,
                BitmapSizeOptions.FromEmptyOptions());
            src.Freeze();
            return src;
        }
        catch
        {
            // Accessing MainModule of an elevated process from a medium-IL process throws; ignore.
            return null;
        }
    }

    private static IntPtr SendIcon(IntPtr hwnd, int which)
    {
        // Use a short timeout so a hung app never stalls the gesture.
        if (Win32.SendMessageTimeout(hwnd, Win32.WM_GETICON, new IntPtr(which), IntPtr.Zero,
                Win32.SMTO_ABORTIFHUNG, 60, out IntPtr res) == IntPtr.Zero)
            return IntPtr.Zero;
        return res;
    }
}
