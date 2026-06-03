using System.Runtime.InteropServices;
using Swoosh.Native;

namespace Swoosh.Input;

/// <summary>
/// Decodes raw Precision Touchpad HID reports into <see cref="TouchFrame"/>s.
/// Caches preparsed data and the per-device report layout (which HID link
/// collections represent fingers, and where the X/Y/contact-id values live).
/// </summary>
public sealed class TouchpadParser
{
    private sealed class DeviceLayout
    {
        public IntPtr Preparsed;
        public int InputReportLength;
        public readonly List<ushort> FingerCollections = new();
        public int LogicalMaxX = 1, LogicalMaxY = 1;
        public int LogicalMinX, LogicalMinY;
        public bool HasContactCount;
        public ushort ContactCountCollection;
    }

    private readonly Dictionary<IntPtr, DeviceLayout?> _devices = new();

    private const int DiagMax = 400;
    private int _diagCount;
    private string _lastSig = "";
    private readonly Dictionary<ushort, (uint x, uint y)> _prevPos = new();

    // Stuck-contact rejection. A real fingertip jitters at the sensor's
    // least-significant bit every few frames; a firmware-stuck phantom reports
    // the exact same raw coordinate forever. Any slot that holds a byte-identical
    // position for longer than StuckMs is treated as a phantom and dropped,
    // independent of the (sometimes-wrong) firmware Contact Count. Any movement
    // instantly un-sticks it.
    private const long StuckMs = 700;
    private readonly Dictionary<ushort, (uint x, uint y, long sinceMs)> _stillSince = new();

    private DeviceLayout? GetLayout(IntPtr device)
    {
        if (_devices.TryGetValue(device, out var cached))
            return cached;

        var layout = BuildLayout(device);
        _devices[device] = layout;
        return layout;
    }

