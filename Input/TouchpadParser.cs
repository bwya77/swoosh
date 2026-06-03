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

    private const int DiagMax = 1500;
    private int _diagCount;
    private string _lastSig = "";
    private readonly Dictionary<ushort, (uint x, uint y)> _prevPos = new();

    // Freeze tracking: how long a slot has held a byte-identical raw position.
    private readonly Dictionary<ushort, (uint x, uint y, long sinceMs)> _stillSince = new();

    // Phantom rejection (intermittent firmware-stuck contact). This device
    // sometimes wedges one or more finger collections at a fixed raw coordinate
    // that never moves and never lifts — and crucially the pad goes SILENT (stops
    // sending reports) while such a contact sits motionless, so any time-based
    // "frozen for N ms" test never gets the frames it needs to fire. Two
    // discriminators, neither of which can trip during a real gesture:
    //   1. LONE: a sole contact frozen past LearnMs (a real hold always shows two
    //      contacts, so a held finger is never the only thing on the pad).
    //   2. MASS-RELEASE RESIDUE (collection-tracked): every finger collection
    //      seen during a >=3-finger press is remembered; the real fingers lift
    //      (tip-up clears them), but a stuck slot lingers. So any collection still
    //      down after the press collapses to <=2 is the firmware residue — dropped
    //      immediately, no wait on freeze time. Genuine new fingers always arrive
    //      on fresh collections, so later gestures are unaffected.
    // Learned coordinates are also suppressed while frozen; a MOVING contact at a
    // learned spot is a real finger reclaiming it, so we unlearn that position.
    private const long LearnMs = 1200;
    private const uint PhantomTol = 3;
    private readonly List<(uint x, uint y)> _phantoms = new();
    private readonly HashSet<ushort> _peakResidue = new();

    // Highest contact count reached in the current touch sequence (reset on full
    // lift). Lets Rule 2 distinguish "press is collapsing" from "press is at peak"
    // so a steady 2-finger hold is never mistaken for stuck residue.
    private int _peakDown;

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

                // Gather every slot that currently claims a finger is down, with
                // its raw position and how long it has been byte-frozen there.
                var cand = new List<(int id, double nx, double ny, ushort col, bool moved, uint rawX, uint rawY, long frozenMs)>();
                long now = Environment.TickCount64;
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

                    if (!tip) { _prevPos.Remove(col); _stillSince.Remove(col); _peakResidue.Remove(col); continue; }

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

                    // Track how long this slot has held a byte-identical position.
                    long frozenMs = 0;
                    if (_stillSince.TryGetValue(col, out var ss) && ss.x == rawX && ss.y == rawY)
                        frozenMs = now - ss.sinceMs;
                    else
                        _stillSince[col] = (rawX, rawY, now);

                    double nx = Math.Clamp((rawX - layout.LogicalMinX) / spanX, 0, 1);
                    double ny = Math.Clamp((rawY - layout.LogicalMinY) / spanY, 0, 1);
                    cand.Add((id, nx, ny, col, moved, rawX, rawY, frozenMs));
                }

                // Collapse firmware-duplicated contact ids. After a many-finger
                // gesture this pad sometimes leaves two or three finger
                // collections all reporting the SAME contact id with stale
                // positions (the residue that looks like a phantom 2-finger
                // hold). Real simultaneous fingers always carry DISTINCT contact
                // ids, so any duplicate id is firmware garbage: keep a single
                // representative per id (prefer a moving one) and drop the rest.
                if (cand.Count > 1)
                {
                    var byId = new Dictionary<int, int>();
                    var deduped = new List<(int id, double nx, double ny, ushort col, bool moved, uint rawX, uint rawY, long frozenMs)>();
                    foreach (var c in cand)
                    {
                        if (byId.TryGetValue(c.id, out int ki))
                        {
                            if (c.moved && !deduped[ki].moved) deduped[ki] = c;
                        }
                        else
                        {
                            byId[c.id] = deduped.Count;
                            deduped.Add(c);
                        }
                    }
                    cand = deduped;
                }

                // Phantom rejection. See the field comments for the two learning
                // rules. Suppress frozen contacts sitting on a learned coord; a
                // moving contact there reclaims it (unlearn).
                static bool Near(uint a, uint b) => (a > b ? a - b : b - a) <= PhantomTol;
                void Learn(uint x, uint y)
                {
                    if (!_phantoms.Any(p => Near(p.x, x) && Near(p.y, y)))
                    {
                        _phantoms.Add((x, y));
                        if (_phantoms.Count > 8) _phantoms.RemoveAt(0);
                    }
                }

                int curDown = cand.Count;
                if (curDown > _peakDown) _peakDown = curDown;

                // Pad genuinely empty → nothing can be stuck. Wipe all residue and
                // learned phantom coords so they never accumulate across gestures.
                if (curDown == 0)
                {
                    _peakResidue.Clear();
                    _phantoms.Clear();
                    _peakDown = 0;
                }

                // Remember every collection present during a multi-finger (>=2)
                // press. Real fingers get cleared on tip-up (gather loop); a stuck
                // slot never lifts, so it survives in this set.
                if (curDown >= 2)
                    foreach (var c in cand) _peakResidue.Add(c.col);

                // Unlearn any phantom position now occupied by a MOVING contact.
                for (int i = _phantoms.Count - 1; i >= 0; i--)
                {
                    var ph = _phantoms[i];
                    int mi = cand.FindIndex(c => Near(c.rawX, ph.x) && Near(c.rawY, ph.y));
                    if (mi >= 0 && cand[mi].moved) _phantoms.RemoveAt(i);
                }

                // Rule 1 — LONE: a sole frozen contact past LearnMs is a phantom.
                if (curDown == 1 && !cand[0].moved && cand[0].frozenMs >= LearnMs)
                    Learn(cand[0].rawX, cand[0].rawY);

                int dropped = 0;

                // Rule 2 — MASS-RELEASE RESIDUE (collection-tracked). Once a
                // multi-finger press starts collapsing (curDown drops below the
                // peak it reached this sequence), any collection still down that
                // is FROZEN is a stuck firmware slot. We drop it the instant it
                // freezes — no wait on a freeze timer, because the pad goes SILENT
                // on a motionless stuck contact and a longer timer would never
                // receive another frame. Learning its coord makes the suppression
                // durable (the phantom-coord block below kills it every later
                // frame, even after this residue set is reset). The !moved guard is
                // what keeps a real finger that keeps MOVING after its partners lift
                // alive across any transition (5->2, 2->1, ...); only a genuinely
                // stuck slot, which freezes within a frame, is ever dropped.
                if (_peakResidue.Count > 0 && curDown < _peakDown)
                {
                    var kept = new List<(int id, double nx, double ny, ushort col, bool moved, uint rawX, uint rawY, long frozenMs)>();
                    foreach (var c in cand)
                    {
                        if (_peakResidue.Contains(c.col) && !c.moved) { Learn(c.rawX, c.rawY); dropped++; }
                        else kept.Add(c);
                    }
                    cand = kept;
                }

                // Suppress frozen contacts sitting on a learned phantom coord.
                if (_phantoms.Count > 0)
                {
                    int before = cand.Count;
                    cand = cand.Where(c => c.moved ||
                        !_phantoms.Any(p => Near(c.rawX, p.x) && Near(c.rawY, p.y))).ToList();
                    dropped += before - cand.Count;
                }

                // Contact-count clamp. The report's ContactCount is the firmware's
                // own count of fingers actually down. If more collections still
                // claim tip-down than that (the classic "lifted one of two fingers
                // but it still reads down" residue, which the >=3-finger residue
                // rule never sees), the extras are stale slots. Keep the most
                // plausible contacts: moving ones first, then the least-frozen.
                if (ccOk && ccVal < (uint)cand.Count)
                {
                    int before = cand.Count;
                    cand = cand
                        .OrderByDescending(c => c.moved)
                        .ThenBy(c => c.frozenMs)
                        .Take((int)ccVal)
                        .ToList();
                    dropped += before - cand.Count;
                }

                // Post-drop reset. If every contact this frame was dropped as
                // phantom residue (final list empty, yet the raw report still
                // claimed fingers down), the real gesture is over and only stuck
                // slots remain. Clear the residue set and peak so a held-high
                // _peakDown from an earlier 5-finger press can't keep tripping
                // Rule 2 against later 2-finger gestures. Learned phantom coords
                // are kept, so the stuck slot stays suppressed by coordinate.
                if (cand.Count == 0 && curDown > 0)
                {
                    _peakResidue.Clear();
                    _peakDown = 0;
                }

                // Always log the low-count region (post-lift phantoms) and any
                // drop, bypassing the cap — that's the interesting part. Steady
                // high-finger holds respect the cap so they don't flood the log.
                bool interesting = curDown <= 2 || dropped > 0;
                if (interesting || _diagCount < DiagMax)
                {
                    string rawDetail = string.Join(" ", cand.Select(c =>
                        $"id{c.id}@{c.rawX},{c.rawY}({c.nx:F2},{c.ny:F2}){(c.moved ? "M" : "S")}f{c.frozenMs}"));
                    string ghostStr = _phantoms.Count == 0 ? "none"
                        : string.Join("", _phantoms.Select(p => $"({p.x},{p.y})"));
                    string sig = $"rid={reportId} cc={ccVal} res={_peakResidue.Count} ghost={ghostStr} drop={dropped} n={cand.Count} [{rawDetail}]";
                    if (sig != _lastSig)
                    {
                        Swoosh.Log.Write(sig);
                        _lastSig = sig;
                        if (!interesting) _diagCount++;
                    }
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
