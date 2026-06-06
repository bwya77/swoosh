using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using Swoosh.Native;

namespace Swoosh;

/// <summary>
/// Collects a human-readable diagnostics report (app/OS info plus the detected Precision
/// Touchpad layout) and writes it to the shared settings folder so the Settings app can offer
/// a one-click "Copy diagnostics". Also records whether a usable PTP digitizer was found, which
/// drives the "no Precision Touchpad detected" notice. Read-only device probing — it never
/// touches the live input hot path.
/// </summary>
public static class Diagnostics
{
    /// <summary>True if the most recent <see cref="Build"/> found a usable Precision Touchpad.</summary>
    public static bool TouchpadDetected { get; private set; }

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Swoosh");
    public static string FilePath => Path.Combine(Dir, "diagnostics.txt");

    /// <summary>Build the report and write it to the shared diagnostics file. Best-effort.</summary>
    public static void WriteStartupReport()
    {
        try
        {
            var report = Build();
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, report);
        }
        catch { /* diagnostics are best-effort */ }
    }

    /// <summary>Compose the full diagnostics report as text.</summary>
    public static string Build()
    {
        var sb = new StringBuilder();
        sb.AppendLine("Swoosh diagnostics");
        sb.AppendLine($"Generated:    {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        var ver = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (string.IsNullOrEmpty(ver))
            ver = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";
        sb.AppendLine($"Version:      {ver}");
        sb.AppendLine($"OS:           {Environment.OSVersion.Version}");
        sb.AppendLine($"Architecture: OS {RuntimeInformation.OSArchitecture}, process {RuntimeInformation.ProcessArchitecture}");
        sb.AppendLine($".NET:         {Environment.Version}");
        sb.AppendLine();

        bool detected = false;
        int touchpadCount = 0;
        try
        {
            foreach (var dev in EnumerateHidDevices())
            {
                if (dev.usagePage != Hid.UP_DIGITIZER || dev.usage != 0x05) continue; // 0x0D/0x05 = Touch Pad
                touchpadCount++;
                sb.AppendLine($"Touchpad #{touchpadCount}: VID_{dev.vendorId:X4} PID_{dev.productId:X4} (usagePage=0x0D usage=0x05)");
                var (text, usable) = ProbeLayout(dev.handle);
                sb.AppendLine(text);
                if (usable) detected = true;
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            sb.AppendLine($"Device enumeration error: {ex.Message}");
            sb.AppendLine();
        }

        if (touchpadCount == 0)
            sb.AppendLine("No HID Precision Touchpad device (usagePage 0x0D, usage 0x05) was found.");

        sb.AppendLine();
        sb.AppendLine(detected
            ? "RESULT: Precision Touchpad DETECTED. Gestures should work."
            : "RESULT: No usable Precision Touchpad detected. Swoosh needs a Windows Precision Touchpad; external mice and older (non-precision) touchpads are not supported.");

        TouchpadDetected = detected;
        return sb.ToString();
    }

    private readonly record struct HidDevice(IntPtr handle, ushort usagePage, ushort usage, uint vendorId, uint productId);

    /// <summary>Enumerate all raw-input HID devices and read each one's top-level usage page,
    /// usage, and VID/PID. Works without having registered for input.</summary>
    private static IEnumerable<HidDevice> EnumerateHidDevices()
    {
        uint count = 0;
        uint listSize = (uint)Marshal.SizeOf<Win32.RAWINPUTDEVICELIST>();
        if (Win32.GetRawInputDeviceList(null, ref count, listSize) == unchecked((uint)-1) || count == 0)
            yield break;

        var list = new Win32.RAWINPUTDEVICELIST[count];
        uint got = Win32.GetRawInputDeviceList(list, ref count, listSize);
        if (got == unchecked((uint)-1)) yield break;

        for (int i = 0; i < got; i++)
        {
            if (list[i].dwType != Win32.RIM_TYPEHID) continue;
            if (TryReadDeviceInfo(list[i].hDevice, out var info))
                yield return info;
        }
    }

    // RID_DEVICE_INFO layout: cbSize(0), dwType(4), then the HID union at offset 8:
    // dwVendorId(8), dwProductId(12), dwVersionNumber(16), usUsagePage(20), usUsage(22).
    private static bool TryReadDeviceInfo(IntPtr handle, out HidDevice device)
    {
        device = default;
        const int bufSize = 32; // >= sizeof(RID_DEVICE_INFO)
        IntPtr buf = Marshal.AllocHGlobal(bufSize);
        try
        {
            Marshal.WriteInt32(buf, 0, bufSize);               // cbSize
            uint cb = bufSize;
            uint r = Win32.GetRawInputDeviceInfo(handle, Win32.RIDI_DEVICEINFO, buf, ref cb);
            if (r == unchecked((uint)-1) || r == 0) return false;

            uint type = (uint)Marshal.ReadInt32(buf, 4);       // dwType
            if (type != Win32.RIM_TYPEHID) return false;

            uint vid = (uint)Marshal.ReadInt32(buf, 8);
            uint pid = (uint)Marshal.ReadInt32(buf, 12);
            ushort usagePage = (ushort)Marshal.ReadInt16(buf, 20);
            ushort usage = (ushort)Marshal.ReadInt16(buf, 22);
            device = new HidDevice(handle, usagePage, usage, vid, pid);
            return true;
        }
        finally
        {
            Marshal.FreeHGlobal(buf);
        }
    }

    /// <summary>Probe a device's HID report descriptor and summarize whether it exposes the
    /// finger collections Swoosh needs (Generic-Desktop X/Y plus a Digitizer Contact ID).</summary>
    private static (string text, bool usable) ProbeLayout(IntPtr device)
    {
        uint size = 0;
        Win32.GetRawInputDeviceInfo(device, Win32.RIDI_PREPARSEDDATA, IntPtr.Zero, ref size);
        if (size == 0) return ("  (no preparsed data available)", false);

        IntPtr preparsed = Marshal.AllocHGlobal((int)size);
        try
        {
            if (Win32.GetRawInputDeviceInfo(device, Win32.RIDI_PREPARSEDDATA, preparsed, ref size) == unchecked((uint)-1))
                return ("  (could not read preparsed data)", false);

            var caps = new Hid.HIDP_CAPS { Reserved = new ushort[17] };
            if (Hid.HidP_GetCaps(preparsed, ref caps) != Hid.HIDP_STATUS_SUCCESS)
                return ("  (HidP_GetCaps failed)", false);

            ushort count = caps.NumberInputValueCaps;
            if (count == 0) return ("  (no input value caps)", false);

            var valueCaps = new Hid.HIDP_VALUE_CAPS[count];
            if (Hid.HidP_GetValueCaps(Hid.HidP_Input, valueCaps, ref count, preparsed) != Hid.HIDP_STATUS_SUCCESS)
                return ("  (HidP_GetValueCaps failed)", false);

            var hasX = new HashSet<ushort>();
            var hasContactId = new HashSet<ushort>();
            bool hasContactCount = false;
            int minX = 0, maxX = 0, minY = 0, maxY = 0;

            foreach (var vc in valueCaps)
            {
                ushort usage = vc.IsRange != 0 ? vc.RangeUsageMin : vc.NotRangeUsage;
                if (vc.UsagePage == Hid.UP_GENERIC && usage == Hid.USAGE_X) { hasX.Add(vc.LinkCollection); minX = vc.LogicalMin; maxX = vc.LogicalMax; }
                else if (vc.UsagePage == Hid.UP_GENERIC && usage == Hid.USAGE_Y) { minY = vc.LogicalMin; maxY = vc.LogicalMax; }
                else if (vc.UsagePage == Hid.UP_DIGITIZER && usage == Hid.USAGE_CONTACT_ID) hasContactId.Add(vc.LinkCollection);
                else if (vc.UsagePage == Hid.UP_DIGITIZER && usage == Hid.USAGE_CONTACT_COUNT) hasContactCount = true;
            }

            int fingerCols = hasX.Count(c => hasContactId.Contains(c));
            bool usable = fingerCols > 0;

            var sb = new StringBuilder();
            sb.AppendLine($"  Report length:     {caps.InputReportByteLength} bytes");
            sb.AppendLine($"  Finger collections: {fingerCols}");
            sb.AppendLine($"  Contact count:      {(hasContactCount ? "yes" : "no")}");
            sb.AppendLine($"  X range:            {minX}..{maxX}");
            sb.Append($"  Y range:            {minY}..{maxY}");
            return (sb.ToString(), usable);
        }
        finally
        {
            Marshal.FreeHGlobal(preparsed);
        }
    }
}
