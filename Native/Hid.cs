using System.Runtime.InteropServices;

namespace Swoosh.Native;

/// <summary>
/// P/Invoke surface for the HID parsing library (hid.dll) used to decode
/// Precision Touchpad reports into per-finger contacts.
/// </summary>
internal static class Hid
{
    public const int HidP_Input = 0;

    // HID usage pages / usages relevant to Precision Touchpads.
    public const ushort UP_GENERIC = 0x01;   // Generic Desktop
    public const ushort USAGE_X = 0x30;
    public const ushort USAGE_Y = 0x31;

    public const ushort UP_DIGITIZER = 0x0D;
    public const ushort USAGE_TIP_SWITCH = 0x42;
    public const ushort USAGE_CONTACT_ID = 0x51;
    public const ushort USAGE_CONTACT_COUNT = 0x54;

    [StructLayout(LayoutKind.Sequential)]
    public struct HIDP_CAPS
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InputReportByteLength;
        public ushort OutputReportByteLength;
        public ushort FeatureReportByteLength;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
        public ushort NumberLinkCollectionNodes;
        public ushort NumberInputButtonCaps;
        public ushort NumberInputValueCaps;
        public ushort NumberInputDataIndices;
        public ushort NumberOutputButtonCaps;
        public ushort NumberOutputValueCaps;
        public ushort NumberOutputDataIndices;
        public ushort NumberFeatureButtonCaps;
        public ushort NumberFeatureValueCaps;
        public ushort NumberFeatureDataIndices;
    }

    /// <summary>
    /// Mirrors HIDP_VALUE_CAPS (hidpi.h). Only the fields we read are named;
    /// the trailing union is collapsed to the NotRange.Usage field we need.
    /// Total size 72 bytes.
    /// </summary>
    [StructLayout(LayoutKind.Explicit, Size = 72)]
    public struct HIDP_VALUE_CAPS
    {
        [FieldOffset(0)] public ushort UsagePage;
        [FieldOffset(2)] public byte ReportID;
        [FieldOffset(3)] public byte IsAlias;
        [FieldOffset(4)] public ushort BitField;
        [FieldOffset(6)] public ushort LinkCollection;
        [FieldOffset(8)] public ushort LinkUsage;
        [FieldOffset(10)] public ushort LinkUsagePage;
        [FieldOffset(12)] public byte IsRange;
        [FieldOffset(18)] public ushort BitSize;
        [FieldOffset(20)] public ushort ReportCount;
        [FieldOffset(40)] public int LogicalMin;
        [FieldOffset(44)] public int LogicalMax;
        [FieldOffset(48)] public int PhysicalMin;
        [FieldOffset(52)] public int PhysicalMax;
        // union start at 56. For IsRange == false, NotRange.Usage sits at 56.
        [FieldOffset(56)] public ushort NotRangeUsage;
        [FieldOffset(56)] public ushort RangeUsageMin;
    }

    [DllImport("hid.dll")]
    public static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);

    [DllImport("hid.dll")]
    public static extern int HidP_GetValueCaps(int reportType,
        [Out] HIDP_VALUE_CAPS[] valueCaps, ref ushort valueCapsLength, IntPtr preparsedData);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsageValue(int reportType, ushort usagePage,
        ushort linkCollection, ushort usage, out uint usageValue, IntPtr preparsedData,
        IntPtr report, uint reportLength);

    [DllImport("hid.dll")]
    public static extern int HidP_GetUsages(int reportType, ushort usagePage,
        ushort linkCollection, [Out] ushort[] usageList, ref uint usageLength,
        IntPtr preparsedData, IntPtr report, uint reportLength);

    public const int HIDP_STATUS_SUCCESS = 0x00110000;
}
