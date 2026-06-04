using Swoosh.Native;

namespace Swoosh.Snapping;

/// <summary>A cardinal direction toward an adjacent physical display.</summary>
public enum MonitorDirection { Up, Down, Left, Right }

/// <summary>
/// Enumerates the physical monitors and answers adjacency questions used by the
/// "move window to the next display" gesture: which monitor a window currently
/// lives on, and which monitor (if any) sits directly up / down / left / right of
/// it. Geometry is in physical pixels (monitor bounds), so it is DPI-correct.
/// </summary>
public static class MonitorLayout
{
    public readonly record struct Mon(IntPtr Handle, Win32.RECT Bounds, Win32.RECT Work)
    {
        public int CenterX => (Bounds.Left + Bounds.Right) / 2;
        public int CenterY => (Bounds.Top + Bounds.Bottom) / 2;
    }

    /// <summary>All monitors currently attached, in enumeration order.</summary>
    public static List<Mon> All()
    {
        var list = new List<Mon>();
        Win32.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr hdc, ref Win32.RECT clip, IntPtr data) =>
        {
            var mi = new Win32.MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<Win32.MONITORINFO>() };
            if (Win32.GetMonitorInfo(h, ref mi))
                list.Add(new Mon(h, mi.rcMonitor, mi.rcWork));
            return true;
        }, IntPtr.Zero);
        return list;
    }

    /// <summary>The monitor the window is currently on (nearest if it straddles).</summary>
    public static Mon? ForWindow(IntPtr hwnd, List<Mon>? all = null)
    {
        IntPtr mon = Win32.MonitorFromWindow(hwnd, Win32.MONITOR_DEFAULTTONEAREST);
        all ??= All();
        foreach (var m in all)
            if (m.Handle == mon) return m;
        return all.Count > 0 ? all[0] : null;
    }

    /// <summary>
    /// The monitor directly adjacent to <paramref name="from"/> in the given
    /// direction, or null if none. A candidate must sit on the correct side and
    /// overlap on the perpendicular axis (so a display that is up-and-far-left does
    /// not count as "left"); among those, the closest one wins.
    /// </summary>
    public static Mon? Adjacent(Mon from, MonitorDirection dir, List<Mon> all)
    {
        Mon? best = null;
        long bestDist = long.MaxValue;
        foreach (var m in all)
        {
            if (m.Handle == from.Handle) continue;

            bool onSide;
            bool overlap;
            switch (dir)
            {
                case MonitorDirection.Left:
                    onSide = m.Bounds.Right <= from.Bounds.Left + 1;
                    overlap = VOverlap(from.Bounds, m.Bounds);
                    break;
                case MonitorDirection.Right:
                    onSide = m.Bounds.Left >= from.Bounds.Right - 1;
                    overlap = VOverlap(from.Bounds, m.Bounds);
                    break;
                case MonitorDirection.Up:
                    onSide = m.Bounds.Bottom <= from.Bounds.Top + 1;
                    overlap = HOverlap(from.Bounds, m.Bounds);
                    break;
                default: // Down
                    onSide = m.Bounds.Top >= from.Bounds.Bottom - 1;
                    overlap = HOverlap(from.Bounds, m.Bounds);
                    break;
            }
            if (!onSide || !overlap) continue;

            long dx = m.CenterX - from.CenterX;
            long dy = m.CenterY - from.CenterY;
            long dist = dx * dx + dy * dy;
            if (dist < bestDist) { bestDist = dist; best = m; }
        }
        return best;
    }

    /// <summary>Availability of a neighbor in each direction (for the HUD map).</summary>
    public static (bool up, bool down, bool left, bool right) Neighbors(Mon from, List<Mon> all) =>
    (
        Adjacent(from, MonitorDirection.Up, all) != null,
        Adjacent(from, MonitorDirection.Down, all) != null,
        Adjacent(from, MonitorDirection.Left, all) != null,
        Adjacent(from, MonitorDirection.Right, all) != null
    );

    private static bool VOverlap(Win32.RECT a, Win32.RECT b) => a.Top < b.Bottom && b.Top < a.Bottom;
    private static bool HOverlap(Win32.RECT a, Win32.RECT b) => a.Left < b.Right && b.Left < a.Right;
}
