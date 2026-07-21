using System.Runtime.InteropServices;

namespace WifiOptimizer.Wlan;

internal static class WlanInterop
{
    public const uint ClientVersion = 2; // Vista+

    [DllImport("wlanapi.dll")]
    public static extern uint WlanOpenHandle(
        uint dwClientVersion, IntPtr pReserved,
        out uint pdwNegotiatedVersion, out IntPtr phClientHandle);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanCloseHandle(IntPtr hClientHandle, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanEnumInterfaces(
        IntPtr hClientHandle, IntPtr pReserved, out IntPtr ppInterfaceList);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanScan(
        IntPtr hClientHandle, ref Guid pInterfaceGuid,
        IntPtr pDot11Ssid, IntPtr pIeData, IntPtr pReserved);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanGetNetworkBssList(
        IntPtr hClientHandle, ref Guid pInterfaceGuid,
        IntPtr pDot11Ssid, Dot11BssType dot11BssType,
        [MarshalAs(UnmanagedType.Bool)] bool bSecurityEnabled,
        IntPtr pReserved, out IntPtr ppWlanBssList);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanGetAvailableNetworkList(
        IntPtr hClientHandle, ref Guid pInterfaceGuid,
        uint dwFlags, IntPtr pReserved, out IntPtr ppAvailableNetworkList);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanQueryInterface(
        IntPtr hClientHandle, ref Guid pInterfaceGuid,
        int opCode, IntPtr pReserved,
        out uint pdwDataSize, out IntPtr ppData, IntPtr pWlanOpcodeValueType);

    public const int OpcodeCurrentConnection = 7; // wlan_intf_opcode_current_connection
    public const uint InterfaceStateConnected = 1;

    public delegate void WlanNotificationCallback(ref WlanNotificationData data, IntPtr context);

    [DllImport("wlanapi.dll")]
    public static extern uint WlanRegisterNotification(
        IntPtr hClientHandle, uint dwNotifSource,
        [MarshalAs(UnmanagedType.Bool)] bool bIgnoreDuplicate,
        WlanNotificationCallback? funcCallback, IntPtr pCallbackContext,
        IntPtr pReserved, out uint pdwPrevNotifSource);

    public const uint NotificationSourceNone = 0x0;
    public const uint NotificationSourceAcm = 0x8;   // auto config module
    public const uint AcmScanComplete = 7;           // wlan_notification_acm_scan_complete
    public const uint AcmScanFail = 8;               // wlan_notification_acm_scan_fail

    [DllImport("wlanapi.dll")]
    public static extern void WlanFreeMemory(IntPtr pMemory);
}

[StructLayout(LayoutKind.Sequential)]
public struct WlanNotificationData
{
    public uint NotificationSource;
    public uint NotificationCode;
    public Guid InterfaceGuid;
    public uint DataSize;
    public IntPtr Data;
}

public enum Dot11BssType
{
    Infrastructure = 1,
    Independent = 2,
    Any = 3,
}

public enum Dot11AuthAlgorithm : uint
{
    Open = 1,
    SharedKey = 2,
    Wpa = 3,
    WpaPsk = 4,
    WpaNone = 5,
    Rsna = 6,
    RsnaPsk = 7,
    Wpa3Enterprise192 = 8,
    Wpa3Sae = 9,
    Owe = 10,
    Wpa3Enterprise = 11,
}

public enum Dot11CipherAlgorithm : uint
{
    None = 0x00,
    Wep40 = 0x01,
    Tkip = 0x02,
    Ccmp = 0x04,
    Wep104 = 0x05,
    Bip = 0x06,
    Gcmp = 0x08,
    Gcmp256 = 0x09,
    Ccmp256 = 0x0a,
    WpaUseGroup = 0x100,
    Wep = 0x101,
}

[StructLayout(LayoutKind.Sequential)]
public struct Dot11Ssid
{
    public uint SsidLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 32)]
    public byte[] Ssid;

    public override readonly string ToString() =>
        SsidLength == 0 ? "" : System.Text.Encoding.UTF8.GetString(Ssid, 0, (int)SsidLength);
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WlanInterfaceInfo
{
    public Guid InterfaceGuid;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string InterfaceDescription;
    public uint State;
}

[StructLayout(LayoutKind.Sequential)]
public struct WlanRateSet
{
    public uint RateSetLength;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 126)]
    public ushort[] RateSet;
}

[StructLayout(LayoutKind.Sequential)]
public struct WlanBssEntry
{
    public Dot11Ssid Ssid;
    public uint PhyId;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
    public byte[] Bssid;
    public Dot11BssType BssType;
    public uint PhyType;
    public int Rssi;
    public uint LinkQuality;
    public byte InRegDomain;
    public ushort BeaconPeriod;
    public ulong Timestamp;
    public ulong HostTimestamp;
    public ushort CapabilityInformation;
    public uint ChCenterFrequencyKhz;
    public WlanRateSet RateSet;
    public uint IeOffset;
    public uint IeSize;
}

[StructLayout(LayoutKind.Sequential)]
public struct WlanAssociationAttributes
{
    public Dot11Ssid Ssid;
    public Dot11BssType BssType;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 6)]
    public byte[] Bssid;
    public uint PhyType;
    public uint PhyIndex;
    public uint SignalQuality;
    public uint RxRate;
    public uint TxRate;
}

[StructLayout(LayoutKind.Sequential)]
public struct WlanSecurityAttributes
{
    [MarshalAs(UnmanagedType.Bool)] public bool SecurityEnabled;
    [MarshalAs(UnmanagedType.Bool)] public bool OneXEnabled;
    public Dot11AuthAlgorithm AuthAlgorithm;
    public Dot11CipherAlgorithm CipherAlgorithm;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WlanConnectionAttributes
{
    public uint State;
    public uint ConnectionMode;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string ProfileName;
    public WlanAssociationAttributes Association;
    public WlanSecurityAttributes Security;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
public struct WlanAvailableNetwork
{
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
    public string ProfileName;
    public Dot11Ssid Ssid;
    public Dot11BssType BssType;
    public uint NumberOfBssids;
    [MarshalAs(UnmanagedType.Bool)]
    public bool NetworkConnectable;
    public uint NotConnectableReason;
    public uint NumberOfPhyTypes;
    [MarshalAs(UnmanagedType.ByValArray, SizeConst = 8)]
    public uint[] PhyTypes;
    [MarshalAs(UnmanagedType.Bool)]
    public bool MorePhyTypes;
    public uint SignalQuality;
    [MarshalAs(UnmanagedType.Bool)]
    public bool SecurityEnabled;
    public Dot11AuthAlgorithm AuthAlgorithm;
    public Dot11CipherAlgorithm CipherAlgorithm;
    public uint Flags;
    public uint Reserved;
}