    private static DeviceLayout? BuildLayout(IntPtr device)
    {
        uint size = 0;
        Win32.GetRawInputDeviceInfo(device, Win32.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return null;

        IntPtr preparsed = Marshal.AllocHGlobal((int)size);
        if (Win32.GetRawInputDeviceInfo(device, Win32.RIDI_PREPARSEDDATA, preparsed, ref size) == unchecked((uint)-1))
        {
            Marshal.FreeHGlobal(preparsed);
            return null;
        }

        var caps = new Hid.HIDP_CAPS { Reserved = new ushort[17] };
        if (Hid.HidP_GetCaps(preparsed, ref caps) != Hid.HIDP_STATUS_SUCCESS)
        {
            Marshal.FreeHGlobal(preparsed);
            return null;
        }

        var layout = new DeviceLayout
        {
            Preparsed = preparsed,
            InputReportLength = caps.InputReportByteLength,
        };

        ushort count = caps.NumberInputValueCaps;
        if (count == 0) { Marshal.FreeHGlobal(preparsed); return null; }

        var valueCaps = new Hid.HIDP_VALUE_CAPS[count];
        if (Hid.HidP_GetValueCaps(Hid.HidP_Input, valueCaps, ref count, preparsed) != Hid.HIDP_STATUS_SUCCESS)
        {
            Marshal.FreeHGlobal(preparsed);
            return null;
        }

        // A real finger collection exposes BOTH a Generic-Desktop X value and a
        // Digitizer Contact ID. Requiring Contact ID excludes the touchpad's
        // mouse-pointer collection (which also has X/Y but no contact id) — the
        // usual source of a phantom contact stuck in the center.
        var hasX = new HashSet<ushort>();
        var hasContactId = new HashSet<ushort>();

        foreach (var vc in valueCaps)
        {
            ushort usage = vc.IsRange != 0 ? vc.RangeUsageMin : vc.NotRangeUsage;
            Swoosh.Log.Write($"  valcap page=0x{vc.UsagePage:X2} usage=0x{usage:X2} link={vc.LinkCollection} rid={vc.ReportID} isRange={vc.IsRange} cnt={vc.ReportCount} bits={vc.BitSize} lmin={vc.LogicalMin} lmax={vc.LogicalMax}");
            if (vc.UsagePage == Hid.UP_GENERIC && usage == Hid.USAGE_X)
            {
                hasX.Add(vc.LinkCollection);
                layout.LogicalMinX = vc.LogicalMin;
                layout.LogicalMaxX = vc.LogicalMax;
            }
            else if (vc.UsagePage == Hid.UP_GENERIC && usage == Hid.USAGE_Y)
            {
                layout.LogicalMinY = vc.LogicalMin;
                layout.LogicalMaxY = vc.LogicalMax;
            }
            else if (vc.UsagePage == Hid.UP_DIGITIZER && usage == Hid.USAGE_CONTACT_ID)
            {
                hasContactId.Add(vc.LinkCollection);
            }
            else if (vc.UsagePage == Hid.UP_DIGITIZER && usage == Hid.USAGE_CONTACT_COUNT)
            {
                layout.HasContactCount = true;
                layout.ContactCountCollection = vc.LinkCollection;
            }
        }

        foreach (var col in hasX)
            if (hasContactId.Contains(col))
                layout.FingerCollections.Add(col);

        layout.FingerCollections.Sort();
        Swoosh.Log.Write($"layout: reportLen={layout.InputReportLength} fingerCols=[{string.Join(",", layout.FingerCollections)}] hasContactCount={layout.HasContactCount} ccCol={layout.ContactCountCollection} xRange={layout.LogicalMinX}..{layout.LogicalMaxX} yRange={layout.LogicalMinY}..{layout.LogicalMaxY}");
        if (layout.FingerCollections.Count == 0)
        {
            Marshal.FreeHGlobal(preparsed);
            return null;
        }
        return layout;
    }

    /// <summary>Parse all reports contained in a single WM_INPUT HID payload.</summary>
    public List<TouchFrame> Parse(IntPtr device, byte[] hidData, int sizeHid, int reportCount)
    {
        var frames = new List<TouchFrame>();
        var layout = GetLayout(device);
        if (layout == null || sizeHid == 0) return frames;

        GCHandle handle = GCHandle.Alloc(hidData, GCHandleType.Pinned);
        try
        {
            IntPtr basePtr = handle.AddrOfPinnedObject();
            double spanX = Math.Max(1, layout.LogicalMaxX - layout.LogicalMinX);
            double spanY = Math.Max(1, layout.LogicalMaxY - layout.LogicalMinY);
            var usageBuf = new ushort[16];

            for (int r = 0; r < reportCount; r++)
            {
                IntPtr report = basePtr + r * sizeHid;

                byte reportId = hidData[r * sizeHid];
                uint ccVal = 0;
                bool ccOk = layout.HasContactCount &&
                    Hid.HidP_GetUsageValue(Hid.HidP_Input, Hid.UP_DIGITIZER,
                        layout.ContactCountCollection, Hid.USAGE_CONTACT_COUNT,
                        out ccVal, layout.Preparsed, report, (uint)sizeHid) == Hid.HIDP_STATUS_SUCCESS;

                // Only reports that carry a Contact Count are touch frames. The
                // touchpad also emits mouse/button reports (different report id)
                // where finger usages would decode to stale garbage — skip them.
                if (layout.HasContactCount && !ccOk)
                    continue;

                // Gather every slot that currently claims a finger is down,
                // tracking whether each moved since the previous report.
                var cand = new List<(int id, double nx, double ny, ushort col, bool moved)>();
                long now = Environment.TickCount64;
                int stuckDropped = 0;
                foreach (ushort col in layout.FingerCollections)
                {
                    uint usageLen = (uint)usageBuf.Length;
                    bool tip = false;
                    if (Hid.HidP_GetUsages(Hid.HidP_Input, Hid.UP_DIGITIZER, col, usageBuf,
                            ref usageLen, layout.Preparsed, report, (uint)sizeHid) == Hid.HIDP_STATUS_SUCCESS)
                    {
                        for (int i = 0; i < usageLen; i++)
                            if (usageBuf[i] == Hid.USAGE_TIP_SWITCH) { tip = true; break; }
                    }

                    if (!tip) { _prevPos.Remove(col); _stillSince.Remove(col); continue; }

                    if (Hid.HidP_GetUsageValue(Hid.HidP_Input, Hid.UP_GENERIC, col, Hid.USAGE_X,
                            out uint rawX, layout.Preparsed, report, (uint)sizeHid) != Hid.HIDP_STATUS_SUCCESS)
                        continue;
                    if (Hid.HidP_GetUsageValue(Hid.HidP_Input, Hid.UP_GENERIC, col, Hid.USAGE_Y,
                            out uint rawY, layout.Preparsed, report, (uint)sizeHid) != Hid.HIDP_STATUS_SUCCESS)
                        continue;

                    int id = col;
                    if (Hid.HidP_GetUsageValue(Hid.HidP_Input, Hid.UP_DIGITIZER, col, Hid.USAGE_CONTACT_ID,
                            out uint rawId, layout.Preparsed, report, (uint)sizeHid) == Hid.HIDP_STATUS_SUCCESS)
                        id = (int)rawId;

                    bool moved = !_prevPos.TryGetValue(col, out var prev) || prev.x != rawX || prev.y != rawY;
                    _prevPos[col] = (rawX, rawY);

                    // Stuck-phantom rejection: if this slot has reported the exact
                    // same raw coordinate continuously for longer than StuckMs, it
                    // is a frozen firmware contact, not a real finger. Drop it.
                    bool stuck = false;
                    if (_stillSince.TryGetValue(col, out var ss) && ss.x == rawX && ss.y == rawY)
                    {
                        if (now - ss.sinceMs >= StuckMs) stuck = true;
                    }
                    else
                    {
                        _stillSince[col] = (rawX, rawY, now);
                    }
                    if (stuck) { stuckDropped++; continue; }

                    double nx = Math.Clamp((rawX - layout.LogicalMinX) / spanX, 0, 1);
                    double ny = Math.Clamp((rawY - layout.LogicalMinY) / spanY, 0, 1);
                    cand.Add((id, nx, ny, col, moved));
                }

                // The firmware's Contact Count is the authoritative number of
                // live fingers. Some touchpads (Bradley's Surface included) have
                // a buggy slot whose TipSwitch is permanently stuck on at a
                // frozen position — it is never counted in Contact Count. When
                // more slots claim tip-down than Contact Count allows, drop the
                // stale (non-moving) phantoms, keeping moving contacts first.
                int budget = layout.HasContactCount ? (int)ccVal : cand.Count;
                if (budget < 0) budget = 0;
                string rawDetail = string.Join(" ", cand.Select(c =>
                    $"id{c.id}@({c.nx:F2},{c.ny:F2}){(c.moved ? "M" : "S")}"));
                int rawCount = cand.Count;
                if (cand.Count > budget)
                {
                    var kept = cand.Where(c => c.moved).ToList();
                    if (kept.Count > budget)
                        kept = kept.Take(budget).ToList();
                    else
                        foreach (var c in cand.Where(c => !c.moved).OrderBy(c => c.id))
                        {
                            if (kept.Count >= budget) break;
                            kept.Add(c);
                        }
                    cand = kept;
                }

                if (_diagCount < DiagMax)
                {
                    string sig = $"rid={reportId} cc={ccVal} raw={rawCount} stuck={stuckDropped} kept={cand.Count} [{rawDetail}]";
                    if (sig != _lastSig) { Swoosh.Log.Write(sig); _lastSig = sig; _diagCount++; }
                }

                var frame = new TouchFrame { TimestampMs = Environment.TickCount64 };
                foreach (var c in cand)
                    frame.Contacts.Add(new Contact(c.id, c.nx, c.ny, true));

                frames.Add(frame);
            }
        }
        finally
        {
            handle.Free();
        }
        return frames;
    }
}
