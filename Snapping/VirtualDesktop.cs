using System.Runtime.InteropServices;

namespace Swoosh.Snapping;

public enum DesktopDirection { Left, Right }

/// <summary>
/// Moves a top-level window (including windows owned by other processes) to the
/// adjacent virtual desktop.
///
/// The public <c>IVirtualDesktopManager.MoveWindowToDesktop</c> returns
/// E_ACCESSDENIED (0x80070005) for foreign-process HWNDs because it checks
/// process identity. We instead use the undocumented
/// <c>IVirtualDesktopManagerInternal.MoveViewToDesktop</c>, which operates on an
/// abstract <c>IApplicationView</c> token obtained from the HWND and has no such
/// check. COM definitions target Windows 11 build 26100+ (24H2/25H2 vtable),
/// which covers this machine's build 26220.
/// </summary>
public static class VirtualDesktop
{
    // CLSIDs
    private static readonly Guid CLSID_ImmersiveShell =
        new("C2F03A33-21F5-47FA-B4BB-156362A2F239");
    private static readonly Guid CLSID_VirtualDesktopManagerInternal =
        new("C5E0CDCA-7B6E-41B2-9FC4-D93975CC467B");

    // COM interop (24H2 / build 26100+ vtable)
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("6D5140C1-7436-11CE-8034-00AA006009FA")]
    private interface IServiceProvider10
    {
        [return: MarshalAs(UnmanagedType.IUnknown)]
        object QueryService(ref Guid service, ref Guid riid);
    }

    // Opaque token we only pass through, never invoking its methods. The real
    // interface is IInspectable-derived, but .NET 8 cannot marshal IInspectable;
    // declaring it as an empty IUnknown marker is safe because QueryInterface
    // still uses this GUID and there are no methods to mis-offset.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("372E1D3B-38D3-42E4-A15B-8AB2B178F513")]
    private interface IApplicationView { }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("1841C6D7-4F9D-42C0-AF41-8747538F10E5")]
    private interface IApplicationViewCollection
    {
        int GetViews(out IntPtr array);
        int GetViewsByZOrder(out IntPtr array);
        int GetViewsByAppUserModelId(string id, out IntPtr array);
        int GetViewForHwnd(IntPtr hwnd, out IApplicationView view);
        // remaining methods unused
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("3F07F4BE-B107-441A-AF0F-39D82529072C")]
    private interface IVirtualDesktop
    {
        bool IsViewVisible(IApplicationView view);
        Guid GetId();
        // remaining methods unused
    }

    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown),
     Guid("53F5CA0B-158F-4124-900C-057158060B27")]
    private interface IVirtualDesktopManagerInternal
    {
        int GetCount();                                                         // slot 0
        void MoveViewToDesktop(IApplicationView view, IVirtualDesktop desktop); // slot 1
        bool CanViewMoveDesktops(IApplicationView view);                        // slot 2
        IVirtualDesktop GetCurrentDesktop();                                    // slot 3
        void GetDesktops(out IntPtr desktops);                                  // slot 4
        [PreserveSig]
        int GetAdjacentDesktop(IVirtualDesktop from, int direction,             // slot 5
                               out IVirtualDesktop desktop);
        void SwitchDesktop(IVirtualDesktop desktop);                            // slot 6
        // Slots 7..18 are declared only to keep the vtable offsets correct so
        // SwitchDesktopWithAnimation lands on slot 19. They are never invoked,
        // so simplified parameter types are fine.
        void SwitchDesktopAndMoveForegroundView(IVirtualDesktop desktop);       // slot 7
        IVirtualDesktop CreateDesktop();                                        // slot 8
        void MoveDesktop(IVirtualDesktop desktop, int nIndex);                  // slot 9
        void RemoveDesktop(IVirtualDesktop desktop, IVirtualDesktop fallback);  // slot 10
        IVirtualDesktop FindDesktop(ref Guid desktopid);                        // slot 11
        void GetDesktopSwitchIncludeExcludeViews(IVirtualDesktop desktop,       // slot 12
                                                 out IntPtr unknown1,
                                                 out IntPtr unknown2);
        void SetDesktopName(IVirtualDesktop desktop, IntPtr name);              // slot 13
        void SetDesktopWallpaper(IVirtualDesktop desktop, IntPtr path);         // slot 14
        void UpdateWallpaperPathForAllDesktops(IntPtr path);                    // slot 15
        void CopyDesktopState(IApplicationView v0, IApplicationView v1);        // slot 16
        void CreateRemoteDesktop(IntPtr path, out IntPtr desktop);              // slot 17
        void SwitchRemoteDesktop(IVirtualDesktop desktop, IntPtr switchtype);   // slot 18
        void SwitchDesktopWithAnimation(IVirtualDesktop desktop);               // slot 19
    }

    private const int LeftDirection = 3;
    private const int RightDirection = 4;

    private static IServiceProvider10? _shell;
    private static IVirtualDesktopManagerInternal? _vdmInternal;
    private static IApplicationViewCollection? _avc;

    private static void EnsureCom()
    {
        if (_vdmInternal != null && _avc != null) return;

        var shellType = Type.GetTypeFromCLSID(CLSID_ImmersiveShell)
            ?? throw new InvalidOperationException("ImmersiveShell CLSID not found");
        _shell = (IServiceProvider10)Activator.CreateInstance(shellType)!;

        Guid svc = CLSID_VirtualDesktopManagerInternal;
        Guid iid = typeof(IVirtualDesktopManagerInternal).GUID;
        _vdmInternal = (IVirtualDesktopManagerInternal)_shell.QueryService(ref svc, ref iid);

        Guid avcIid = typeof(IApplicationViewCollection).GUID;
        Guid avcSvc = avcIid;
        _avc = (IApplicationViewCollection)_shell.QueryService(ref avcSvc, ref avcIid);
    }

    private static void ResetCom()
    {
        _vdmInternal = null;
        _avc = null;
        _shell = null;
    }

    /// <summary>
    /// Moves <paramref name="hwnd"/> to the desktop on the given side and follows
    /// it there. Returns false (with a reason in <paramref name="diag"/>) when
    /// there is no neighbor, no view, or the COM call fails.
    /// </summary>
    public static bool MoveAdjacent(IntPtr hwnd, DesktopDirection dir, out string diag)
        => MoveAdjacent(hwnd, dir, IntPtr.Zero, out diag);

    /// <summary>
    /// As <see cref="MoveAdjacent(IntPtr, DesktopDirection, out string)"/>, but also
    /// carries <paramref name="followHwnd"/> (e.g. the cursor HUD overlay) to the
    /// same target desktop so it stays visible after the switch. The follow is
    /// best-effort and never changes the result for the primary window.
    /// </summary>
    public static bool MoveAdjacent(IntPtr hwnd, DesktopDirection dir, IntPtr followHwnd, out string diag)
    {
        diag = "";
        if (hwnd == IntPtr.Zero) { diag = "no-window"; return false; }

        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                EnsureCom();

                int count = _vdmInternal!.GetCount();
                if (count < 2) { diag = $"only {count} desktop(s)"; return false; }

                int rc = _avc!.GetViewForHwnd(hwnd, out var view);
                if (rc != 0 || view == null)
                {
                    diag = $"no-view hr=0x{rc:X8}";
                    return false;
                }

                var current = _vdmInternal.GetCurrentDesktop();
                int direction = dir == DesktopDirection.Left ? LeftDirection : RightDirection;
                int hr = _vdmInternal.GetAdjacentDesktop(current, direction, out var target);
                if (hr != 0 || target == null)
                {
                    diag = $"no-neighbor dir={dir} hr=0x{hr:X8} count={count}";
                    SafeRelease(current);
                    return false;
                }

                _vdmInternal.MoveViewToDesktop(view, target);

                // Carry the HUD overlay along so it remains on-screen after the
                // switch, enabling another move from the new desktop.
                string followDiag = "";
                if (followHwnd != IntPtr.Zero)
                {
                    try
                    {
                        int frc = _avc.GetViewForHwnd(followHwnd, out var fview);
                        if (frc == 0 && fview != null)
                        {
                            _vdmInternal.MoveViewToDesktop(fview, target);
                            SafeRelease(fview);
                            followDiag = " follow=ok";
                        }
                        else followDiag = $" follow=no-view(0x{frc:X8})";
                    }
                    catch (Exception fex) { followDiag = $" follow=err(0x{fex.HResult:X8})"; }
                }

                _vdmInternal.SwitchDesktopWithAnimation(target);   // follow with native slide
                diag = $"moved(internal,anim) dir={dir} count={count}{followDiag}";

                SafeRelease(view);
                SafeRelease(current);
                SafeRelease(target);
                return true;
            }
            catch (COMException ex) when (attempt == 0)
            {
                // Explorer may have recycled the COM server; rebuild once and retry.
                diag = $"com-retry 0x{ex.HResult:X8}";
                ResetCom();
            }
            catch (Exception ex)
            {
                diag = $"internal-failed {ex.GetType().Name} 0x{ex.HResult:X8} {ex.Message}";
                return false;
            }
        }
        return false;
    }

    /// <summary>
    /// Reports the current virtual-desktop topology: the total number of desktops
    /// and the zero-based index (from the leftmost) of the one currently shown.
    /// Used to render the HUD mini-map. Returns false on COM failure.
    /// </summary>
    public static bool GetLayout(out int count, out int currentIndex, out string diag)
    {
        count = 0; currentIndex = 0; diag = "";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            try
            {
                EnsureCom();
                count = _vdmInternal!.GetCount();

                var current = _vdmInternal.GetCurrentDesktop();
                int idx = 0;
                IVirtualDesktop walker = current;
                var visited = new List<IVirtualDesktop>();
                while (_vdmInternal.GetAdjacentDesktop(walker, LeftDirection, out var prev) == 0 && prev != null)
                {
                    idx++;
                    visited.Add(prev);
                    walker = prev;
                }
                currentIndex = idx;

                SafeRelease(current);
                foreach (var d in visited) SafeRelease(d);
                diag = $"count={count} idx={idx}";
                return true;
            }
            catch (COMException ex) when (attempt == 0)
            {
                diag = $"com-retry 0x{ex.HResult:X8}";
                ResetCom();
            }
            catch (Exception ex)
            {
                diag = $"layout-failed {ex.GetType().Name} 0x{ex.HResult:X8}";
                return false;
            }
        }
        return false;
    }

    private static void SafeRelease(object? o)
    {
        if (o != null && Marshal.IsComObject(o))
        {
            try { Marshal.ReleaseComObject(o); } catch { /* ignore */ }
        }
    }
}
